using System;
using System.Collections.Generic;
using Asparagus.Sdk.Contracts;
using Asparagus.Sdk.Internal.Capture;
using Asparagus.Sdk.Internal.Encoding;
using Asparagus.Sdk.Internal.Storage;
using Asparagus.Sdk.Internal.Time;
using Asparagus.Sdk.Internal.Tracking;

namespace Asparagus.Sdk.Internal.Pipeline
{
    public sealed class SessionIdentity
    {
        public string IdOrg = string.Empty;
        public string IdDeployment = string.Empty;
        public string IdSource = string.Empty;
        public string IdSession = string.Empty;
        public SdkVersion SdkVersion;
        public Platform Platform;
        public string OsVersion = string.Empty;
        public string DeviceModel = string.Empty;
        public bool HasGyroscope;
        public SessionSettings Settings = SessionSettings.CreateDefaults();
    }

    /// <summary>
    /// Owns active batch window lifecycle across clocks, state, events/spans, motion, touch,
    /// screen changes, lifecycle records, diagnostics, and proactive W8-26 bounds splitting
    /// (Decision 142). Enforces all six Header intake rules and <c>CLOCK_SENSOR</c> presence invariants.
    /// </summary>
    public sealed class BatchWindowCoordinator
    {
        public const int ProactiveMaxEstimatedUnzippedBytes = 220000;
        private const int MaxBatchPoolSize = 8;

        private readonly IClockSource _clockSource;
        private readonly SessionClock _sessionClock;
        private readonly WindowCallOrder _callOrder;
        private readonly StateTracker _stateTracker;
        private readonly EventAndSpanTracker _eventTracker;
        private readonly MotionWindowAggregator _motionAggregator;
        private readonly TouchWindowAggregator _touchAggregator;
        private readonly ScreenTracker _screenTracker;
        private readonly DiagnosticRecorder _diagnostics;
        private readonly CompositeMotionCaptureCoordinator _motionCoordinator;
        private readonly DiskBatchQueue _diskQueue;

        private readonly Stack<Batch> _batchPool = new Stack<Batch>(4);
        private readonly Stack<Touch> _touchPool = new Stack<Touch>(4);
        private readonly List<LifecycleRecord> _windowLifecycle = new List<LifecycleRecord>(8);
        private readonly List<GapRecord> _windowExtraGaps = new List<GapRecord>(8);
        private readonly List<GapRecord> _flushGapsScratch = new List<GapRecord>(16);

        private SessionIdentity _identity;
        private bool _isSessionActive;
        private ulong _nextSequence = 1UL;

        private ClockReading _windowOpenClocks;
        private ScreenRecord _windowOpenScreen;
        private int _windowOpenUtcOffsetMinutes;
        private int _estimatedExtraBytes;

        public BatchWindowCoordinator(
            IClockSource clockSource,
            SessionClock sessionClock,
            WindowCallOrder callOrder,
            StateTracker stateTracker,
            EventAndSpanTracker eventTracker,
            MotionWindowAggregator motionAggregator,
            TouchWindowAggregator touchAggregator,
            ScreenTracker screenTracker,
            DiagnosticRecorder diagnostics,
            CompositeMotionCaptureCoordinator motionCoordinator = null,
            DiskBatchQueue diskQueue = null)
        {
            _clockSource = clockSource ?? new SystemClockSource();
            _sessionClock = sessionClock ?? new SessionClock(_clockSource);
            _callOrder = callOrder ?? new WindowCallOrder();
            _stateTracker = stateTracker ?? new StateTracker(_callOrder, null, diagnostics);
            _eventTracker = eventTracker ?? new EventAndSpanTracker(_callOrder);
            _motionAggregator = motionAggregator ?? new MotionWindowAggregator();
            _touchAggregator = touchAggregator ?? new TouchWindowAggregator();
            _screenTracker = screenTracker ?? new ScreenTracker();
            _diagnostics = diagnostics ?? new DiagnosticRecorder();
            _motionCoordinator = motionCoordinator;
            _diskQueue = diskQueue;
        }

        public bool IsSessionActive
        {
            get { return _isSessionActive; }
        }

        public ulong NextSequence
        {
            get { return _nextSequence; }
        }

        public SessionIdentity Identity
        {
            get { return _identity; }
        }

        public int TotalWindowGapCount
        {
            get
            {
                return _motionAggregator.GapCount
                    + _touchAggregator.GapCount
                    + _windowExtraGaps.Count
                    + (_diskQueue != null && _diskQueue.HasPendingDroppedBatchReport ? 1 : 0);
            }
        }

        public int EstimatedUnzippedBytes
        {
            get
            {
                return 512
                    + _motionAggregator.TotalMotionSampleCount * 12
                    + _touchAggregator.TotalTouchSampleCount * 20
                    + _eventTracker.CurrentWindowEventCount * 64
                    + _stateTracker.WindowRecords.Count * 64
                    + TotalWindowGapCount * 40
                    + _screenTracker.ScreenChangeCount * 32
                    + _estimatedExtraBytes;
            }
        }

        public void StartNewSession(
            SessionIdentity identity,
            long serverUtcMs,
            uint roundTripMs,
            TouchMode touchMode = TouchMode.Unspecified)
        {
            SessionIdentity safe = identity ?? new SessionIdentity();
            SessionSettings settings = safe.Settings != null ? safe.Settings.Clone() : SessionSettings.CreateDefaults();

            _identity = new SessionIdentity
            {
                IdOrg = safe.IdOrg ?? string.Empty,
                IdDeployment = safe.IdDeployment ?? string.Empty,
                IdSource = safe.IdSource ?? string.Empty,
                IdSession = safe.IdSession ?? string.Empty,
                SdkVersion = safe.SdkVersion,
                Platform = safe.Platform,
                OsVersion = safe.OsVersion ?? string.Empty,
                DeviceModel = safe.DeviceModel ?? string.Empty,
                HasGyroscope = safe.HasGyroscope,
                Settings = settings
            };

            _nextSequence = 1UL;
            _isSessionActive = true;

            _sessionClock.StartSession(serverUtcMs, roundTripMs);
            _callOrder.ResetWindow();
            _stateTracker.ResetSession();
            _eventTracker.ResetSession();
            _motionAggregator.Configure(settings);
            _motionAggregator.ResetWindow();
            _touchAggregator.Configure(settings, touchMode);
            _touchAggregator.ResetSession();
            _screenTracker.Reset();
            _diagnostics.ResetWindow();

            _windowLifecycle.Clear();
            _windowExtraGaps.Clear();
            _estimatedExtraBytes = 0;

            CaptureWindowOpenSnapshots();
        }

        public void EndSession()
        {
            _isSessionActive = false;
            _windowLifecycle.Clear();
            _windowExtraGaps.Clear();
            _estimatedExtraBytes = 0;
        }

        /// <summary>
        /// Proactively closes the active window before a 751st event or byte-bound breach would occur.
        /// </summary>
        public bool PrepareForEvent(Action<Batch> onBatchClosed)
        {
            if (!_isSessionActive)
            {
                return false;
            }

            if (_eventTracker.CurrentWindowEventCount >= BatchConstants.MaxEvents
                || EstimatedUnzippedBytes >= ProactiveMaxEstimatedUnzippedBytes)
            {
                if (CloseCurrentWindow(CloseReason.WindowFull, out Batch closed))
                {
                    SafeNotifyClosed(onBatchClosed, closed);
                    return true;
                }
            }

            return false;
        }

        public GameEvent RecordEvent(
            string eventName,
            IReadOnlyDictionary<string, Value> fields,
            bool hasOutcome,
            string outcome,
            string spanOverride,
            Action<Batch> onBatchClosed = null)
        {
            if (!_isSessionActive)
            {
                return null;
            }

            PrepareForEvent(onBatchClosed);

            long unityUs = _clockSource.UnityTimeUs;
            GameEvent evt = _eventTracker.RecordEvent(eventName, fields, hasOutcome, outcome, spanOverride, unityUs);
            if (evt != null)
            {
                _estimatedExtraBytes += EstimateEventPayloadBytes(evt);
            }

            return evt;
        }

        public void IngestMotionSample(in RawMotionSample sample, Action<Batch> onBatchClosed = null)
        {
            if (!_isSessionActive)
            {
                return;
            }

            bool hitsMotionBound = sample.Sensor != Sensor.Magnetometer
                && _motionAggregator.BoundedMotionSampleCount >= BatchConstants.MaxMotionSamples;

            if (hitsMotionBound
                || TotalWindowGapCount >= BatchConstants.MaxGaps
                || EstimatedUnzippedBytes >= ProactiveMaxEstimatedUnzippedBytes)
            {
                if (CloseCurrentWindow(CloseReason.WindowFull, out Batch closed))
                {
                    SafeNotifyClosed(onBatchClosed, closed);
                }
            }

            _motionAggregator.OnMotionSample(in sample);
        }

        public void IngestTouchSample(in RawTouchSample sample, Action<Batch> onBatchClosed = null)
        {
            if (!_isSessionActive)
            {
                return;
            }

            if (_touchAggregator.ContactCount >= BatchConstants.MaxTouchContacts
                || _touchAggregator.TotalTouchSampleCount >= BatchConstants.MaxTouchSamples
                || TotalWindowGapCount >= BatchConstants.MaxGaps
                || EstimatedUnzippedBytes >= ProactiveMaxEstimatedUnzippedBytes)
            {
                if (CloseCurrentWindow(CloseReason.WindowFull, out Batch closed))
                {
                    SafeNotifyClosed(onBatchClosed, closed);
                }
            }

            _touchAggregator.OnTouchSample(in sample);
        }

        public void RecordGap(GapRecord gap, Action<Batch> onBatchClosed = null)
        {
            if (!_isSessionActive)
            {
                return;
            }

            if (TotalWindowGapCount >= BatchConstants.MaxGaps)
            {
                if (CloseCurrentWindow(CloseReason.WindowFull, out Batch closed))
                {
                    SafeNotifyClosed(onBatchClosed, closed);
                }
            }

            _windowExtraGaps.Add(gap);
        }

        public void RecordLifecycle(LifecycleRecord record)
        {
            if (!_isSessionActive)
            {
                return;
            }

            _windowLifecycle.Add(record);
        }

        public void NoteEstimatedStateBytes(int approxBytes)
        {
            if (approxBytes > 0)
            {
                _estimatedExtraBytes += approxBytes;
            }
        }

        /// <summary>
        /// Refreshes window-open clock, screen, and UTC-offset snapshots (e.g., immediately after
        /// returning from background pause so post-step clocks and timezone changes are reflected).
        /// </summary>
        public void RefreshWindowOpenSnapshots()
        {
            if (_isSessionActive)
            {
                CaptureWindowOpenSnapshots();
            }
        }

        public bool EvaluateBounds(long currentSessionUs)
        {
            if (!_isSessionActive || _identity == null)
            {
                return false;
            }

            uint batchLengthS = GetEffectiveBatchLengthS();
            long windowEndUs = _sessionClock.GetWindowEndUs(batchLengthS);
            if (currentSessionUs >= windowEndUs)
            {
                return true;
            }

            if (_eventTracker.CurrentWindowEventCount >= BatchConstants.MaxEvents) return true;
            if (TotalWindowGapCount >= BatchConstants.MaxGaps) return true;
            if (_motionAggregator.BoundedMotionSampleCount >= BatchConstants.MaxMotionSamples) return true;
            if (_touchAggregator.ContactCount >= BatchConstants.MaxTouchContacts) return true;
            if (_touchAggregator.TotalTouchSampleCount >= BatchConstants.MaxTouchSamples) return true;
            if (_screenTracker.ScreenChangeCount >= BatchConstants.MaxScreenChanges) return true;
            if (EstimatedUnzippedBytes >= ProactiveMaxEstimatedUnzippedBytes) return true;

            return false;
        }

        public bool Tick(Action<Batch> onBatchClosed)
        {
            if (!_isSessionActive)
            {
                return false;
            }

            _screenTracker.PollForChanges(_clockSource.UnityTimeUs);

            long nowSessionUs = _sessionClock.CurrentSessionUs;
            if (EvaluateBounds(nowSessionUs))
            {
                if (CloseCurrentWindow(CloseReason.WindowFull, out Batch closed))
                {
                    SafeNotifyClosed(onBatchClosed, closed);
                    return true;
                }
            }

            return false;
        }

        /// <summary>
        /// Closes the active window into a pooled <see cref="Batch"/>, enforcing all six intake
        /// Header rules and <c>CLOCK_SENSOR</c> presence guarantees, then opens the next window.
        /// </summary>
        public bool CloseCurrentWindow(CloseReason reason, out Batch closedBatch)
        {
            closedBatch = null;
            if (!_isSessionActive || _identity == null)
            {
                return false;
            }

            bool claimedDropReport = false;
            GapRecord claimedDropGap = default;
            uint claimedDropCount = 0u;
            try
            {
                uint effectiveBatchLengthS = GetEffectiveBatchLengthS();
                long windowStartUs = SessionClock.FloorToWholeSecondUs(_sessionClock.CurrentWindowStartUs);
                long windowEndUs = _sessionClock.GetWindowEndUs(effectiveBatchLengthS);
                long nowSessionUs = _sessionClock.CurrentSessionUs;
                bool timerFull = reason == CloseReason.WindowFull
                    && nowSessionUs >= windowEndUs
                    && nowSessionUs < windowEndUs + 1000000L;

                long closedUs;
                if (timerFull)
                {
                    closedUs = windowEndUs;
                }
                else
                {
                    long maxWindowClosedUs = windowStartUs + (long)BatchConstants.MaxWindowLengthS * 1000000L;
                    closedUs = nowSessionUs;
                    if (closedUs < windowStartUs)
                    {
                        closedUs = windowStartUs;
                    }
                    else if (closedUs > maxWindowClosedUs)
                    {
                        closedUs = maxWindowClosedUs;
                    }
                }

                uint windowLengthS = _sessionClock.AdvanceWindow(closedUs, timerFull, effectiveBatchLengthS);

                Batch batch = RentBatch();
                Header header = batch.Header;
                header.IdOrg = Utf8StringBounds.TruncateUtf8(_identity.IdOrg, BatchConstants.MaxHeaderStringBytes);
                header.IdDeployment = Utf8StringBounds.TruncateUtf8(_identity.IdDeployment, BatchConstants.MaxHeaderStringBytes);
                header.IdSource = Utf8StringBounds.TruncateUtf8(_identity.IdSource, BatchConstants.MaxHeaderStringBytes);
                header.IdSession = Utf8StringBounds.TruncateUtf8(_identity.IdSession, BatchConstants.MaxHeaderStringBytes);
                header.Sequence = _nextSequence++;
                header.SchemaVersion = BatchConstants.SchemaVersion;
                header.SetSdkVersion(_identity.SdkVersion);
                header.Platform = _identity.Platform;
                header.OsVersion = Utf8StringBounds.TruncateUtf8(_identity.OsVersion, BatchConstants.MaxHeaderStringBytes);
                header.DeviceModel = Utf8StringBounds.TruncateUtf8(_identity.DeviceModel, BatchConstants.MaxHeaderStringBytes);
                header.SettingsVersion = Utf8StringBounds.TruncateUtf8(
                    _identity.Settings != null ? _identity.Settings.Version : string.Empty,
                    BatchConstants.MaxHeaderStringBytes);

                header.WindowStartUs = windowStartUs;
                header.WindowLengthS = windowLengthS;
                header.ClosedUs = closedUs;
                header.CloseReason = reason;

                header.SetClocks(_windowOpenClocks);
                if (header.ServerAnchor == null)
                {
                    header.ServerAnchor = new ServerAnchor();
                }
                _sessionClock.CopyServerAnchorTo(header.ServerAnchor);
                header.SetScreen(_windowOpenScreen);
                header.HasGyroscope = _identity.HasGyroscope;
                header.SetUtcOffsetMinutes(_windowOpenUtcOffsetMinutes);

                // Flush state & events (and populate Carried snapshots frozen at window open)
                _stateTracker.FlushWindowTo(batch);
                _eventTracker.FlushWindowTo(batch);

                // Flush motion & touch gaps into scratch list so batch.Gaps never exceeds MaxGaps (750)
                _flushGapsScratch.Clear();
                _motionAggregator.FlushWindowTo(batch.SensorBlocks, _flushGapsScratch);

                Touch touchObj = RentTouch();
                if (_touchAggregator.FlushWindowTo(touchObj, _flushGapsScratch))
                {
                    batch.Touch = touchObj;
                }
                else
                {
                    RecycleTouch(touchObj);
                    batch.Touch = null;
                }

                for (int i = 0; i < _windowExtraGaps.Count; i++)
                {
                    _flushGapsScratch.Add(_windowExtraGaps[i]);
                }
                _windowExtraGaps.Clear();

                bool reserveDropSlot = _diskQueue != null && _diskQueue.HasPendingDroppedBatchReport;
                int nonDropBudget = reserveDropSlot ? (BatchConstants.MaxGaps - 1) : BatchConstants.MaxGaps;
                int emitCount = _flushGapsScratch.Count < nonDropBudget ? _flushGapsScratch.Count : nonDropBudget;
                for (int i = 0; i < emitCount; i++)
                {
                    batch.Gaps.Add(_flushGapsScratch[i]);
                }

                // Spill any excess gaps into _windowExtraGaps for the next window
                for (int i = emitCount; i < _flushGapsScratch.Count; i++)
                {
                    _windowExtraGaps.Add(_flushGapsScratch[i]);
                }
                _flushGapsScratch.Clear();

                if (_diskQueue != null && batch.Gaps.Count < BatchConstants.MaxGaps)
                {
                    if (_diskQueue.TryClaimDroppedBatchReport(out claimedDropGap, out claimedDropCount))
                    {
                        claimedDropReport = true;
                        header.BatchesDropped = claimedDropCount;
                        batch.Gaps.Add(claimedDropGap);
                    }
                    else
                    {
                        header.BatchesDropped = 0u;
                    }
                }
                else
                {
                    header.BatchesDropped = 0u;
                }

                _screenTracker.FlushWindowChangesTo(batch.ScreenChanges);

                for (int i = 0; i < _windowLifecycle.Count; i++)
                {
                    batch.Lifecycle.Add(_windowLifecycle[i]);
                }
                _windowLifecycle.Clear();

                _diagnostics.DrainTo(batch.Diagnostics);

                // Safety invariant: if any block or gap uses CLOCK_SENSOR, Header.Clocks.HasSensorUs MUST be true.
                EnsureSensorClockHeaderInvariant(batch);

                // Advance window-open carried snapshots & reset call order for next window
                _stateTracker.AdvanceWindow();
                _eventTracker.AdvanceWindow();
                _callOrder.ResetWindow();
                _estimatedExtraBytes = 0;

                if (_motionCoordinator != null)
                {
                    _motionCoordinator.ResetWindowSensorClockFlag();
                }

                CaptureWindowOpenSnapshots();

                closedBatch = batch;
                return true;
            }
            catch
            {
                if (claimedDropReport && _diskQueue != null)
                {
                    _diskQueue.RestoreCarriedDropReport(
                        claimedDropCount,
                        claimedDropGap.FirstDroppedSequence,
                        claimedDropGap.StartTimeUs,
                        claimedDropGap.EndTimeUs);
                }

                closedBatch = null;
                return false;
            }
        }

        /// <summary>
        /// Returns a serialized <see cref="Batch"/> and all its pooled child collections back to pools.
        /// Call on the main thread after background worker finishes writing the batch to disk.
        /// </summary>
        public void RecycleBatch(Batch batch)
        {
            if (batch == null)
            {
                return;
            }

            try
            {
                _eventTracker.ReturnEventsToPool(batch.Events);
                _stateTracker.ReturnRecordsToPool(batch.StateRecords);
                _motionAggregator.RecycleBlocks(batch.SensorBlocks);
                if (batch.Touch != null)
                {
                    _touchAggregator.RecycleTouch(batch.Touch);
                    RecycleTouch(batch.Touch);
                    batch.Touch = null;
                }
                _diagnostics.ReturnToPool(batch.Diagnostics);

                batch.Clear();
                if (_batchPool.Count < MaxBatchPoolSize)
                {
                    _batchPool.Push(batch);
                }
            }
            catch
            {
            }
        }

        private void CaptureWindowOpenSnapshots()
        {
            _windowOpenClocks = _sessionClock.ReadClocks();
            _windowOpenScreen = _screenTracker.WindowOpenScreen;
            _windowOpenScreen.TimeUs = 0L;
            _windowOpenUtcOffsetMinutes = _sessionClock.UtcOffsetMinutes;
        }

        private void EnsureSensorClockHeaderInvariant(Batch batch)
        {
            if (batch.Header.Clocks.HasSensorUs)
            {
                return;
            }

            bool usesSensorClock = false;
            long maxSensorTs = 0L;
            bool hasMaxTs = false;

            for (int i = 0; i < batch.SensorBlocks.Count; i++)
            {
                SensorBlock blk = batch.SensorBlocks[i];
                if (blk != null && blk.Clock == Clock.Sensor)
                {
                    usesSensorClock = true;
                    if (blk.X.Count > 0 && (!hasMaxTs || blk.FirstTimeUs > maxSensorTs))
                    {
                        maxSensorTs = blk.FirstTimeUs;
                        hasMaxTs = true;
                    }
                    for (int h = 0; h < blk.Holds.Count; h++)
                    {
                        Hold hold = blk.Holds[h];
                        if (hold != null && (!hasMaxTs || hold.EndTimeUs > maxSensorTs))
                        {
                            maxSensorTs = hold.EndTimeUs;
                            hasMaxTs = true;
                        }
                    }
                }
            }

            for (int i = 0; i < batch.Gaps.Count; i++)
            {
                GapRecord g = batch.Gaps[i];
                if (g.Clock == Clock.Sensor)
                {
                    usesSensorClock = true;
                    if (!hasMaxTs || g.EndTimeUs > maxSensorTs)
                    {
                        maxSensorTs = g.EndTimeUs;
                        hasMaxTs = true;
                    }
                }
            }

            if (!usesSensorClock)
            {
                return;
            }

            long sensorUs;
            if (!_clockSource.TryGetSensorTimeUs(out sensorUs))
            {
                sensorUs = maxSensorTs;
            }

            ClockReading updated = batch.Header.Clocks;
            updated.SetSensorUs(sensorUs);
            batch.Header.SetClocks(updated);
        }

        private uint GetEffectiveBatchLengthS()
        {
            uint len = _identity != null && _identity.Settings != null
                ? _identity.Settings.BatchLengthS
                : SessionSettings.DefaultBatchLengthS;

            if (len < 1u) return 1u;
            if (len > BatchConstants.MaxWindowLengthS) return BatchConstants.MaxWindowLengthS;
            return len;
        }

        private static int EstimateEventPayloadBytes(GameEvent evt)
        {
            int bytes = evt.Name != null ? evt.Name.Length : 0;
            if (evt.HasOutcome && evt.Outcome != null) bytes += evt.Outcome.Length;
            if (evt.SpanName != null) bytes += evt.SpanName.Length;

            foreach (KeyValuePair<string, Value> kv in evt.Fields)
            {
                bytes += (kv.Key != null ? kv.Key.Length : 0) + 12;
                if (kv.Value.Kind == ValueKind.Text)
                {
                    bytes += kv.Value.TextValue.Length * 2;
                }
            }

            return bytes;
        }

        private Batch RentBatch()
        {
            Batch b = _batchPool.Count > 0 ? _batchPool.Pop() : new Batch();
            if (b.Header == null)
            {
                b.Header = new Header();
            }
            return b;
        }

        private Touch RentTouch()
        {
            return _touchPool.Count > 0 ? _touchPool.Pop() : new Touch();
        }

        private void RecycleTouch(Touch t)
        {
            if (t == null) return;
            t.Clear();
            if (_touchPool.Count < MaxBatchPoolSize)
            {
                _touchPool.Push(t);
            }
        }

        private static void SafeNotifyClosed(Action<Batch> onBatchClosed, Batch closed)
        {
            if (onBatchClosed == null || closed == null)
            {
                return;
            }

            try
            {
                onBatchClosed(closed);
            }
            catch
            {
            }
        }
    }
}
