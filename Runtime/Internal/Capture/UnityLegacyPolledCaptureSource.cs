#if ENABLE_LEGACY_INPUT_MANAGER
using Asparagus.Sdk.Contracts;
using UnityEngine;

namespace Asparagus.Sdk.Internal.Capture
{
    /// <summary>
    /// Legacy Input Manager polled motion capture source (<c>CAPTURE_SOURCE_UNITY_POLLED</c>)
    /// per Decision 32, W1-09 (<c>Input.acceleration</c> in g), and Decision 152 / W0-10
    /// (<c>Input.compass.rawVector</c> in microtesla).
    /// </summary>
    public sealed class UnityLegacyPolledMotionSource : IMotionCaptureSource
    {
        private bool _isRunning;
        private FallbackReason _fallbackReason;

        private bool _wantAcc;
        private bool _wantGyro;
        private bool _wantGrav;
        private bool _wantMag;

        private bool _enabledGyroBySdk;
        private bool _enabledCompassBySdk;

        public CaptureSource SourceKind
        {
            get { return CaptureSource.UnityPolled; }
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
                    return SystemInfo.supportsGyroscope;
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

                if ((_wantGyro || _wantGrav) && SystemInfo.supportsGyroscope)
                {
                    if (!Input.gyro.enabled)
                    {
                        Input.gyro.enabled = true;
                        _enabledGyroBySdk = true;
                    }
                }

                if (_wantMag)
                {
                    if (!Input.compass.enabled)
                    {
                        Input.compass.enabled = true;
                        _enabledCompassBySdk = true;
                    }
                }

                _isRunning = true;
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
                if (_enabledGyroBySdk)
                {
                    Input.gyro.enabled = false;
                    _enabledGyroBySdk = false;
                }
            }
            catch
            {
                _enabledGyroBySdk = false;
            }

            try
            {
                if (_enabledCompassBySdk)
                {
                    Input.compass.enabled = false;
                    _enabledCompassBySdk = false;
                }
            }
            catch
            {
                _enabledCompassBySdk = false;
            }
        }

        public bool TryConsumeOverflowGap(out GapRecord gap)
        {
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
                if (_wantAcc)
                {
                    Vector3 acc = Input.acceleration;
                    RawMotionSample sample = new RawMotionSample(
                        Sensor.Accelerometer,
                        CaptureSource.UnityPolled,
                        _fallbackReason,
                        Clock.Unity,
                        ValueUnit.G,
                        currentUnityTimeUs,
                        acc.x,
                        acc.y,
                        acc.z);
                    consumer.OnMotionSample(in sample);
                }

                if (SystemInfo.supportsGyroscope && Input.gyro.enabled)
                {
                    if (_wantGyro)
                    {
                        Vector3 rot = Input.gyro.rotationRate;
                        RawMotionSample sample = new RawMotionSample(
                            Sensor.Gyroscope,
                            CaptureSource.UnityPolled,
                            _fallbackReason,
                            Clock.Unity,
                            ValueUnit.RadiansPerSecond,
                            currentUnityTimeUs,
                            rot.x,
                            rot.y,
                            rot.z);
                        consumer.OnMotionSample(in sample);
                    }

                    if (_wantGrav)
                    {
                        Vector3 grav = Input.gyro.gravity;
                        RawMotionSample sample = new RawMotionSample(
                            Sensor.Gravity,
                            CaptureSource.UnityPolled,
                            _fallbackReason,
                            Clock.Unity,
                            ValueUnit.G,
                            currentUnityTimeUs,
                            grav.x,
                            grav.y,
                            grav.z);
                        consumer.OnMotionSample(in sample);
                    }
                }

                if (_wantMag && Input.compass.enabled)
                {
                    Vector3 mag = Input.compass.rawVector;
                    RawMotionSample sample = new RawMotionSample(
                        Sensor.Magnetometer,
                        CaptureSource.UnityPolled,
                        _fallbackReason,
                        Clock.Unity,
                        ValueUnit.Microtesla,
                        currentUnityTimeUs,
                        mag.x,
                        mag.y,
                        mag.z);
                    consumer.OnMotionSample(in sample);
                }
            }
            catch
            {
                // Total fault containment: never throw into host game code.
            }
        }
    }

    /// <summary>
    /// Legacy Input Manager polled touch capture source (<c>TOUCH_MODE_POLLED</c>) per Decision 32.
    /// </summary>
    public sealed class UnityLegacyPolledTouchSource : ITouchCaptureSource
    {
        private bool _isRunning;
        private bool _touchPaused;
        private bool _pauseTouchOnKeyboard;

        public TouchMode CurrentTouchMode
        {
            get { return TouchMode.Polled; }
        }

        public bool IsRunning
        {
            get { return _isRunning; }
        }

        public void StartCapture(SessionSettings settings)
        {
            SessionSettings s = settings ?? SessionSettings.CreateDefaults();
            _pauseTouchOnKeyboard = s.PauseTouchOnKeyboard;
            _isRunning = true;
        }

        public void StopCapture()
        {
            _isRunning = false;
        }

        public void SetTouchPaused(bool paused)
        {
            _touchPaused = paused;
        }

        public bool TryConsumeOverflowGap(out GapRecord gap)
        {
            gap = default;
            return false;
        }

        public void DrainSamples(ITouchSampleConsumer consumer, long currentUnityTimeUs)
        {
            if (!_isRunning || _touchPaused || consumer == null)
            {
                return;
            }

            try
            {
                if (_pauseTouchOnKeyboard && TouchScreenKeyboard.visible)
                {
                    return;
                }

                int touchCount = Input.touchCount;
                if (touchCount <= 0)
                {
                    return;
                }

                bool pressureSupported = Input.touchPressureSupported;

                for (int i = 0; i < touchCount; i++)
                {
                    UnityEngine.Touch t = Input.GetTouch(i);
                    TouchPhaseKind phase = MapPhase(t.phase);
                    uint fingerId = t.fingerId >= 0 ? (uint)t.fingerId : 0u;
                    bool hasPressure = pressureSupported && !float.IsNaN(t.pressure) && !float.IsInfinity(t.pressure);

                    RawTouchSample sample = new RawTouchSample(
                        fingerId,
                        phase,
                        currentUnityTimeUs,
                        t.position.x,
                        t.position.y,
                        t.radius,
                        t.radius,
                        hasPressure ? t.pressure : 0f,
                        hasPressure);
                    consumer.OnTouchSample(in sample);
                }
            }
            catch
            {
                // Total fault containment.
            }
        }

        private static TouchPhaseKind MapPhase(UnityEngine.TouchPhase phase)
        {
            switch (phase)
            {
                case UnityEngine.TouchPhase.Began: return TouchPhaseKind.Began;
                case UnityEngine.TouchPhase.Moved: return TouchPhaseKind.Moved;
                case UnityEngine.TouchPhase.Stationary: return TouchPhaseKind.Stationary;
                case UnityEngine.TouchPhase.Ended: return TouchPhaseKind.Ended;
                case UnityEngine.TouchPhase.Canceled: return TouchPhaseKind.Cancelled;
                default: return TouchPhaseKind.Moved;
            }
        }
    }
}
#endif
