using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using Asparagus.Sdk.Contracts;
using Asparagus.Sdk.Internal.Capture;
using Asparagus.Sdk.Internal.Encoding;
using Asparagus.Sdk.Internal.Network;
using Asparagus.Sdk.Internal.Pipeline;
using Asparagus.Sdk.Internal.Storage;
using Asparagus.Sdk.Internal.Time;
using Asparagus.Sdk.Internal.Tracking;

namespace Asparagus.Sdk.Internal.Runtime
{
    public sealed class AsparagusRuntimeOptions
    {
        public SdkVersion SdkVersion = new SdkVersion(1, 1, 0);
        public string StorageBaseDirectory;
        public IClockSource ClockSource;
        public IHttpTransport HttpTransport;
        public IMotionCaptureSource MotionSource;
        public ITouchCaptureSource TouchSource;
        public IScreenInfoProvider ScreenInfoProvider;
        public ISystemStateProvider SystemStateProvider;
        public IDeviceMetadataProvider DeviceMetadataProvider;
        public RetryBackoffPolicy RetryPolicy;
        public bool RunWorkerSynchronouslyForTests;
        public bool AutoLoadDiskQueueOnStart = true;
    }

    /// <summary>
    /// Pure-C# end-to-end Asparagus runtime engine coordinating clocks, state, events/spans,
    /// native/Unity sensor capture, system state monitoring, batch windowing, background disk
    /// persistence, network transport, and lifecycle transitions.
    /// </summary>
    public sealed class AsparagusRuntime : IEventSink, IDisposable
    {
        private sealed class MotionDrainAdapter : IMotionSampleConsumer
        {
            private readonly AsparagusRuntime _owner;

            public MotionDrainAdapter(AsparagusRuntime owner)
            {
                _owner = owner;
            }

            public void OnMotionSample(in RawMotionSample sample)
            {
                _owner._windowCoordinator.IngestMotionSample(in sample, _owner._onBatchClosedAction);
            }
        }

        private sealed class TouchDrainAdapter : ITouchSampleConsumer
        {
            private readonly AsparagusRuntime _owner;

            public TouchDrainAdapter(AsparagusRuntime owner)
            {
                _owner = owner;
            }

            public void OnTouchSample(in RawTouchSample sample)
            {
                _owner._windowCoordinator.IngestTouchSample(in sample, _owner._onBatchClosedAction);
            }
        }

        private readonly SdkVersion _sdkVersion;
        private readonly IClockSource _clockSource;
        private readonly SessionClock _sessionClock;
        private readonly WindowCallOrder _callOrder;
        private readonly DiagnosticRecorder _diagnostics;
        private readonly StateTracker _stateTracker;
        private readonly EventAndSpanTracker _eventTracker;
        private readonly MotionWindowAggregator _motionAggregator;
        private readonly TouchWindowAggregator _touchAggregator;
        private readonly IScreenInfoProvider _screenInfoProvider;
        private readonly ScreenTracker _screenTracker;
        private readonly IMotionCaptureSource _motionSource;
        private readonly CompositeMotionCaptureCoordinator _motionCoordinator;
        private readonly ITouchCaptureSource _touchSource;
        private readonly SystemStateMonitor _systemStateMonitor;
        private readonly IDeviceMetadataProvider _deviceMetadataProvider;
        private readonly DiskBatchQueue _diskQueue;
        private readonly CollectionHistoryStore _historyStore;
        private readonly BatchWindowCoordinator _windowCoordinator;
        private readonly AsparagusWorker _worker;
        private readonly IHttpTransport _httpTransport;
        private readonly AsparagusTransportCoordinator _transport;
        private readonly CoreEventSender _coreEventSender;

        // Pre-cached delegates and adapters -> 0 bytes allocated per frame in Tick()
        private readonly Action<Batch> _onBatchClosedAction;
        private readonly Action<Batch> _recycleBatchAction;
        private readonly MotionDrainAdapter _motionDrainAdapter;
        private readonly TouchDrainAdapter _touchDrainAdapter;

        // Reusable scratch dictionary for FireEvent legacy shim
        private readonly object _fireEventLock = new object();
        private readonly Dictionary<string, Value> _fireEventScratch = new Dictionary<string, Value>(8, StringComparer.Ordinal);
        private readonly Dictionary<string, Value> _lastSessionProperties = new Dictionary<string, Value>(8, StringComparer.Ordinal);

        private DeviceMetadataSnapshot _lastDeviceMetadata;
        private string _lastControlPlaneBaseUrl = string.Empty;
        private string _lastApiKey = string.Empty;
        private string _lastAppVersion;
        private string _lastBuildNumber;
        private bool _lastDemo;

        // CollectionHistory accounting (asparagus-contract-v7, Decisions 160-161)
        private bool _historyInitializedFromWorker;
        private uint _baseSessionsBefore;
        private ulong _baseCollectedSecondsBefore;
        private uint _baseLongestSessionSecondsBefore;
        private bool _hasActiveControlPlaneSession;
        private long _currentSessionCollectedUs;
        private CollectionHistory _activeSessionSentHistory;

        private bool _telemetryStoppedByGame;
        private bool _hasTelemetryStopSessionUs;
        private long _telemetryStopSessionUs;
        private bool _isPaused;
        private bool _userTouchPaused;
#if UNITY_5_3_OR_NEWER
        private bool _keyboardTouchPaused;
#endif
        private bool _isShutdown;

        public AsparagusRuntime(AsparagusRuntimeOptions options = null)
        {
            AsparagusRuntimeOptions opt = options ?? new AsparagusRuntimeOptions();
            _sdkVersion = opt.SdkVersion;

            _clockSource = opt.ClockSource ?? new SystemClockSource();
            _sessionClock = new SessionClock(_clockSource);
            _callOrder = new WindowCallOrder();
            _diagnostics = new DiagnosticRecorder();
            _stateTracker = new StateTracker(_callOrder, null, _diagnostics);
            _eventTracker = new EventAndSpanTracker(_callOrder);
            _motionAggregator = new MotionWindowAggregator();
            _touchAggregator = new TouchWindowAggregator();
            _screenInfoProvider = opt.ScreenInfoProvider ?? new UnityScreenInfoProvider();
            _screenTracker = new ScreenTracker(_screenInfoProvider);

            INativeMotionBridge createdBridge = null;
            if (opt.MotionSource != null)
            {
                _motionSource = opt.MotionSource;
                _motionCoordinator = opt.MotionSource as CompositeMotionCaptureCoordinator;
            }
            else
            {
                createdBridge = UnityCaptureSourceFactory.CreatePlatformNativeBridge();
                _motionCoordinator = UnityCaptureSourceFactory.CreatePlatformMotionCoordinator(_clockSource, _diagnostics, createdBridge);
                _motionSource = _motionCoordinator;
            }

            if (_clockSource is SystemClockSource systemClock && _motionSource is ISensorClockProvider sensorClockProvider)
            {
                systemClock.SetSensorClockProvider(sensorClockProvider);
            }

            _touchSource = opt.TouchSource ?? UnityCaptureSourceFactory.CreateTouchSource(_clockSource);

            ISystemStateProvider sysStateProvider = opt.SystemStateProvider
                ?? UnityCaptureSourceFactory.CreatePlatformSystemStateProvider(createdBridge);
            _systemStateMonitor = new SystemStateMonitor(sysStateProvider, _stateTracker);
            _deviceMetadataProvider = opt.DeviceMetadataProvider ?? new UnityDeviceMetadataProvider();

            string storageRoot = ResolveStorageDirectory(opt.StorageBaseDirectory);
            _diskQueue = new DiskBatchQueue(storageRoot);
            _historyStore = new CollectionHistoryStore(storageRoot);
            _windowCoordinator = new BatchWindowCoordinator(
                _clockSource,
                _sessionClock,
                _callOrder,
                _stateTracker,
                _eventTracker,
                _motionAggregator,
                _touchAggregator,
                _screenTracker,
                _diagnostics,
                _motionCoordinator,
                _diskQueue);

            _worker = new AsparagusWorker(_diskQueue, opt.RunWorkerSynchronouslyForTests, _historyStore);
            _httpTransport = opt.HttpTransport ?? new UnityWebRequestHttpTransport();
            _transport = new AsparagusTransportCoordinator(
                _httpTransport,
                _worker,
                _sessionClock,
                _clockSource,
                opt.RetryPolicy,
                _diagnostics);

            _coreEventSender = new CoreEventSender(this);

            _onBatchClosedAction = HandleBatchClosed;
            _recycleBatchAction = _windowCoordinator.RecycleBatch;
            _motionDrainAdapter = new MotionDrainAdapter(this);
            _touchDrainAdapter = new TouchDrainAdapter(this);

            _transport.HistoryProvider = GetHistoryForNextSessionStart;
            _transport.OnBeforeSessionActivated = HandleBeforeSessionActivated;
            _transport.OnSessionStarted = HandleSessionStarted;

            if (opt.AutoLoadDiskQueueOnStart)
            {
                _worker.EnqueueStartupLoad(_clockSource.WallUtcMs);
            }
        }

        public CoreEventSender SendCoreEvent
        {
            get { return _coreEventSender; }
        }

        public bool IsShutdown
        {
            get { return _isShutdown; }
        }

        public long CurrentUnityTimeUs
        {
            get
            {
                try
                {
                    return _clockSource != null ? _clockSource.UnityTimeUs : 0L;
                }
                catch
                {
                    return 0L;
                }
            }
        }

        public AsparagusTransportCoordinator Transport
        {
            get { return _transport; }
        }

        public BatchWindowCoordinator WindowCoordinator
        {
            get { return _windowCoordinator; }
        }

        public DiskBatchQueue DiskQueue
        {
            get { return _diskQueue; }
        }

        public AsparagusWorker Worker
        {
            get { return _worker; }
        }

        public bool StartSession(
            string apiKey,
            IReadOnlyDictionary<string, Value> sessionProperties = null,
            string appVersion = null,
            string buildNumber = null,
            bool demo = false)
        {
            return StartSession(
                AsparagusTransportCoordinator.DefaultControlPlaneBaseUrl,
                apiKey,
                sessionProperties,
                appVersion,
                buildNumber,
                demo);
        }

        public bool StartSession(
            string controlPlaneBaseUrl,
            string apiKey,
            IReadOnlyDictionary<string, Value> sessionProperties = null,
            string appVersion = null,
            string buildNumber = null,
            bool demo = false)
        {
            try
            {
                if (_isShutdown || string.IsNullOrEmpty(controlPlaneBaseUrl) || string.IsNullOrEmpty(apiKey))
                {
                    return false;
                }

                _lastControlPlaneBaseUrl = controlPlaneBaseUrl;
                _lastApiKey = apiKey;
                _lastAppVersion = appVersion;
                _lastBuildNumber = buildNumber;
                _lastDemo = demo;

                _lastSessionProperties.Clear();
                if (sessionProperties != null)
                {
                    foreach (KeyValuePair<string, Value> kv in sessionProperties)
                    {
                        if (!string.IsNullOrEmpty(kv.Key))
                        {
                            _lastSessionProperties[kv.Key] = kv.Value;
                        }
                    }
                }

                DeviceMetadataSnapshot meta = CollectDeviceMetadata(appVersion, buildNumber);
                _lastDeviceMetadata = meta;

                SessionStartRequest req = new SessionStartRequest
                {
                    ApiKey = apiKey,
                    DeviceIdentifier = meta.DeviceIdentifier,
                    Platform = meta.Platform,
                    Device = meta.DeviceProperties,
                    AppVersion = meta.AppVersion,
                    BuildNumber = meta.BuildNumber,
                    Demo = demo
                };
                req.SetSdkVersion(_sdkVersion);

                foreach (KeyValuePair<string, Value> kv in _lastSessionProperties)
                {
                    req.SessionProperties[kv.Key] = kv.Value;
                }

                return _transport.StartSessionViaControlPlane(controlPlaneBaseUrl, req);
            }
            catch
            {
                return false;
            }
        }

        public bool StartSessionWithStageOverride(StageBootstrapConfig stageConfig)
        {
            try
            {
                if (_isShutdown || stageConfig == null)
                {
                    return false;
                }

                _lastControlPlaneBaseUrl = string.Empty;
                _lastApiKey = string.Empty;
                _lastDemo = false;
                _lastDeviceMetadata = CollectDeviceMetadata(null, null);

                return _transport.StartSessionWithStageOverride(stageConfig);
            }
            catch
            {
                return false;
            }
        }

        public void UpdateStageToken(string newToken)
        {
            try
            {
                if (_isShutdown) return;
                _transport.UpdateStageToken(newToken);
            }
            catch
            {
            }
        }

        public void StopSession()
        {
            try
            {
                if (_isShutdown) return;

                if (_windowCoordinator.IsSessionActive)
                {
                    DrainCaptureSourcesIfRunning();
                    StopCaptureSources();

                    if (_windowCoordinator.CloseCurrentWindow(CloseReason.SessionEnded, out Batch finalBatch))
                    {
                        HandleBatchClosed(finalBatch);
                    }
                    _windowCoordinator.EndSession();
                }
                else
                {
                    StopCaptureSources();
                }

                _hasTelemetryStopSessionUs = false;
                FoldCompletedControlPlaneSession(persistToDisk: true);
                _transport.StopSession();
            }
            catch
            {
            }
        }

        public void StopTelemetry()
        {
            try
            {
                if (_isShutdown || _telemetryStoppedByGame)
                {
                    return;
                }

                _telemetryStoppedByGame = true;

                if (_windowCoordinator.IsSessionActive && _transport.IsCollectingEnabled)
                {
                    _hasTelemetryStopSessionUs = true;
                    _telemetryStopSessionUs = _sessionClock.CurrentSessionUs;

                    DrainCaptureSourcesIfRunning();
                    StopCaptureSources();

                    _windowCoordinator.RecordLifecycle(new LifecycleRecord(_clockSource.UnityTimeUs, LifecycleKind.CaptureStoppedByGame));
                    if (_windowCoordinator.CloseCurrentWindow(CloseReason.CaptureStopped, out Batch closed))
                    {
                        HandleBatchClosed(closed);
                    }
                }
                else
                {
                    StopCaptureSources();
                }
            }
            catch
            {
            }
        }

        public void StartTelemetry()
        {
            try
            {
                if (_isShutdown || !_telemetryStoppedByGame)
                {
                    return;
                }

                _telemetryStoppedByGame = false;

                if (_windowCoordinator.IsSessionActive && _transport.IsCollectingEnabled)
                {
                    long nowSessionUs = _sessionClock.CurrentSessionUs;
                    long nowUnityUs = _clockSource.UnityTimeUs;

                    if (_hasTelemetryStopSessionUs)
                    {
                        long startUs = _telemetryStopSessionUs;
                        long endUs = nowSessionUs >= startUs ? nowSessionUs : startUs;
                        _windowCoordinator.RecordGap(new GapRecord
                        {
                            Signal = GapSignal.Everything,
                            Clock = Clock.Session,
                            StartTimeUs = startUs,
                            EndTimeUs = endUs,
                            Cause = GapCause.CaptureStopped
                        }, _onBatchClosedAction);
                        _hasTelemetryStopSessionUs = false;
                    }

                    _windowCoordinator.RecordLifecycle(new LifecycleRecord(nowUnityUs, LifecycleKind.CaptureStartedByGame));

                    if (!_isPaused)
                    {
                        StartCaptureSources(_transport.ActiveSettings);
                    }
                }
            }
            catch
            {
            }
        }

        public void SetTouchPaused(bool paused)
        {
            try
            {
                if (_isShutdown) return;

                _userTouchPaused = paused;
                ApplyEffectiveTouchPaused();
            }
            catch
            {
            }
        }

        public void SendEvent(string eventName, IReadOnlyDictionary<string, Value> eventData = null, string span = null)
        {
            try
            {
                if (!CanRecordGameData() || string.IsNullOrEmpty(eventName))
                {
                    return;
                }

                _windowCoordinator.RecordEvent(eventName, eventData, false, null, span, _onBatchClosedAction);
            }
            catch
            {
            }
        }

        public void SendOutcomeEvent(string eventName, string outcome, IReadOnlyDictionary<string, Value> eventData = null, string span = null)
        {
            try
            {
                if (!CanRecordGameData() || string.IsNullOrEmpty(eventName))
                {
                    return;
                }

                _windowCoordinator.RecordEvent(eventName, eventData, true, outcome ?? string.Empty, span, _onBatchClosedAction);
            }
            catch
            {
            }
        }

        public void SetAttributes(IReadOnlyDictionary<string, Value> attributes)
        {
            try
            {
                if (!CanRecordGameData())
                {
                    return;
                }

                if (_stateTracker.SetAttributes(attributes, _clockSource.UnityTimeUs))
                {
                    _windowCoordinator.NoteEstimatedStateBytes(attributes != null ? attributes.Count * 48 : 32);
                    if (_windowCoordinator.EstimatedUnzippedBytes >= BatchWindowCoordinator.ProactiveMaxEstimatedUnzippedBytes)
                    {
                        ForceCloseCurrentWindow(CloseReason.WindowFull);
                    }
                }
            }
            catch
            {
            }
        }

        public void SetAttribute(string key, Value value)
        {
            try
            {
                if (!CanRecordGameData() || string.IsNullOrEmpty(key))
                {
                    return;
                }

                if (_stateTracker.SetAttribute(key, value, _clockSource.UnityTimeUs))
                {
                    int extra = key.Length + (value.Kind == ValueKind.Text ? value.TextValue.Length * 2 : 16);
                    _windowCoordinator.NoteEstimatedStateBytes(extra);
                    if (_windowCoordinator.EstimatedUnzippedBytes >= BatchWindowCoordinator.ProactiveMaxEstimatedUnzippedBytes)
                    {
                        ForceCloseCurrentWindow(CloseReason.WindowFull);
                    }
                }
            }
            catch
            {
            }
        }

        public void RemoveAttribute(string key)
        {
            try
            {
                if (!CanRecordGameData() || string.IsNullOrEmpty(key))
                {
                    return;
                }

                _stateTracker.RemoveAttribute(key, _clockSource.UnityTimeUs);
            }
            catch
            {
            }
        }

        public void ClearAttributes()
        {
            try
            {
                if (!CanRecordGameData())
                {
                    return;
                }

                _stateTracker.ClearAttributes(_clockSource.UnityTimeUs);
            }
            catch
            {
            }
        }

        public void PersonalDataRequested(string jsonBody, Action<bool, long> callback)
        {
            try
            {
                if (_isShutdown)
                {
                    SafeInvokeCompliance(callback, false, 0L);
                    return;
                }

                _transport.SendPersonalDataAccessRequest(jsonBody, callback);
            }
            catch
            {
                SafeInvokeCompliance(callback, false, 0L);
            }
        }

        public void PersonalDataDeletionRequested(string jsonBody, Action<bool, long> callback)
        {
            try
            {
                if (_isShutdown)
                {
                    SafeInvokeCompliance(callback, false, 0L);
                    return;
                }

                _transport.SendPersonalDataForgetRequest(jsonBody, callback);
            }
            catch
            {
                SafeInvokeCompliance(callback, false, 0L);
            }
        }

        [Obsolete("Turnip activity prediction is not supported in Asparagus v3. Always returns not available with zero network calls.")]
        public void RequestActivity(Action<bool, string> callback = null)
        {
            if (callback == null) return;
            try
            {
                callback(false, "not available");
            }
            catch
            {
            }
        }

        [Obsolete("Turnip affect prediction is not supported in Asparagus v3. Always returns not available with zero network calls.")]
        public void RequestAffect(Action<bool, float, string> callback = null)
        {
            if (callback == null) return;
            try
            {
                callback(false, 0f, "not available");
            }
            catch
            {
            }
        }

        [Obsolete("Use SendEvent or SendCoreEvent instead. Forwards directly into the Asparagus event pipeline with zero Turnip network calls.")]
        public void FireEvent(string eventName, IReadOnlyDictionary<string, Value> eventData = null, string source = null)
        {
            try
            {
                if (string.IsNullOrEmpty(eventName))
                {
                    return;
                }

                if (string.IsNullOrEmpty(source))
                {
                    SendEvent(eventName, eventData, null);
                    return;
                }

                lock (_fireEventLock)
                {
                    _fireEventScratch.Clear();
                    if (eventData != null)
                    {
                        foreach (KeyValuePair<string, Value> kv in eventData)
                        {
                            if (!string.IsNullOrEmpty(kv.Key))
                            {
                                _fireEventScratch[kv.Key] = kv.Value;
                            }
                        }
                    }

                    if (!_fireEventScratch.ContainsKey("source"))
                    {
                        _fireEventScratch["source"] = Value.FromText(source);
                    }

                    SendEvent(eventName, _fireEventScratch, null);
                    _fireEventScratch.Clear();
                }
            }
            catch
            {
            }
        }

        /// <summary>
        /// Main-thread per-frame pump. Allocates zero closure objects.
        /// </summary>
        public void Tick()
        {
            try
            {
                if (_isShutdown)
                {
                    return;
                }

                long unityUs = _clockSource.UnityTimeUs;
                _worker.DrainRecycledBatches(_recycleBatchAction);
                _worker.DrainWorkerDiagnostics(_diagnostics, unityUs);

                if (_windowCoordinator.IsSessionActive
                    && _transport.IsCollectingEnabled
                    && !_telemetryStoppedByGame
                    && !_isPaused)
                {
                    UpdateKeyboardTouchPause();
                    DrainCaptureSourcesIfRunning(unityUs);
                    _systemStateMonitor.Poll(unityUs);
                    _windowCoordinator.Tick(_onBatchClosedAction);
                }

                _transport.Tick(_clockSource.SteadyTimeUs / 1000L);
            }
            catch
            {
                // Total fault containment: Tick never throws into Unity Update loop.
            }
        }

        public void OnApplicationPause(bool paused)
        {
            try
            {
                if (_isShutdown || paused == _isPaused)
                {
                    return;
                }

                long unityUs = _clockSource.UnityTimeUs;

                if (paused)
                {
                    _isPaused = true;

                    if (_motionCoordinator != null)
                    {
                        _motionCoordinator.OnApplicationPause(true, unityUs);
                    }

                    if (_windowCoordinator.IsSessionActive)
                    {
                        if (_transport.IsCollectingEnabled && !_telemetryStoppedByGame)
                        {
                            DrainCaptureSourcesIfRunning(unityUs);
                        }

                        ApplyEffectiveTouchPaused();
                        _touchAggregator.CancelActiveTouches();

                        _windowCoordinator.RecordLifecycle(new LifecycleRecord(unityUs, LifecycleKind.Paused));
                        if (_windowCoordinator.CloseCurrentWindow(CloseReason.Paused, out Batch pausedBatch))
                        {
                            HandleBatchClosed(pausedBatch);
                        }
                    }
                    else
                    {
                        ApplyEffectiveTouchPaused();
                    }

                    _sessionClock.OnPause();
                }
                else
                {
                    _isPaused = false;

                    if (_motionCoordinator != null)
                    {
                        _motionCoordinator.OnApplicationPause(false, unityUs);
                    }

                    ApplyEffectiveTouchPaused();

                    if (_windowCoordinator.IsSessionActive)
                    {
                        SessionResumeResult resume = _sessionClock.OnResume(
                            _transport.ActiveSettings.NewSessionAfterS,
                            out LifecycleRecord? steppedRecord);

                        if (resume == SessionResumeResult.StartNewSession)
                        {
                            StopCaptureSources();
                            _windowCoordinator.EndSession();
                            _hasTelemetryStopSessionUs = false;
                            FoldCompletedControlPlaneSession(persistToDisk: true);

                            if (!_transport.IsStageOverrideMode
                                && !string.IsNullOrEmpty(_lastControlPlaneBaseUrl)
                                && !string.IsNullOrEmpty(_lastApiKey))
                            {
                                StartSession(
                                    _lastControlPlaneBaseUrl,
                                    _lastApiKey,
                                    _lastSessionProperties,
                                    _lastAppVersion,
                                    _lastBuildNumber,
                                    _lastDemo);
                            }
                            else
                            {
                                _transport.StopSession();
                            }
                        }
                        else
                        {
                            // 1. Drain any samples/gaps buffered by native sensors during a short pause (e.g. full-screen ad)
                            //    and close that pause-buffered window before snapping the foreground resume window.
                            if (_transport.IsCollectingEnabled && !_telemetryStoppedByGame)
                            {
                                DrainCaptureSourcesIfRunning(unityUs);
                                if (_motionAggregator.TotalMotionSampleCount > 0 || _windowCoordinator.TotalWindowGapCount > 0)
                                {
                                    if (_windowCoordinator.CloseCurrentWindow(CloseReason.WindowFull, out Batch pauseBufferedBatch))
                                    {
                                        HandleBatchClosed(pauseBufferedBatch);
                                    }
                                }
                            }

                            // 2. Snap foreground window start to current whole second & refresh window-open snapshots.
                            _sessionClock.SnapWindowStartToCurrentSecond();
                            _windowCoordinator.RefreshWindowOpenSnapshots();

                            if (steppedRecord.HasValue)
                            {
                                _windowCoordinator.RecordLifecycle(steppedRecord.Value);
                            }

                            _windowCoordinator.RecordLifecycle(new LifecycleRecord(unityUs, LifecycleKind.Resumed));

                            if (_transport.IsCollectingEnabled && !_telemetryStoppedByGame && !_motionSource.IsRunning)
                            {
                                StartCaptureSources(_transport.ActiveSettings);
                            }
                        }
                    }
                }
            }
            catch
            {
            }
        }

        public void OnApplicationFocus(bool hasFocus)
        {
            try
            {
                if (_isShutdown || !_windowCoordinator.IsSessionActive)
                {
                    return;
                }

                _windowCoordinator.RecordLifecycle(new LifecycleRecord(
                    _clockSource.UnityTimeUs,
                    hasFocus ? LifecycleKind.FocusGained : LifecycleKind.FocusLost));
            }
            catch
            {
            }
        }

        public void OnApplicationQuit()
        {
            Shutdown();
        }

        public void Dispose()
        {
            Shutdown();
        }

        public void Shutdown()
        {
            try
            {
                if (_isShutdown)
                {
                    return;
                }

                _isShutdown = true;

                if (_windowCoordinator.IsSessionActive)
                {
                    DrainCaptureSourcesIfRunning();
                    StopCaptureSources();

                    _windowCoordinator.RecordLifecycle(new LifecycleRecord(_clockSource.UnityTimeUs, LifecycleKind.Quitting));
                    if (_windowCoordinator.CloseCurrentWindow(CloseReason.Quit, out Batch quitBatch))
                    {
                        HandleBatchClosed(quitBatch);
                    }
                    _windowCoordinator.EndSession();
                }
                else
                {
                    StopCaptureSources();
                }

                FoldCompletedControlPlaneSession(persistToDisk: true);
                _transport.StopSession();
                _worker.Shutdown(250);
                _worker.DrainRecycledBatches(_recycleBatchAction);
                _httpTransport.Dispose();
            }
            catch
            {
            }
        }

        public bool ForceCloseCurrentWindow(CloseReason reason = CloseReason.WindowFull)
        {
            try
            {
                if (_isShutdown || !_windowCoordinator.IsSessionActive)
                {
                    return false;
                }

                if (_transport.IsCollectingEnabled && !_telemetryStoppedByGame && !_isPaused)
                {
                    DrainCaptureSourcesIfRunning();
                }

                if (_windowCoordinator.CloseCurrentWindow(reason, out Batch closed))
                {
                    HandleBatchClosed(closed);
                    return true;
                }

                return false;
            }
            catch
            {
                return false;
            }
        }

        /// <summary>
        /// Closes the active window, writes <c>batch.binpb</c> (uncompressed Protobuf) and
        /// <c>batch.binpb.gz</c> (GZip <c>1f 8b</c>) to <paramref name="outputDirectory"/> for
        /// <c>BUILDING_AN_SDK.md</c> Step 5 offline validation, and queues/recycles the batch cleanly.
        /// </summary>
        public bool TryExportClosedBatchFiles(
            string outputDirectory,
            out string rawFilePath,
            out string gzipFilePath,
            out string error)
        {
            rawFilePath = string.Empty;
            gzipFilePath = string.Empty;
            error = null;

            try
            {
                if (_isShutdown)
                {
                    error = "SDK runtime is shut down.";
                    return false;
                }

                if (!_windowCoordinator.IsSessionActive)
                {
                    error = "No active session; start a session before exporting a batch.";
                    return false;
                }

                if (_transport.IsCollectingEnabled && !_telemetryStoppedByGame && !_isPaused)
                {
                    DrainCaptureSourcesIfRunning();
                }

                if (!_windowCoordinator.CloseCurrentWindow(CloseReason.WindowFull, out Batch closed) || closed == null)
                {
                    error = "Failed to close active batch window.";
                    return false;
                }

                try
                {
                    ProtobufWireWriter writer = new ProtobufWireWriter(8192);
                    if (!BatchProtobufCodec.TryEncodeBatch(closed, writer, out string encodeErr))
                    {
                        error = encodeErr ?? "Failed to encode batch.";
                        return false;
                    }

                    string dir = string.IsNullOrEmpty(outputDirectory) ? Path.GetTempPath() : outputDirectory;
                    if (!Directory.Exists(dir))
                    {
                        Directory.CreateDirectory(dir);
                    }

                    rawFilePath = Path.Combine(dir, "batch.binpb");
                    gzipFilePath = Path.Combine(dir, "batch.binpb.gz");

                    using (FileStream rawFs = new FileStream(rawFilePath, FileMode.Create, FileAccess.Write, FileShare.None))
                    {
                        if (writer.Length > 0)
                        {
                            rawFs.Write(writer.Buffer, 0, writer.Length);
                        }
                    }

                    using (FileStream gzFs = new FileStream(gzipFilePath, FileMode.Create, FileAccess.Write, FileShare.None))
                    using (GZipStream gz = new GZipStream(gzFs, CompressionLevel.Fastest, false))
                    {
                        if (writer.Length > 0)
                        {
                            gz.Write(writer.Buffer, 0, writer.Length);
                        }
                    }

                    return true;
                }
                finally
                {
                    // Hand closed batch to worker (which persists & recycles it) so sequence numbers remain contiguous
                    // and pooled Batch/child records never leak.
                    HandleBatchClosed(closed);
                }
            }
            catch (Exception ex)
            {
                error = ex.Message;
                return false;
            }
        }

        public void InjectSimulatedMotionSample(in RawMotionSample sample)
        {
            try
            {
                if (!CanRecordGameData() || _isPaused)
                {
                    return;
                }

                _windowCoordinator.IngestMotionSample(in sample, _onBatchClosedAction);
            }
            catch
            {
            }
        }

        public void InjectSimulatedTouchSample(in RawTouchSample sample)
        {
            try
            {
                if (!CanRecordGameData() || _isPaused)
                {
                    return;
                }

                _windowCoordinator.IngestTouchSample(in sample, _onBatchClosedAction);
            }
            catch
            {
            }
        }

        public AsparagusStatusSnapshot GetStatusSnapshot()
        {
            try
            {
                SessionStartResponse resp = _transport.ActiveSessionResponse;
                SessionSettings settings = _transport.ActiveSettings;
                CollectionHistory displayHistory = _hasActiveControlPlaneSession
                    ? _activeSessionSentHistory
                    : GetHistoryForNextSessionStart();

                return new AsparagusStatusSnapshot(
                    isInitialized: !_isShutdown,
                    isSessionActive: _windowCoordinator.IsSessionActive,
                    isCollecting: _transport.IsCollectingEnabled && _windowCoordinator.IsSessionActive && !_telemetryStoppedByGame && !_isPaused,
                    isTelemetryStoppedByGame: _telemetryStoppedByGame,
                    isPaused: _isPaused,
                    isStageOverrideMode: _transport.IsStageOverrideMode,
                    isRefusedForAppRun: _transport.IsRefusedForAppRun,
                    activeSessionId: resp != null ? resp.IdSession : string.Empty,
                    settingsVersion: settings != null ? settings.Version : string.Empty,
                    nextSequence: _windowCoordinator.NextSequence,
                    activeMotionSource: _motionSource != null ? _motionSource.SourceKind : CaptureSource.Unspecified,
                    activeTouchMode: _touchSource != null ? _touchSource.CurrentTouchMode : TouchMode.Unspecified,
                    windowBoundedMotionSamples: _motionAggregator.BoundedMotionSampleCount,
                    windowTotalMotionSamples: _motionAggregator.TotalMotionSampleCount,
                    windowTouchContacts: _touchAggregator.ContactCount,
                    windowTouchSamples: _touchAggregator.TotalTouchSampleCount,
                    windowEvents: _eventTracker.CurrentWindowEventCount,
                    currentStateVersion: _stateTracker.CurrentVersion,
                    openSpansCount: _eventTracker.OpenSpanCount,
                    windowGapsCount: _windowCoordinator.TotalWindowGapCount,
                    activeDiagnosticsCount: _diagnostics.ActiveCount,
                    diskQueuedBatches: _diskQueue.QueuedCount,
                    diskQueuedBytes: _diskQueue.TotalQueuedBytes,
                    sessionRetryAttempt: _transport.SessionRetryAttempt,
                    uploadRetryAttempt: _transport.UploadRetryAttempt,
                    consecutive401Count: _transport.Consecutive401Count,
                    settingsLabel: resp != null ? resp.SettingsLabel : string.Empty,
                    historySessionsBefore: displayHistory.SessionsBefore,
                    historyCollectedSecondsBefore: displayHistory.CollectedSecondsBefore,
                    historyLongestSessionSecondsBefore: displayHistory.LongestSessionSecondsBefore);
            }
            catch
            {
                return default;
            }
        }

        private void HandleBeforeSessionActivated()
        {
            try
            {
                if (_windowCoordinator.IsSessionActive)
                {
                    if (_transport.IsCollectingEnabled && !_telemetryStoppedByGame && !_isPaused)
                    {
                        DrainCaptureSourcesIfRunning();
                    }

                    StopCaptureSources();

                    if (_windowCoordinator.CloseCurrentWindow(CloseReason.SessionEnded, out Batch outgoingBatch))
                    {
                        HandleBatchClosed(outgoingBatch);
                    }

                    _windowCoordinator.EndSession();
                }

                FoldCompletedControlPlaneSession(persistToDisk: false);
            }
            catch
            {
            }
        }

        private void HandleSessionStarted(SessionStartResponse response, uint roundTripMs)
        {
            try
            {
                if (response == null)
                {
                    return;
                }

                // Count every Control Plane session start (even non-collecting / demo) per session.proto lines 70-82.
                if (!_transport.IsStageOverrideMode)
                {
                    EnsureHistoryBaselineLoaded();
                    FoldCompletedControlPlaneSession(persistToDisk: false);
                    _activeSessionSentHistory = new CollectionHistory(
                        _baseSessionsBefore,
                        _baseCollectedSecondsBefore,
                        _baseLongestSessionSecondsBefore);
                    _hasActiveControlPlaneSession = true;
                    _currentSessionCollectedUs = 0L;
                    _worker.EnqueueSaveHistory(ComputeNextLaunchHistory());
                }

                if (!_transport.IsCollectingEnabled)
                {
                    StopCaptureSources();
                    _windowCoordinator.EndSession();
                    return;
                }

                DeviceMetadataSnapshot meta = _lastDeviceMetadata ?? CollectDeviceMetadata(_lastAppVersion, _lastBuildNumber);
                SessionSettings settings = response.Settings != null ? response.Settings : SessionSettings.CreateDefaults();

                _systemStateMonitor.Reset();
                _hasTelemetryStopSessionUs = false;

                if (!_telemetryStoppedByGame && !_isPaused)
                {
                    StartCaptureSources(settings);
                }

                SessionIdentity identity = new SessionIdentity
                {
                    IdOrg = response.IdOrg,
                    IdDeployment = response.IdDeployment,
                    IdSource = response.IdSource,
                    IdSession = response.IdSession,
                    SdkVersion = _sdkVersion,
                    Platform = meta.Platform,
                    OsVersion = meta.DeviceProperties != null ? meta.DeviceProperties.OsVersion : string.Empty,
                    DeviceModel = meta.DeviceProperties != null ? meta.DeviceProperties.Model : string.Empty,
                    HasGyroscope = meta.DeviceProperties != null && meta.DeviceProperties.HasGyroscope,
                    Settings = settings
                };

                _windowCoordinator.StartNewSession(
                    identity,
                    response.ServerUtcMs,
                    roundTripMs,
                    _touchSource != null ? _touchSource.CurrentTouchMode : TouchMode.Unspecified);
            }
            catch
            {
            }
        }

        private void HandleBatchClosed(Batch closedBatch)
        {
            if (closedBatch == null)
            {
                return;
            }

            try
            {
                if (_hasActiveControlPlaneSession && closedBatch.Header != null)
                {
                    long deltaUs = closedBatch.Header.ClosedUs - closedBatch.Header.WindowStartUs;
                    if (deltaUs > 0L)
                    {
                        _currentSessionCollectedUs += deltaUs;
                        _worker.EnqueueSaveHistory(ComputeNextLaunchHistory());
                    }
                }

                bool compress = _transport.ActiveSettings != null && _transport.ActiveSettings.Compress;
                _worker.EnqueueClosedBatch(closedBatch, compress, _clockSource.WallUtcMs);
            }
            catch
            {
                _windowCoordinator.RecycleBatch(closedBatch);
            }
        }

        private void EnsureHistoryBaselineLoaded()
        {
            if (!_historyInitializedFromWorker && _worker.IsStartupLoadComplete)
            {
                CollectionHistory loaded = _worker.LoadedHistory;
                _baseSessionsBefore = loaded.SessionsBefore;
                _baseCollectedSecondsBefore = loaded.CollectedSecondsBefore;
                _baseLongestSessionSecondsBefore = loaded.LongestSessionSecondsBefore;
                _historyInitializedFromWorker = true;
            }
        }

        private CollectionHistory GetHistoryForNextSessionStart()
        {
            EnsureHistoryBaselineLoaded();
            if (_hasActiveControlPlaneSession)
            {
                return ComputeNextLaunchHistory();
            }

            return new CollectionHistory(
                _baseSessionsBefore,
                _baseCollectedSecondsBefore,
                _baseLongestSessionSecondsBefore);
        }

        private CollectionHistory ComputeNextLaunchHistory()
        {
            uint nextSessions = _baseSessionsBefore < uint.MaxValue
                ? _baseSessionsBefore + 1u
                : uint.MaxValue;
            ulong currentSec = _currentSessionCollectedUs > 0L
                ? (ulong)(_currentSessionCollectedUs / 1000000L)
                : 0UL;
            ulong nextCollected = SaturatingAdd(_baseCollectedSecondsBefore, currentSec);
            uint currentClamped = ClampToUInt32(currentSec);
            uint nextLongest = currentClamped > _baseLongestSessionSecondsBefore
                ? currentClamped
                : _baseLongestSessionSecondsBefore;

            return new CollectionHistory(nextSessions, nextCollected, nextLongest);
        }

        private void FoldCompletedControlPlaneSession(bool persistToDisk)
        {
            if (!_hasActiveControlPlaneSession)
            {
                return;
            }

            ulong sessionSec = _currentSessionCollectedUs > 0L
                ? (ulong)(_currentSessionCollectedUs / 1000000L)
                : 0UL;
            if (_baseSessionsBefore < uint.MaxValue)
            {
                _baseSessionsBefore++;
            }
            _baseCollectedSecondsBefore = SaturatingAdd(_baseCollectedSecondsBefore, sessionSec);
            uint clampedSec = ClampToUInt32(sessionSec);
            if (clampedSec > _baseLongestSessionSecondsBefore)
            {
                _baseLongestSessionSecondsBefore = clampedSec;
            }

            _hasActiveControlPlaneSession = false;
            _currentSessionCollectedUs = 0L;

            if (persistToDisk)
            {
                _worker.EnqueueSaveHistory(new CollectionHistory(
                    _baseSessionsBefore,
                    _baseCollectedSecondsBefore,
                    _baseLongestSessionSecondsBefore));
            }
        }

        private static ulong SaturatingAdd(ulong a, ulong b)
        {
            ulong sum = a + b;
            return sum < a ? ulong.MaxValue : sum;
        }

        private static uint ClampToUInt32(ulong value)
        {
            return value > uint.MaxValue ? uint.MaxValue : (uint)value;
        }

        private void StartCaptureSources(SessionSettings settings)
        {
            try
            {
                if (_motionSource != null)
                {
                    _motionSource.StartCapture(settings);
                }
            }
            catch
            {
            }

            try
            {
                if (_touchSource != null)
                {
                    _touchSource.StartCapture(settings);
                    ApplyEffectiveTouchPaused();
                }
            }
            catch
            {
            }
        }

        private void StopCaptureSources()
        {
            try
            {
                if (_motionSource != null)
                {
                    _motionSource.StopCapture();
                }
            }
            catch
            {
            }

            try
            {
                if (_touchSource != null)
                {
                    _touchSource.StopCapture();
                }
            }
            catch
            {
            }
        }

        private void DrainCaptureSourcesIfRunning()
        {
            DrainCaptureSourcesIfRunning(_clockSource.UnityTimeUs);
        }

        private void DrainCaptureSourcesIfRunning(long unityUs)
        {
            if (_motionSource != null && _motionSource.IsRunning)
            {
                _motionSource.DrainSamples(_motionDrainAdapter, unityUs);
                while (_motionSource.TryConsumeOverflowGap(out GapRecord motionGap))
                {
                    _windowCoordinator.RecordGap(motionGap, _onBatchClosedAction);
                }
            }

            if (_touchSource != null && _touchSource.IsRunning)
            {
                _touchSource.DrainSamples(_touchDrainAdapter, unityUs);
                while (_touchSource.TryConsumeOverflowGap(out GapRecord touchGap))
                {
                    _windowCoordinator.RecordGap(touchGap, _onBatchClosedAction);
                }
            }
        }

        private void ApplyEffectiveTouchPaused()
        {
            if (_touchSource == null) return;
#if UNITY_5_3_OR_NEWER
            bool paused = _isPaused || _userTouchPaused || _keyboardTouchPaused;
#else
            bool paused = _isPaused || _userTouchPaused;
#endif
            _touchSource.SetTouchPaused(paused);
            if (paused)
            {
                _touchAggregator.CancelActiveTouches();
            }
        }

        private void UpdateKeyboardTouchPause()
        {
#if UNITY_5_3_OR_NEWER
            bool shouldPause = false;
            if (_transport.ActiveSettings != null && _transport.ActiveSettings.PauseTouchOnKeyboard)
            {
                try
                {
                    shouldPause = UnityEngine.TouchScreenKeyboard.visible;
                }
                catch
                {
                    shouldPause = false;
                }
            }

            if (shouldPause != _keyboardTouchPaused)
            {
                _keyboardTouchPaused = shouldPause;
                ApplyEffectiveTouchPaused();
            }
#endif
        }

        private bool CanRecordGameData()
        {
            return !_isShutdown
                && _windowCoordinator.IsSessionActive
                && _transport.IsCollectingEnabled
                && !_telemetryStoppedByGame;
        }

        private DeviceMetadataSnapshot CollectDeviceMetadata(string appVersionOverride, string buildNumberOverride)
        {
            bool hasGyro = false;
            try
            {
                hasGyro = _motionSource != null && _motionSource.HasGyroscope;
            }
            catch
            {
            }

            return _deviceMetadataProvider.Collect(_screenInfoProvider, hasGyro, appVersionOverride, buildNumberOverride);
        }

        private static string ResolveStorageDirectory(string overrideDir)
        {
            if (!string.IsNullOrEmpty(overrideDir))
            {
                return overrideDir;
            }

#if UNITY_5_3_OR_NEWER
            try
            {
                if (!string.IsNullOrEmpty(UnityEngine.Application.persistentDataPath))
                {
                    return UnityEngine.Application.persistentDataPath;
                }
            }
            catch
            {
            }
#endif
            return Path.GetTempPath();
        }

        private static void SafeInvokeCompliance(Action<bool, long> callback, bool ok, long statusCode)
        {
            if (callback == null) return;
            try { callback(ok, statusCode); } catch { }
        }
    }
}
