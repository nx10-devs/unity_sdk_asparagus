#if ENABLE_INPUT_SYSTEM
using System;
using Asparagus.Sdk.Contracts;
using Asparagus.Sdk.Internal.Time;
using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.InputSystem.LowLevel;

namespace Asparagus.Sdk.Internal.Capture
{
    /// <summary>
    /// Event-stream driven Unity Input System motion capture source (<c>CAPTURE_SOURCE_UNITY_EVENTS</c>).
    /// Buffers samples into a fixed-capacity struct ring buffer inside <see cref="InputSystem.onEvent"/>
    /// with zero per-sample allocations, never mutates <see cref="InputSystem.settings"/>, and restores
    /// SDK-enabled sensors on <see cref="StopCapture"/>.
    /// </summary>
    public sealed class UnityInputSystemMotionSource : IMotionCaptureSource
    {
        private const int RingCapacity = 4096;

        private readonly IClockSource _clockSource;
        private readonly object _ringLock = new object();
        private readonly RawMotionSample[] _ring = new RawMotionSample[RingCapacity];

        private int _ringHead;
        private int _ringCount;

        private bool _hasOverflow;
        private long _overflowStartUnityUs;
        private long _overflowEndUnityUs;

        private bool _isRunning;
        private bool _subscribed;
        private FallbackReason _fallbackReason;

        private bool _wantAcc;
        private bool _wantGyro;
        private bool _wantGrav;
        private bool _wantMag;

        private Accelerometer _accDevice;
        private UnityEngine.InputSystem.Gyroscope _gyroDevice;
        private GravitySensor _gravDevice;
        private MagneticFieldSensor _magDevice;

        private bool _enabledAccBySdk;
        private bool _enabledGyroBySdk;
        private bool _enabledGravBySdk;
        private bool _enabledMagBySdk;

        private bool _hasTimeAnchor;
        private double _anchorEventSec;
        private long _anchorUnityUs;
        private long _lastEmittedUnityUs;

        public UnityInputSystemMotionSource(IClockSource clockSource)
        {
            _clockSource = clockSource ?? new SystemClockSource();
        }

        public CaptureSource SourceKind
        {
            get { return CaptureSource.UnityEvents; }
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
                    return UnityEngine.InputSystem.Gyroscope.current != null || SystemInfo.supportsGyroscope;
                }
                catch
                {
                    return false;
                }
            }
        }

        public void StartCapture(SessionSettings settings, FallbackReason fallbackReason = FallbackReason.Unspecified)
        {
            try
            {
                StopCapture();

                SessionSettings s = settings ?? SessionSettings.CreateDefaults();
                _fallbackReason = fallbackReason;
                _wantAcc = s.AccelerometerHz > 0u;
                _wantGyro = s.GyroscopeHz > 0u;
                _wantGrav = s.GravityHz > 0u;
                _wantMag = s.MagnetometerHz > 0u;

                if (!_wantAcc && !_wantGyro && !_wantGrav && !_wantMag)
                {
                    return;
                }

                if (_wantAcc)
                {
                    _accDevice = Accelerometer.current;
                    EnableSensorIfPresent(_accDevice, s.AccelerometerHz, ref _enabledAccBySdk);
                }

                if (_wantGyro)
                {
                    _gyroDevice = UnityEngine.InputSystem.Gyroscope.current;
                    EnableSensorIfPresent(_gyroDevice, s.GyroscopeHz, ref _enabledGyroBySdk);
                }

                if (_wantGrav)
                {
                    _gravDevice = GravitySensor.current;
                    EnableSensorIfPresent(_gravDevice, s.GravityHz, ref _enabledGravBySdk);
                }

                if (_wantMag)
                {
                    _magDevice = MagneticFieldSensor.current;
                    EnableSensorIfPresent(_magDevice, s.MagnetometerHz, ref _enabledMagBySdk);
                }

                lock (_ringLock)
                {
                    _ringHead = 0;
                    _ringCount = 0;
                    _hasOverflow = false;
                    _overflowStartUnityUs = 0L;
                    _overflowEndUnityUs = 0L;

                    double nowEventSec = InputState.currentTime;
                    if (nowEventSec > 0.0)
                    {
                        _hasTimeAnchor = true;
                        _anchorEventSec = nowEventSec;
                        _anchorUnityUs = _clockSource.UnityTimeUs;
                    }
                    else
                    {
                        _hasTimeAnchor = false;
                    }

                    _lastEmittedUnityUs = 0L;
                }

                _isRunning = true;
                InputSystem.onEvent += OnInputEvent;
                _subscribed = true;
            }
            catch
            {
                StopCapture();
            }
        }

        public void StopCapture()
        {
            _isRunning = false;

            try
            {
                if (_subscribed)
                {
                    InputSystem.onEvent -= OnInputEvent;
                    _subscribed = false;
                }
            }
            catch
            {
                _subscribed = false;
            }

            DisableSensorIfEnabledBySdk(_accDevice, ref _enabledAccBySdk);
            DisableSensorIfEnabledBySdk(_gyroDevice, ref _enabledGyroBySdk);
            DisableSensorIfEnabledBySdk(_gravDevice, ref _enabledGravBySdk);
            DisableSensorIfEnabledBySdk(_magDevice, ref _enabledMagBySdk);

            _accDevice = null;
            _gyroDevice = null;
            _gravDevice = null;
            _magDevice = null;
        }

        public void DrainSamples(IMotionSampleConsumer consumer, long currentUnityTimeUs)
        {
            if (consumer == null)
            {
                return;
            }

            try
            {
                while (true)
                {
                    RawMotionSample sample;
                    lock (_ringLock)
                    {
                        if (_ringCount == 0)
                        {
                            break;
                        }

                        sample = _ring[_ringHead];
                        _ringHead = (_ringHead + 1) % RingCapacity;
                        _ringCount--;
                    }

                    consumer.OnMotionSample(in sample);
                }
            }
            catch
            {
                // Fault containment: never throw into game loop.
            }
        }

        public bool TryConsumeOverflowGap(out GapRecord gap)
        {
            lock (_ringLock)
            {
                if (!_hasOverflow)
                {
                    gap = default;
                    return false;
                }

                gap = new GapRecord
                {
                    Signal = GapSignal.Everything,
                    Clock = Clock.Unity,
                    StartTimeUs = _overflowStartUnityUs,
                    EndTimeUs = _overflowEndUnityUs,
                    Cause = GapCause.BufferOverflowed
                };
                _hasOverflow = false;
                return true;
            }
        }

        private void OnInputEvent(InputEventPtr eventPtr, InputDevice device)
        {
            if (!_isRunning || !eventPtr.valid || device == null)
            {
                return;
            }

            try
            {
                if (eventPtr.type != StateEvent.Type && eventPtr.type != DeltaStateEvent.Type)
                {
                    return;
                }

                int deviceId = device.deviceId;
                Vector3 v;

                if (_wantAcc && _accDevice != null && deviceId == _accDevice.deviceId)
                {
                    if (_accDevice.acceleration.ReadValueFromEvent(eventPtr, out v))
                    {
                        EnqueueSample(Asparagus.Sdk.Contracts.Sensor.Accelerometer, ValueUnit.G, eventPtr.time, v);
                    }
                    return;
                }

                if (_wantGyro && _gyroDevice != null && deviceId == _gyroDevice.deviceId)
                {
                    if (_gyroDevice.angularVelocity.ReadValueFromEvent(eventPtr, out v))
                    {
                        EnqueueSample(Asparagus.Sdk.Contracts.Sensor.Gyroscope, ValueUnit.RadiansPerSecond, eventPtr.time, v);
                    }
                    return;
                }

                if (_wantGrav && _gravDevice != null && deviceId == _gravDevice.deviceId)
                {
                    if (_gravDevice.gravity.ReadValueFromEvent(eventPtr, out v))
                    {
                        EnqueueSample(Asparagus.Sdk.Contracts.Sensor.Gravity, ValueUnit.G, eventPtr.time, v);
                    }
                    return;
                }

                if (_wantMag && _magDevice != null && deviceId == _magDevice.deviceId)
                {
                    if (_magDevice.magneticField.ReadValueFromEvent(eventPtr, out v))
                    {
                        EnqueueSample(Asparagus.Sdk.Contracts.Sensor.Magnetometer, ValueUnit.Microtesla, eventPtr.time, v);
                    }
                }
            }
            catch
            {
                // Total fault containment inside InputSystem callback.
            }
        }

        private void EnqueueSample(Asparagus.Sdk.Contracts.Sensor sensor, ValueUnit unit, double eventTimeSec, Vector3 v)
        {
            lock (_ringLock)
            {
                long unityUs = MapEventTimeToUnityUsLocked(eventTimeSec);
                RawMotionSample sample = new RawMotionSample(
                    sensor,
                    CaptureSource.UnityEvents,
                    _fallbackReason,
                    Clock.Unity,
                    unit,
                    unityUs,
                    v.x,
                    v.y,
                    v.z);

                if (_ringCount == RingCapacity)
                {
                    // Drop oldest sample cleanly and record overflow window rather than allocating.
                    long droppedTimeUs = _ring[_ringHead].TimeUs;
                    if (!_hasOverflow)
                    {
                        _hasOverflow = true;
                        _overflowStartUnityUs = droppedTimeUs;
                    }
                    _overflowEndUnityUs = unityUs;

                    _ringHead = (_ringHead + 1) % RingCapacity;
                    _ringCount--;
                }

                int tail = (_ringHead + _ringCount) % RingCapacity;
                _ring[tail] = sample;
                _ringCount++;
            }
        }

        private long MapEventTimeToUnityUsLocked(double eventTimeSec)
        {
            if (!_hasTimeAnchor)
            {
                _hasTimeAnchor = true;
                _anchorEventSec = eventTimeSec;
                _anchorUnityUs = _clockSource.UnityTimeUs;
            }

            long mapped = _anchorUnityUs + (long)Math.Round((eventTimeSec - _anchorEventSec) * 1000000.0);
            if (mapped < 0L)
            {
                mapped = 0L;
            }

            if (mapped < _lastEmittedUnityUs)
            {
                mapped = _lastEmittedUnityUs;
            }
            else
            {
                _lastEmittedUnityUs = mapped;
            }

            return mapped;
        }

        private static void EnableSensorIfPresent(UnityEngine.InputSystem.Sensor sensor, uint requestedHz, ref bool enabledBySdk)
        {
            if (sensor == null)
            {
                return;
            }

            try
            {
                if (!sensor.enabled)
                {
                    InputSystem.EnableDevice(sensor);
                    enabledBySdk = true;
                }

                if (requestedHz > 0u)
                {
                    sensor.samplingFrequency = requestedHz;
                }
            }
            catch
            {
                // Some platforms/Editor stubs reject samplingFrequency; ignore safely.
            }
        }

        private static void DisableSensorIfEnabledBySdk(UnityEngine.InputSystem.Sensor sensor, ref bool enabledBySdk)
        {
            if (!enabledBySdk)
            {
                return;
            }

            enabledBySdk = false;
            if (sensor == null)
            {
                return;
            }

            try
            {
                if (sensor.enabled)
                {
                    InputSystem.DisableDevice(sensor);
                }
            }
            catch
            {
            }
        }
    }

    /// <summary>
    /// Event-stream driven Unity Input System touch capture source.
    /// Reads <see cref="InputSystem.settings"/> read-only to report <see cref="TouchMode.Events"/>
    /// vs <see cref="TouchMode.EventsMerged"/>, buffers <see cref="RawTouchSample"/> structs in a
    /// pre-allocated ring buffer, and respects keyboard/manual pause guards.
    /// </summary>
    public sealed class UnityInputSystemTouchSource : ITouchCaptureSource
    {
        private const int RingCapacity = 2048;

        private readonly IClockSource _clockSource;
        private readonly object _ringLock = new object();
        private readonly RawTouchSample[] _ring = new RawTouchSample[RingCapacity];

        private int _ringHead;
        private int _ringCount;

        private bool _hasOverflow;
        private long _overflowStartUnityUs;
        private long _overflowEndUnityUs;

        private bool _isRunning;
        private bool _subscribed;
        private bool _touchPaused;
        private bool _pauseTouchOnKeyboard;

        private bool _hasTimeAnchor;
        private double _anchorEventSec;
        private long _anchorUnityUs;
        private long _lastEmittedUnityUs;

        public UnityInputSystemTouchSource(IClockSource clockSource)
        {
            _clockSource = clockSource ?? new SystemClockSource();
        }

        public TouchMode CurrentTouchMode
        {
            get
            {
                try
                {
                    InputSettings settings = InputSystem.settings;
                    if (settings != null && settings.disableRedundantEventsMerging)
                    {
                        return TouchMode.Events;
                    }

                    return TouchMode.EventsMerged;
                }
                catch
                {
                    return TouchMode.EventsMerged;
                }
            }
        }

        public bool IsRunning
        {
            get { return _isRunning; }
        }

        public void StartCapture(SessionSettings settings)
        {
            try
            {
                StopCapture();

                SessionSettings s = settings ?? SessionSettings.CreateDefaults();
                _pauseTouchOnKeyboard = s.PauseTouchOnKeyboard;

                lock (_ringLock)
                {
                    _ringHead = 0;
                    _ringCount = 0;
                    _hasOverflow = false;
                    _overflowStartUnityUs = 0L;
                    _overflowEndUnityUs = 0L;

                    double nowEventSec = InputState.currentTime;
                    if (nowEventSec > 0.0)
                    {
                        _hasTimeAnchor = true;
                        _anchorEventSec = nowEventSec;
                        _anchorUnityUs = _clockSource.UnityTimeUs;
                    }
                    else
                    {
                        _hasTimeAnchor = false;
                    }

                    _lastEmittedUnityUs = 0L;
                }

                _isRunning = true;
                InputSystem.onEvent += OnInputEvent;
                _subscribed = true;
            }
            catch
            {
                StopCapture();
            }
        }

        public void StopCapture()
        {
            _isRunning = false;

            try
            {
                if (_subscribed)
                {
                    InputSystem.onEvent -= OnInputEvent;
                    _subscribed = false;
                }
            }
            catch
            {
                _subscribed = false;
            }
        }

        public void SetTouchPaused(bool paused)
        {
            _touchPaused = paused;
        }

        public void DrainSamples(ITouchSampleConsumer consumer, long currentUnityTimeUs)
        {
            if (consumer == null)
            {
                return;
            }

            try
            {
                while (true)
                {
                    RawTouchSample sample;
                    lock (_ringLock)
                    {
                        if (_ringCount == 0)
                        {
                            break;
                        }

                        sample = _ring[_ringHead];
                        _ringHead = (_ringHead + 1) % RingCapacity;
                        _ringCount--;
                    }

                    consumer.OnTouchSample(in sample);
                }
            }
            catch
            {
            }
        }

        public bool TryConsumeOverflowGap(out GapRecord gap)
        {
            lock (_ringLock)
            {
                if (!_hasOverflow)
                {
                    gap = default;
                    return false;
                }

                gap = new GapRecord
                {
                    Signal = GapSignal.Touch,
                    Clock = Clock.Unity,
                    StartTimeUs = _overflowStartUnityUs,
                    EndTimeUs = _overflowEndUnityUs,
                    Cause = GapCause.BufferOverflowed
                };
                _hasOverflow = false;
                return true;
            }
        }

        private void OnInputEvent(InputEventPtr eventPtr, InputDevice device)
        {
            if (!_isRunning || _touchPaused || !eventPtr.valid || device == null)
            {
                return;
            }

            try
            {
                if (_pauseTouchOnKeyboard && TouchScreenKeyboard.visible)
                {
                    return;
                }

                Touchscreen touchscreen = device as Touchscreen;
                if (touchscreen == null)
                {
                    return;
                }

                if (eventPtr.type != StateEvent.Type)
                {
                    return;
                }

                if (!touchscreen.primaryTouch.ReadValueFromEvent(eventPtr, out TouchState ts))
                {
                    return;
                }

                if (!TryMapPhase(ts.phase, out TouchPhaseKind phase))
                {
                    return;
                }

                uint fingerId = ts.touchId >= 0 ? (uint)ts.touchId : 0u;
                bool hasPressure = ts.pressure > 0f && !float.IsNaN(ts.pressure) && !float.IsInfinity(ts.pressure);

                lock (_ringLock)
                {
                    long unityUs = MapEventTimeToUnityUsLocked(eventPtr.time);
                    RawTouchSample sample = new RawTouchSample(
                        fingerId,
                        phase,
                        unityUs,
                        ts.position.x,
                        ts.position.y,
                        ts.radius.x,
                        ts.radius.y,
                        hasPressure ? ts.pressure : 0f,
                        hasPressure);

                    if (_ringCount == RingCapacity)
                    {
                        long droppedTimeUs = _ring[_ringHead].TimeUs;
                        if (!_hasOverflow)
                        {
                            _hasOverflow = true;
                            _overflowStartUnityUs = droppedTimeUs;
                        }
                        _overflowEndUnityUs = unityUs;

                        _ringHead = (_ringHead + 1) % RingCapacity;
                        _ringCount--;
                    }

                    int tail = (_ringHead + _ringCount) % RingCapacity;
                    _ring[tail] = sample;
                    _ringCount++;
                }
            }
            catch
            {
                // Total fault containment inside InputSystem callback.
            }
        }

        private long MapEventTimeToUnityUsLocked(double eventTimeSec)
        {
            if (!_hasTimeAnchor)
            {
                _hasTimeAnchor = true;
                _anchorEventSec = eventTimeSec;
                _anchorUnityUs = _clockSource.UnityTimeUs;
            }

            long mapped = _anchorUnityUs + (long)Math.Round((eventTimeSec - _anchorEventSec) * 1000000.0);
            if (mapped < 0L)
            {
                mapped = 0L;
            }

            if (mapped < _lastEmittedUnityUs)
            {
                mapped = _lastEmittedUnityUs;
            }
            else
            {
                _lastEmittedUnityUs = mapped;
            }

            return mapped;
        }

        private static bool TryMapPhase(UnityEngine.InputSystem.TouchPhase phase, out TouchPhaseKind kind)
        {
            switch (phase)
            {
                case UnityEngine.InputSystem.TouchPhase.Began:
                    kind = TouchPhaseKind.Began;
                    return true;
                case UnityEngine.InputSystem.TouchPhase.Moved:
                    kind = TouchPhaseKind.Moved;
                    return true;
                case UnityEngine.InputSystem.TouchPhase.Stationary:
                    kind = TouchPhaseKind.Stationary;
                    return true;
                case UnityEngine.InputSystem.TouchPhase.Ended:
                    kind = TouchPhaseKind.Ended;
                    return true;
                case UnityEngine.InputSystem.TouchPhase.Canceled:
                    kind = TouchPhaseKind.Cancelled;
                    return true;
                default:
                    kind = TouchPhaseKind.Began;
                    return false;
            }
        }
    }
}
#endif
