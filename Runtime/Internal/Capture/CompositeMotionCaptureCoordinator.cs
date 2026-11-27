using System;
using System.Collections.Generic;
using Asparagus.Sdk.Contracts;
using Asparagus.Sdk.Internal.Time;
using Asparagus.Sdk.Internal.Tracking;

namespace Asparagus.Sdk.Internal.Capture
{
    /// <summary>
    /// Orchestrates native-first motion capture on iOS/Android with silent per-sensor and global
    /// fallback to Unity Input System, start/stall watchdogs, overflow gap forwarding, and
    /// window-persistent <see cref="ISensorClockProvider"/> tracking.
    /// </summary>
    public sealed class CompositeMotionCaptureCoordinator : IMotionCaptureSource, ISensorClockProvider
    {
        private const int SensorCount = 4;
        private const int DrainChunkSamples = 512;
        private const int MaxSamplesPerDrainCall = 32768;
        private const int MaxQueuedOverflowGaps = 64;

        private enum SensorMode : byte
        {
            Off = 0,
            Native = 1,
            UnityFallback = 2
        }

        private sealed class FallbackFilterConsumer : IMotionSampleConsumer
        {
            private readonly CompositeMotionCaptureCoordinator _owner;
            public IMotionSampleConsumer Target;

            public FallbackFilterConsumer(CompositeMotionCaptureCoordinator owner)
            {
                _owner = owner;
            }

            public void OnMotionSample(in RawMotionSample sample)
            {
                IMotionSampleConsumer target = Target;
                if (target == null)
                {
                    return;
                }

                int idx = ToSensorIndex(sample.Sensor);
                if (idx < 0 || _owner._sensorModes[idx] != SensorMode.UnityFallback)
                {
                    return;
                }

                RawMotionSample stamped = new RawMotionSample(
                    sample.Sensor,
                    sample.Source,
                    _owner._sensorFallbackReasons[idx],
                    sample.Clock,
                    sample.Unit,
                    sample.TimeUs,
                    sample.X,
                    sample.Y,
                    sample.Z);
                target.OnMotionSample(in stamped);
            }
        }

        private readonly Platform _runtimePlatform;
        private readonly INativeMotionBridge _nativeBridge;
        private readonly IMotionCaptureSource _unityFallbackSource;
        private readonly DiagnosticRecorder _diagnostics;
        private readonly IClockSource _clockSource;

        private readonly long[] _drainScratch = new long[DrainChunkSamples * PackedMotionRecord.LongsPerSample];
        private readonly SensorMode[] _sensorModes = new SensorMode[SensorCount];
        private readonly FallbackReason[] _sensorFallbackReasons = new FallbackReason[SensorCount];
        private readonly bool[] _hasStartTimestamp = new bool[SensorCount];
        private readonly long[] _sensorStartUnityUs = new long[SensorCount];
        private readonly bool[] _hasFirstNativeSample = new bool[SensorCount];
        private readonly long[] _lastNativeSampleUnityUs = new long[SensorCount];

        private readonly Queue<GapRecord> _pendingOverflowGaps = new Queue<GapRecord>(8);
        private readonly FallbackFilterConsumer _fallbackFilter;

        private SessionSettings _activeSettings = SessionSettings.CreateDefaults();
        private SessionSettings _unityFallbackSettings = SessionSettings.CreateDefaults();
        private bool _isRunning;
        private bool _isPaused;
        private bool _unityFallbackStarted;
        private bool _usedNativeInCurrentWindow;

        public CompositeMotionCaptureCoordinator(
            Platform runtimePlatform,
            INativeMotionBridge nativeBridge,
            IMotionCaptureSource unityFallbackSource,
            DiagnosticRecorder diagnostics = null,
            IClockSource clockSource = null)
        {
            _runtimePlatform = runtimePlatform;
            _nativeBridge = nativeBridge;
            _unityFallbackSource = unityFallbackSource;
            _diagnostics = diagnostics;
            _clockSource = clockSource;
            _fallbackFilter = new FallbackFilterConsumer(this);
        }

        public CaptureSource SourceKind
        {
            get
            {
                if (HasAnyActiveNativeSensor() && _nativeBridge != null)
                {
                    return _nativeBridge.SourceKind;
                }

                return _unityFallbackSource != null ? _unityFallbackSource.SourceKind : CaptureSource.Unspecified;
            }
        }

        public bool IsRunning
        {
            get { return _isRunning; }
        }

        public bool HasGyroscope
        {
            get
            {
                try
                {
                    if (_nativeBridge != null && _nativeBridge.IsPluginAvailable && _nativeBridge.HasSensor(Sensor.Gyroscope))
                    {
                        return true;
                    }

                    return _unityFallbackSource != null && _unityFallbackSource.HasGyroscope;
                }
                catch
                {
                    return false;
                }
            }
        }

        public FallbackReason GetSensorFallbackReason(Sensor sensor)
        {
            int idx = ToSensorIndex(sensor);
            return idx >= 0 ? _sensorFallbackReasons[idx] : FallbackReason.Unspecified;
        }

        public bool IsSensorRunningNatively(Sensor sensor)
        {
            int idx = ToSensorIndex(sensor);
            return idx >= 0 && _sensorModes[idx] == SensorMode.Native;
        }

        public bool IsSensorRunningOnUnity(Sensor sensor)
        {
            int idx = ToSensorIndex(sensor);
            return idx >= 0 && _sensorModes[idx] == SensorMode.UnityFallback;
        }

        public void StartCapture(SessionSettings settings, FallbackReason fallbackReason = FallbackReason.Unspecified)
        {
            try
            {
                StopCapture();

                _activeSettings = settings != null ? settings.Clone() : SessionSettings.CreateDefaults();
                _isPaused = false;
                _usedNativeInCurrentWindow = false;
                _pendingOverflowGaps.Clear();

                bool isMobile = _runtimePlatform == Platform.Android || _runtimePlatform == Platform.Ios;
                long initialUnityUs = _clockSource != null ? _clockSource.UnityTimeUs : 0L;
                bool hasInitialUnityUs = _clockSource != null;

                if (!isMobile)
                {
                    // Editor/Desktop: Unity source is first choice (not a fallback).
                    AssignAllEnabledSensorsToUnity(FallbackReason.Unspecified);
                }
                else if (_activeSettings.MotionSource == MotionSource.Unity)
                {
                    AssignAllEnabledSensorsToUnity(FallbackReason.ForcedBySettings);
                }
                else if (_nativeBridge == null || !_nativeBridge.IsPluginAvailable)
                {
                    AssignAllEnabledSensorsToUnity(FallbackReason.PluginMissing);
                    RecordDiagnosticWarning("native_plugin_missing", "Native motion plugin unavailable; falling back to Unity.", initialUnityUs);
                }
                else
                {
                    StartEnabledSensorsNatively(hasInitialUnityUs, initialUnityUs);
                }

                if (HasAnyUnityFallbackSensor())
                {
                    SyncUnityFallbackSource();
                }

                _isRunning = HasAnyActiveSensor();
            }
            catch (Exception ex)
            {
                RecordDiagnosticWarning("motion_start_error", ex.Message, _clockSource != null ? _clockSource.UnityTimeUs : 0L);
                StopCapture();
            }
        }

        public void StopCapture()
        {
            _isRunning = false;
            _isPaused = false;

            try
            {
                if (_nativeBridge != null)
                {
                    _nativeBridge.StopAll();
                }
            }
            catch
            {
            }

            try
            {
                if (_unityFallbackStarted && _unityFallbackSource != null)
                {
                    _unityFallbackSource.StopCapture();
                }
            }
            catch
            {
            }

            _unityFallbackStarted = false;
            for (int i = 0; i < SensorCount; i++)
            {
                _sensorModes[i] = SensorMode.Off;
                _sensorFallbackReasons[i] = FallbackReason.Unspecified;
                _hasStartTimestamp[i] = false;
                _sensorStartUnityUs[i] = 0L;
                _hasFirstNativeSample[i] = false;
                _lastNativeSampleUnityUs[i] = 0L;
            }
        }

        public void OnApplicationPause(bool paused, long currentUnityTimeUs)
        {
            try
            {
                _isPaused = paused;

                if (_nativeBridge != null && _nativeBridge.IsPluginAvailable)
                {
                    _nativeBridge.SetUnityPaused(paused, _activeSettings.NativeBufferS);
                }

                if (!paused)
                {
                    // Reset start/stall watchdog baselines on resume so ad/background pause never causes false fallback.
                    for (int i = 0; i < SensorCount; i++)
                    {
                        if (_sensorModes[i] == SensorMode.Native)
                        {
                            _hasStartTimestamp[i] = true;
                            _sensorStartUnityUs[i] = currentUnityTimeUs;
                            if (_hasFirstNativeSample[i])
                            {
                                _lastNativeSampleUnityUs[i] = currentUnityTimeUs;
                            }
                        }
                    }
                }
            }
            catch
            {
            }
        }

        public void ResetWindowSensorClockFlag()
        {
            _usedNativeInCurrentWindow = HasAnyActiveNativeSensor();
        }

        public bool TryGetSensorTimeUs(out long sensorUs)
        {
            try
            {
                if ((HasAnyActiveNativeSensor() || _usedNativeInCurrentWindow)
                    && _nativeBridge != null
                    && _nativeBridge.IsPluginAvailable)
                {
                    return _nativeBridge.TryGetSensorClockUs(out sensorUs);
                }
            }
            catch
            {
            }

            sensorUs = 0L;
            return false;
        }

        public bool TryConsumeOverflowGap(out GapRecord gap)
        {
            if (_pendingOverflowGaps.Count > 0)
            {
                gap = _pendingOverflowGaps.Dequeue();
                return true;
            }

            gap = default;
            return false;
        }

        public void DrainSamples(IMotionSampleConsumer consumer, long currentUnityTimeUs)
        {
            if (!_isRunning || consumer == null)
            {
                return;
            }

            try
            {
                bool fallbackAddedThisFrame = false;

                if (HasAnyActiveNativeSensor() && _nativeBridge != null)
                {
                    // 1. Check native runtime errors
                    int nativeErrors = _nativeBridge.ConsumeErrorCount();
                    if (nativeErrors > 0)
                    {
                        RecordDiagnosticWarning("native_sensor_error", "Native sensor plugin reported " + nativeErrors + " runtime error(s); switching to Unity.", currentUnityTimeUs);
                        FallbackAllNativeSensors(FallbackReason.NativeError);
                        fallbackAddedThisFrame = true;
                    }
                    else
                    {
                        // 2. Collect native ring-buffer overflow gaps
                        CollectNativeOverflowGaps();

                        // 3. Drain packed native samples in bounded chunks
                        int totalDrained = 0;
                        bool drainFailed = false;
                        CaptureSource nativeSourceKind = _nativeBridge.SourceKind;

                        while (totalDrained < MaxSamplesPerDrainCall)
                        {
                            int remainingBudget = MaxSamplesPerDrainCall - totalDrained;
                            int requestCount = remainingBudget < DrainChunkSamples ? remainingBudget : DrainChunkSamples;
                            int count = _nativeBridge.DrainPackedSamples(_drainScratch, requestCount);

                            if (count < 0)
                            {
                                drainFailed = true;
                                break;
                            }

                            if (count == 0)
                            {
                                break;
                            }

                            for (int i = 0; i < count; i++)
                            {
                                PackedMotionRecord.Unpack(_drainScratch, i, out Sensor sensor, out long tsUs, out float x, out float y, out float z);
                                int idx = ToSensorIndex(sensor);
                                if (idx < 0 || _sensorModes[idx] != SensorMode.Native)
                                {
                                    continue;
                                }

                                _hasFirstNativeSample[idx] = true;
                                _lastNativeSampleUnityUs[idx] = currentUnityTimeUs;
                                _usedNativeInCurrentWindow = true;

                                RawMotionSample sample = new RawMotionSample(
                                    sensor,
                                    nativeSourceKind,
                                    FallbackReason.Unspecified,
                                    Clock.Sensor,
                                    _nativeBridge.GetSensorUnit(sensor),
                                    tsUs,
                                    x,
                                    y,
                                    z);
                                consumer.OnMotionSample(in sample);
                            }

                            totalDrained += count;
                            if (count < requestCount)
                            {
                                break;
                            }
                        }

                        if (drainFailed)
                        {
                            RecordDiagnosticWarning("native_drain_failed", "Native sensor bulk drain failed; switching to Unity.", currentUnityTimeUs);
                            FallbackAllNativeSensors(FallbackReason.NativeError);
                            fallbackAddedThisFrame = true;
                        }
                        else if (!_isPaused)
                        {
                            // 4. Evaluate start-limit & stall-limit watchdogs
                            if (EvaluateNativeWatchdogs(currentUnityTimeUs))
                            {
                                fallbackAddedThisFrame = true;
                            }
                        }
                    }
                }

                if (fallbackAddedThisFrame && HasAnyUnityFallbackSensor())
                {
                    SyncUnityFallbackSource();
                }

                // 5. Drain Unity fallback source with selective per-sensor filtering
                if (_unityFallbackStarted && _unityFallbackSource != null && HasAnyUnityFallbackSensor())
                {
                    _fallbackFilter.Target = consumer;
                    try
                    {
                        _unityFallbackSource.DrainSamples(_fallbackFilter, currentUnityTimeUs);
                    }
                    finally
                    {
                        _fallbackFilter.Target = null;
                    }

                    while (_unityFallbackSource.TryConsumeOverflowGap(out GapRecord unityGap))
                    {
                        EnqueueOverflowGap(unityGap);
                    }
                }
            }
            catch (Exception ex)
            {
                RecordDiagnosticWarning("motion_drain_error", ex.Message, currentUnityTimeUs);
            }
        }

        private void StartEnabledSensorsNatively(bool hasInitialUnityUs, long initialUnityUs)
        {
            // Clear any stale native pause flag from a prior session/background cycle before starting sensors.
            _nativeBridge.SetUnityPaused(false, _activeSettings.NativeBufferS);

            for (int i = 0; i < SensorCount; i++)
            {
                Sensor sensor = (Sensor)(i + 1);
                uint hz = GetRequestedHz(_activeSettings, sensor);
                if (hz == 0u)
                {
                    _sensorModes[i] = SensorMode.Off;
                    continue;
                }

                if (!_nativeBridge.HasSensor(sensor))
                {
                    // Per spec: if phone lacks sensor completely, leave it out.
                    // For Gyroscope, double-check Unity fallback availability before leaving off.
                    if (sensor == Sensor.Gyroscope && _unityFallbackSource != null && _unityFallbackSource.HasGyroscope)
                    {
                        _sensorModes[i] = SensorMode.UnityFallback;
                        _sensorFallbackReasons[i] = FallbackReason.SensorRefused;
                    }
                    else
                    {
                        _sensorModes[i] = SensorMode.Off;
                    }
                    continue;
                }

                NativeSensorStartResult res = _nativeBridge.StartSensor(sensor, hz, _activeSettings.NativeBufferS);
                if (res == NativeSensorStartResult.Started)
                {
                    _sensorModes[i] = SensorMode.Native;
                    _sensorFallbackReasons[i] = FallbackReason.Unspecified;
                    _hasStartTimestamp[i] = hasInitialUnityUs;
                    _sensorStartUnityUs[i] = initialUnityUs;
                    _hasFirstNativeSample[i] = false;
                    _lastNativeSampleUnityUs[i] = initialUnityUs;
                    _usedNativeInCurrentWindow = true;
                }
                else if (res == NativeSensorStartResult.MissingOrRefused)
                {
                    _sensorModes[i] = SensorMode.UnityFallback;
                    _sensorFallbackReasons[i] = FallbackReason.SensorRefused;
                }
                else
                {
                    _sensorModes[i] = SensorMode.UnityFallback;
                    _sensorFallbackReasons[i] = FallbackReason.NativeError;
                    RecordDiagnosticWarning("native_start_error", "Native start failed for " + sensor.ToProtoName(), initialUnityUs);
                }
            }
        }

        private void AssignAllEnabledSensorsToUnity(FallbackReason reason)
        {
            for (int i = 0; i < SensorCount; i++)
            {
                Sensor sensor = (Sensor)(i + 1);
                uint hz = GetRequestedHz(_activeSettings, sensor);
                if (hz == 0u)
                {
                    _sensorModes[i] = SensorMode.Off;
                    continue;
                }

                if (sensor == Sensor.Gyroscope && _unityFallbackSource != null && !_unityFallbackSource.HasGyroscope)
                {
                    _sensorModes[i] = SensorMode.Off;
                    continue;
                }

                _sensorModes[i] = SensorMode.UnityFallback;
                _sensorFallbackReasons[i] = reason;
            }
        }

        private bool EvaluateNativeWatchdogs(long currentUnityTimeUs)
        {
            bool anyFallback = false;
            long startLimitUs = (long)_activeSettings.NativeStartLimitMs * 1000L;
            long stallLimitUs = (long)_activeSettings.NativeStallLimitMs * 1000L;

            for (int i = 0; i < SensorCount; i++)
            {
                if (_sensorModes[i] != SensorMode.Native)
                {
                    continue;
                }

                Sensor sensor = (Sensor)(i + 1);
                if (!_hasStartTimestamp[i])
                {
                    _hasStartTimestamp[i] = true;
                    _sensorStartUnityUs[i] = currentUnityTimeUs;
                }

                if (!_hasFirstNativeSample[i])
                {
                    if (startLimitUs > 0L && (currentUnityTimeUs - _sensorStartUnityUs[i]) > startLimitUs)
                    {
                        SafeStopNativeSensor(sensor);
                        _sensorModes[i] = SensorMode.UnityFallback;
                        _sensorFallbackReasons[i] = FallbackReason.NoFirstSample;
                        anyFallback = true;
                        RecordDiagnosticWarning("native_no_first_sample", "No initial native sample for " + sensor.ToProtoName() + " within limit; switching to Unity.", currentUnityTimeUs);
                    }
                }
                else
                {
                    if (stallLimitUs > 0L && (currentUnityTimeUs - _lastNativeSampleUnityUs[i]) > stallLimitUs)
                    {
                        SafeStopNativeSensor(sensor);
                        _sensorModes[i] = SensorMode.UnityFallback;
                        _sensorFallbackReasons[i] = FallbackReason.SamplesStopped;
                        anyFallback = true;
                        RecordDiagnosticWarning("native_samples_stopped", "Native samples stalled for " + sensor.ToProtoName() + "; switching to Unity.", currentUnityTimeUs);
                    }
                }
            }

            return anyFallback;
        }

        private void FallbackAllNativeSensors(FallbackReason reason)
        {
            try
            {
                if (_nativeBridge != null)
                {
                    _nativeBridge.StopAll();
                }
            }
            catch
            {
            }

            for (int i = 0; i < SensorCount; i++)
            {
                if (_sensorModes[i] == SensorMode.Native)
                {
                    _sensorModes[i] = SensorMode.UnityFallback;
                    _sensorFallbackReasons[i] = reason;
                }
            }
        }

        private void SyncUnityFallbackSource()
        {
            if (_unityFallbackSource == null)
            {
                return;
            }

            // Enable ONLY the sensors currently routed to Unity fallback so unused Unity sensors stay off (Principle 5).
            _unityFallbackSettings = _activeSettings.Clone();
            _unityFallbackSettings.AccelerometerHz = _sensorModes[0] == SensorMode.UnityFallback ? _activeSettings.AccelerometerHz : 0u;
            _unityFallbackSettings.GyroscopeHz = _sensorModes[1] == SensorMode.UnityFallback ? _activeSettings.GyroscopeHz : 0u;
            _unityFallbackSettings.GravityHz = _sensorModes[2] == SensorMode.UnityFallback ? _activeSettings.GravityHz : 0u;
            _unityFallbackSettings.MagnetometerHz = _sensorModes[3] == SensorMode.UnityFallback ? _activeSettings.MagnetometerHz : 0u;

            FallbackReason primaryReason = FallbackReason.Unspecified;
            for (int i = 0; i < SensorCount; i++)
            {
                if (_sensorModes[i] == SensorMode.UnityFallback)
                {
                    primaryReason = _sensorFallbackReasons[i];
                    break;
                }
            }

            _unityFallbackSource.StartCapture(_unityFallbackSettings, primaryReason);
            _unityFallbackStarted = true;
        }

        private void CollectNativeOverflowGaps()
        {
            for (int i = 0; i < SensorCount; i++)
            {
                if (_sensorModes[i] != SensorMode.Native)
                {
                    continue;
                }

                Sensor sensor = (Sensor)(i + 1);
                if (_nativeBridge.TryConsumeOverflow(sensor, out long startUs, out long endUs))
                {
                    EnqueueOverflowGap(new GapRecord
                    {
                        Signal = SampleQuantizer.ToGapSignal(sensor),
                        Clock = Clock.Sensor,
                        StartTimeUs = startUs,
                        EndTimeUs = endUs,
                        Cause = GapCause.BufferOverflowed
                    });
                }
            }
        }

        private void EnqueueOverflowGap(GapRecord gap)
        {
            if (_pendingOverflowGaps.Count >= MaxQueuedOverflowGaps)
            {
                _pendingOverflowGaps.Dequeue();
            }

            _pendingOverflowGaps.Enqueue(gap);
        }

        private void SafeStopNativeSensor(Sensor sensor)
        {
            try
            {
                if (_nativeBridge != null)
                {
                    _nativeBridge.StopSensor(sensor);
                }
            }
            catch
            {
            }
        }

        private bool HasAnyActiveNativeSensor()
        {
            for (int i = 0; i < SensorCount; i++)
            {
                if (_sensorModes[i] == SensorMode.Native) return true;
            }
            return false;
        }

        private bool HasAnyUnityFallbackSensor()
        {
            for (int i = 0; i < SensorCount; i++)
            {
                if (_sensorModes[i] == SensorMode.UnityFallback) return true;
            }
            return false;
        }

        private bool HasAnyActiveSensor()
        {
            for (int i = 0; i < SensorCount; i++)
            {
                if (_sensorModes[i] != SensorMode.Off) return true;
            }
            return false;
        }

        private void RecordDiagnosticWarning(string code, string detail, long unityTimeUs)
        {
            if (_diagnostics != null)
            {
                _diagnostics.RecordWarning(code, detail, unityTimeUs);
            }
        }

        private static uint GetRequestedHz(SessionSettings settings, Sensor sensor)
        {
            switch (sensor)
            {
                case Sensor.Accelerometer: return settings.AccelerometerHz;
                case Sensor.Gyroscope: return settings.GyroscopeHz;
                case Sensor.Gravity: return settings.GravityHz;
                case Sensor.Magnetometer: return settings.MagnetometerHz;
                default: return 0u;
            }
        }

        private static int ToSensorIndex(Sensor sensor)
        {
            int idx = (int)sensor - 1;
            return idx >= 0 && idx < SensorCount ? idx : -1;
        }
    }
}
