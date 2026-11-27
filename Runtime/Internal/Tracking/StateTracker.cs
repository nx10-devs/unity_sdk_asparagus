using System;
using System.Collections.Generic;
using Asparagus.Sdk.Contracts;
using Asparagus.Sdk.Internal.Encoding;

namespace Asparagus.Sdk.Internal.Tracking
{
    /// <summary>
    /// Tracks game state and SDK-owned <c>nx10_*</c> state (W15-04 / Decision 152),
    /// maintaining frozen window-open <see cref="Carried.State"/> snapshots and pooled <see cref="StateRecord"/>s.
    /// </summary>
    public sealed class StateTracker
    {
        public const int MaxStateKeys = 128;
        public const int MaxKeyBytes = 256;
        public const int MaxTextValueBytes = 4096;
        public const double BrightnessDeltaThreshold = 0.05d;
        public const long BrightnessMinIntervalUs = 1000000L;

        private const double BrightnessDeltaEpsilon = 1e-9d;
        private const int MaxPoolSize = 128;

        private readonly WindowCallOrder _callOrder;
        private readonly Action<string> _warningLogger;
        private readonly DiagnosticRecorder _diagnostics;

        private readonly Dictionary<string, Value> _currentState = new Dictionary<string, Value>(16, StringComparer.Ordinal);
        private uint _currentVersion;

        private readonly Dictionary<string, Value> _windowOpenState = new Dictionary<string, Value>(16, StringComparer.Ordinal);
        private uint _windowOpenVersion;

        private readonly List<StateRecord> _windowRecords = new List<StateRecord>(16);
        private readonly Stack<StateRecord> _recordPool = new Stack<StateRecord>(16);
        private readonly List<KeyValuePair<string, Value>> _preservedSdkEntries = new List<KeyValuePair<string, Value>>(4);

        private bool _hasSentBrightness;
        private double _lastSentBrightness;
        private long _lastSentBrightnessTimeUs;

        private bool _hasSentScreenLocked;
        private bool _lastSentScreenLocked;

        public StateTracker(
            WindowCallOrder callOrder,
            Action<string> warningLogger = null,
            DiagnosticRecorder diagnostics = null)
        {
            _callOrder = callOrder ?? new WindowCallOrder();
            _warningLogger = warningLogger ?? DefaultWarningLogger;
            _diagnostics = diagnostics;
        }

        public uint CurrentVersion
        {
            get { return _currentVersion; }
        }

        public uint WindowOpenVersion
        {
            get { return _windowOpenVersion; }
        }

        public int CurrentKeyCount
        {
            get { return _currentState.Count; }
        }

        public IReadOnlyDictionary<string, Value> CurrentState
        {
            get { return _currentState; }
        }

        public IReadOnlyList<StateRecord> WindowRecords
        {
            get { return _windowRecords; }
        }

        public void ResetSession()
        {
            _currentState.Clear();
            _currentVersion = 0u;
            _windowOpenState.Clear();
            _windowOpenVersion = 0u;
            RecycleWindowRecords();
            _preservedSdkEntries.Clear();
            _hasSentBrightness = false;
            _lastSentBrightness = 0d;
            _lastSentBrightnessTimeUs = 0L;
            _hasSentScreenLocked = false;
            _lastSentScreenLocked = false;
        }

        public bool SetAttribute(string key, Value value, long unityTimeUs)
        {
            try
            {
                if (string.IsNullOrEmpty(key))
                {
                    return false;
                }

                if (SdkStateKeys.IsReservedKey(key))
                {
                    LogWarning("[AsparagusSDK] Ignored SetAttribute for reserved SDK state key '" + key + "'. Keys starting with 'nx10_' are reserved for the SDK.");
                    return false;
                }

                string boundedKey = Utf8StringBounds.TruncateUtf8(key, MaxKeyBytes);
                if (boundedKey.Length == 0)
                {
                    return false;
                }

                if (!_currentState.ContainsKey(boundedKey) && _currentState.Count >= MaxStateKeys)
                {
                    if (_diagnostics != null)
                    {
                        _diagnostics.RecordWarning("state_key_limit", "State key limit (" + MaxStateKeys + ") reached; ignored key '" + boundedKey + "'.", unityTimeUs);
                    }
                    return false;
                }

                Value sanitized = SanitizeValue(value);
                _currentState[boundedKey] = sanitized;
                _currentVersion++;

                StateRecord rec = RentRecord();
                rec.TimeUs = unityTimeUs;
                rec.Kind = StateKind.Patch;
                rec.Values[boundedKey] = sanitized;
                rec.Order = _callOrder.NextOrder();
                rec.Version = _currentVersion;
                _windowRecords.Add(rec);
                return true;
            }
            catch
            {
                return false;
            }
        }

        public bool RemoveAttribute(string key, long unityTimeUs)
        {
            try
            {
                if (string.IsNullOrEmpty(key))
                {
                    return false;
                }

                if (SdkStateKeys.IsReservedKey(key))
                {
                    LogWarning("[AsparagusSDK] Ignored RemoveAttribute for reserved SDK state key '" + key + "'. Keys starting with 'nx10_' are reserved for the SDK.");
                    return false;
                }

                string boundedKey = Utf8StringBounds.TruncateUtf8(key, MaxKeyBytes);
                if (boundedKey.Length == 0)
                {
                    return false;
                }

                _currentState.Remove(boundedKey);
                _currentVersion++;

                StateRecord rec = RentRecord();
                rec.TimeUs = unityTimeUs;
                rec.Kind = StateKind.Patch;
                rec.RemovedKeys.Add(boundedKey);
                rec.Order = _callOrder.NextOrder();
                rec.Version = _currentVersion;
                _windowRecords.Add(rec);
                return true;
            }
            catch
            {
                return false;
            }
        }

        public bool SetAttributes(Dictionary<string, Value> attributes, long unityTimeUs)
        {
            try
            {
                PreserveSdkKeysAndClearCurrentState();

                bool sawReserved = false;
                string firstReserved = null;
                bool hitKeyLimit = false;

                if (attributes != null)
                {
                    foreach (KeyValuePair<string, Value> kv in attributes)
                    {
                        ApplyGameAttributeEntry(kv.Key, kv.Value, ref sawReserved, ref firstReserved, ref hitKeyLimit);
                    }
                }

                return FinalizePut(unityTimeUs, sawReserved, firstReserved, hitKeyLimit);
            }
            catch
            {
                return false;
            }
        }

        public bool SetAttributes(IEnumerable<KeyValuePair<string, Value>> attributes, long unityTimeUs)
        {
            try
            {
                if (attributes is Dictionary<string, Value> dict)
                {
                    return SetAttributes(dict, unityTimeUs);
                }

                PreserveSdkKeysAndClearCurrentState();

                bool sawReserved = false;
                string firstReserved = null;
                bool hitKeyLimit = false;

                if (attributes != null)
                {
                    foreach (KeyValuePair<string, Value> kv in attributes)
                    {
                        ApplyGameAttributeEntry(kv.Key, kv.Value, ref sawReserved, ref firstReserved, ref hitKeyLimit);
                    }
                }

                return FinalizePut(unityTimeUs, sawReserved, firstReserved, hitKeyLimit);
            }
            catch
            {
                return false;
            }
        }

        public bool ClearAttributes(long unityTimeUs)
        {
            try
            {
                PreserveSdkKeysAndClearCurrentState();
                return FinalizePut(unityTimeUs, false, null, false);
            }
            catch
            {
                return false;
            }
        }

        /// <summary>
        /// SDK-owned <c>nx10_screen_brightness</c> updater (W15-04):
        /// ignores NaN/Infinity/&lt;0 (platform cannot tell), clamps to [0, 1],
        /// and emits <c>STATE_KIND_PATCH</c> on first report or when delta &gt;= 0.05 and &gt;= 1s elapsed.
        /// </summary>
        public bool UpdateSdkScreenBrightness(double brightness01, long unityTimeUs)
        {
            try
            {
                if (double.IsNaN(brightness01) || double.IsInfinity(brightness01) || brightness01 < 0d)
                {
                    return false;
                }

                double clamped = brightness01 > 1d ? 1d : brightness01;

                if (_hasSentBrightness)
                {
                    if ((unityTimeUs - _lastSentBrightnessTimeUs) < BrightnessMinIntervalUs)
                    {
                        return false;
                    }

                    if (Math.Abs(clamped - _lastSentBrightness) < (BrightnessDeltaThreshold - BrightnessDeltaEpsilon))
                    {
                        return false;
                    }
                }

                _hasSentBrightness = true;
                _lastSentBrightness = clamped;
                _lastSentBrightnessTimeUs = unityTimeUs;

                Value val = Value.FromNumber(clamped);
                _currentState[SdkStateKeys.ScreenBrightness] = val;
                _currentVersion++;

                StateRecord rec = RentRecord();
                rec.TimeUs = unityTimeUs;
                rec.Kind = StateKind.Patch;
                rec.Values[SdkStateKeys.ScreenBrightness] = val;
                rec.Order = _callOrder.NextOrder();
                rec.Version = _currentVersion;
                _windowRecords.Add(rec);
                return true;
            }
            catch
            {
                return false;
            }
        }

        /// <summary>
        /// SDK-owned <c>nx10_screen_locked</c> updater (W15-04):
        /// ignores <c>null</c> (platform cannot tell), emits <c>STATE_KIND_PATCH</c> on first report
        /// and thereafter only on boolean transitions.
        /// </summary>
        public bool UpdateSdkScreenLocked(bool? isLocked, long unityTimeUs)
        {
            try
            {
                if (!isLocked.HasValue)
                {
                    return false;
                }

                bool locked = isLocked.Value;
                if (_hasSentScreenLocked && _lastSentScreenLocked == locked)
                {
                    return false;
                }

                _hasSentScreenLocked = true;
                _lastSentScreenLocked = locked;

                Value val = Value.FromFlag(locked);
                _currentState[SdkStateKeys.ScreenLocked] = val;
                _currentVersion++;

                StateRecord rec = RentRecord();
                rec.TimeUs = unityTimeUs;
                rec.Kind = StateKind.Patch;
                rec.Values[SdkStateKeys.ScreenLocked] = val;
                rec.Order = _callOrder.NextOrder();
                rec.Version = _currentVersion;
                _windowRecords.Add(rec);
                return true;
            }
            catch
            {
                return false;
            }
        }

        /// <summary>
        /// Writes the frozen window-open state snapshot into <see cref="Carried.State"/>.
        /// Always populates a non-null <see cref="State"/> instance (even when empty) per batch.proto.
        /// </summary>
        public void WriteCarriedStateTo(Carried carried)
        {
            if (carried == null)
            {
                return;
            }

            if (carried.State == null)
            {
                carried.State = new State();
            }
            else
            {
                carried.State.Clear();
            }

            carried.State.Version = _windowOpenVersion;
            foreach (KeyValuePair<string, Value> kv in _windowOpenState)
            {
                carried.State.Values[kv.Key] = kv.Value;
            }
        }

        /// <summary>
        /// Writes the window-open <see cref="Carried.State"/> snapshot and transfers all active
        /// window <see cref="StateRecord"/> instances into <paramref name="targetBatch"/>.
        /// </summary>
        public void FlushWindowTo(Batch targetBatch)
        {
            if (targetBatch == null)
            {
                return;
            }

            if (targetBatch.Carried == null)
            {
                targetBatch.Carried = new Carried();
            }

            WriteCarriedStateTo(targetBatch.Carried);

            for (int i = 0; i < _windowRecords.Count; i++)
            {
                targetBatch.StateRecords.Add(_windowRecords[i]);
            }

            _windowRecords.Clear();
        }

        /// <summary>
        /// Promotes the current state into the next window's open snapshot and recycles any
        /// un-flushed window records.
        /// </summary>
        public void AdvanceWindow()
        {
            _windowOpenState.Clear();
            foreach (KeyValuePair<string, Value> kv in _currentState)
            {
                _windowOpenState[kv.Key] = kv.Value;
            }

            _windowOpenVersion = _currentVersion;
            RecycleWindowRecords();
        }

        public void ReturnRecordsToPool(List<StateRecord> records)
        {
            if (records == null)
            {
                return;
            }

            for (int i = 0; i < records.Count; i++)
            {
                StateRecord rec = records[i];
                if (rec != null && _recordPool.Count < MaxPoolSize)
                {
                    rec.Clear();
                    _recordPool.Push(rec);
                }
            }

            records.Clear();
        }

        private void PreserveSdkKeysAndClearCurrentState()
        {
            _preservedSdkEntries.Clear();
            foreach (KeyValuePair<string, Value> kv in _currentState)
            {
                if (SdkStateKeys.IsReservedKey(kv.Key))
                {
                    _preservedSdkEntries.Add(kv);
                }
            }

            _currentState.Clear();
            for (int i = 0; i < _preservedSdkEntries.Count; i++)
            {
                KeyValuePair<string, Value> sdkKv = _preservedSdkEntries[i];
                _currentState[sdkKv.Key] = sdkKv.Value;
            }

            _preservedSdkEntries.Clear();
        }

        private void ApplyGameAttributeEntry(
            string key,
            Value value,
            ref bool sawReserved,
            ref string firstReserved,
            ref bool hitKeyLimit)
        {
            if (string.IsNullOrEmpty(key))
            {
                return;
            }

            if (SdkStateKeys.IsReservedKey(key))
            {
                if (!sawReserved)
                {
                    sawReserved = true;
                    firstReserved = key;
                }
                return;
            }

            string boundedKey = Utf8StringBounds.TruncateUtf8(key, MaxKeyBytes);
            if (boundedKey.Length == 0)
            {
                return;
            }

            if (!_currentState.ContainsKey(boundedKey) && _currentState.Count >= MaxStateKeys)
            {
                hitKeyLimit = true;
                return;
            }

            _currentState[boundedKey] = SanitizeValue(value);
        }

        private bool FinalizePut(long unityTimeUs, bool sawReserved, string firstReserved, bool hitKeyLimit)
        {
            if (sawReserved)
            {
                LogWarning("[AsparagusSDK] Ignored reserved SDK state key(s) in SetAttributes (e.g. '" + firstReserved + "'). Keys starting with 'nx10_' are reserved for the SDK.");
            }

            if (hitKeyLimit && _diagnostics != null)
            {
                _diagnostics.RecordWarning("state_key_limit", "State key limit (" + MaxStateKeys + ") reached during SetAttributes; excess keys ignored.", unityTimeUs);
            }

            _currentVersion++;

            StateRecord rec = RentRecord();
            rec.TimeUs = unityTimeUs;
            rec.Kind = StateKind.Put;
            foreach (KeyValuePair<string, Value> kv in _currentState)
            {
                rec.Values[kv.Key] = kv.Value;
            }
            rec.Order = _callOrder.NextOrder();
            rec.Version = _currentVersion;
            _windowRecords.Add(rec);
            return true;
        }

        private static Value SanitizeValue(Value value)
        {
            if (value.Kind == ValueKind.Text)
            {
                string raw = value.TextValue;
                string truncated = Utf8StringBounds.TruncateUtf8(raw, MaxTextValueBytes);
                return ReferenceEquals(raw, truncated) ? value : Value.FromText(truncated);
            }

            if (value.Kind == ValueKind.Number)
            {
                double num = value.NumberValue;
                if (double.IsNaN(num) || double.IsInfinity(num))
                {
                    return Value.Empty;
                }
            }

            return value;
        }

        private StateRecord RentRecord()
        {
            if (_recordPool.Count > 0)
            {
                return _recordPool.Pop();
            }

            return new StateRecord();
        }

        private void RecycleWindowRecords()
        {
            for (int i = 0; i < _windowRecords.Count; i++)
            {
                StateRecord rec = _windowRecords[i];
                if (rec != null && _recordPool.Count < MaxPoolSize)
                {
                    rec.Clear();
                    _recordPool.Push(rec);
                }
            }

            _windowRecords.Clear();
        }

        private void LogWarning(string message)
        {
            if (_warningLogger == null)
            {
                return;
            }

            try
            {
                _warningLogger(message);
            }
            catch
            {
                // Fault containment: logger failures must never escape.
            }
        }

        private static void DefaultWarningLogger(string message)
        {
#if UNITY_5_3_OR_NEWER
            UnityEngine.Debug.LogWarning(message);
#endif
        }
    }
}
