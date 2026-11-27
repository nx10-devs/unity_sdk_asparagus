using Asparagus.Sdk.Contracts;
#if UNITY_IOS && !UNITY_EDITOR
using System.Runtime.InteropServices;
#endif

namespace Asparagus.Sdk.Internal.Capture
{
    /// <summary>
    /// Zero-allocation P/Invoke iOS native sensor bridge and system-state provider backed by
    /// <c>AsparagusSensorPlugin.mm</c>.
    /// </summary>
    public sealed class IosNativeMotionBridge : INativeMotionBridge, ISystemStateProvider
    {
#if UNITY_IOS && !UNITY_EDITOR
        [DllImport("__Internal")]
        [return: MarshalAs(UnmanagedType.I1)]
        private static extern bool AsparagusIos_IsAvailable();

        [DllImport("__Internal")]
        [return: MarshalAs(UnmanagedType.I1)]
        private static extern bool AsparagusIos_HasSensor(int sensorId);

        [DllImport("__Internal")]
        private static extern int AsparagusIos_StartSensor(int sensorId, int requestedHz, int bufferSeconds);

        [DllImport("__Internal")]
        private static extern void AsparagusIos_StopSensor(int sensorId);

        [DllImport("__Internal")]
        private static extern void AsparagusIos_StopAll();

        [DllImport("__Internal")]
        private static extern void AsparagusIos_SetUnityPaused(
            [MarshalAs(UnmanagedType.I1)] bool paused,
            int bufferSeconds);

        [DllImport("__Internal")]
        private static extern long AsparagusIos_GetSensorClockUs();

        [DllImport("__Internal")]
        private static extern int AsparagusIos_DrainPackedSamples(long[] outPackedBuffer, int maxSamples);

        [DllImport("__Internal")]
        [return: MarshalAs(UnmanagedType.I1)]
        private static extern bool AsparagusIos_ConsumeOverflow(int sensorId, out long outStartUs, out long outEndUs);

        [DllImport("__Internal")]
        private static extern int AsparagusIos_ConsumeErrorCount();

        [DllImport("__Internal")]
        private static extern float AsparagusIos_GetScreenBrightness();

        [DllImport("__Internal")]
        private static extern int AsparagusIos_GetScreenLockedState();
#endif

        public CaptureSource SourceKind
        {
            get { return CaptureSource.NativeIos; }
        }

        public Platform NativePlatform
        {
            get { return Platform.Ios; }
        }

        public bool IsPluginAvailable
        {
            get
            {
#if UNITY_IOS && !UNITY_EDITOR
                try
                {
                    return AsparagusIos_IsAvailable();
                }
                catch
                {
                    return false;
                }
#else
                return false;
#endif
            }
        }

        public ValueUnit GetSensorUnit(Sensor sensor)
        {
            switch (sensor)
            {
                case Sensor.Accelerometer: return ValueUnit.G;
                case Sensor.Gyroscope: return ValueUnit.RadiansPerSecond;
                case Sensor.Gravity: return ValueUnit.G;
                case Sensor.Magnetometer: return ValueUnit.Microtesla;
                default: return ValueUnit.Unspecified;
            }
        }

        public bool HasSensor(Sensor sensor)
        {
#if UNITY_IOS && !UNITY_EDITOR
            try
            {
                return AsparagusIos_HasSensor((int)sensor);
            }
            catch
            {
                return false;
            }
#else
            return false;
#endif
        }

        public NativeSensorStartResult StartSensor(Sensor sensor, uint requestedHz, uint bufferSeconds)
        {
#if UNITY_IOS && !UNITY_EDITOR
            try
            {
                int hz = requestedHz > int.MaxValue ? int.MaxValue : (int)requestedHz;
                int bufS = bufferSeconds > int.MaxValue ? int.MaxValue : (int)bufferSeconds;
                int status = AsparagusIos_StartSensor((int)sensor, hz, bufS);
                if (status == 1) return NativeSensorStartResult.Started;
                if (status == 0) return NativeSensorStartResult.MissingOrRefused;
                return NativeSensorStartResult.Error;
            }
            catch
            {
                return NativeSensorStartResult.Error;
            }
#else
            return NativeSensorStartResult.Error;
#endif
        }

        public void StopSensor(Sensor sensor)
        {
#if UNITY_IOS && !UNITY_EDITOR
            try
            {
                AsparagusIos_StopSensor((int)sensor);
            }
            catch
            {
            }
#endif
        }

        public void StopAll()
        {
#if UNITY_IOS && !UNITY_EDITOR
            try
            {
                AsparagusIos_StopAll();
            }
            catch
            {
            }
#endif
        }

        public void SetUnityPaused(bool paused, uint bufferSeconds)
        {
#if UNITY_IOS && !UNITY_EDITOR
            try
            {
                int bufS = bufferSeconds > int.MaxValue ? int.MaxValue : (int)bufferSeconds;
                AsparagusIos_SetUnityPaused(paused, bufS);
            }
            catch
            {
            }
#endif
        }

        public bool TryGetSensorClockUs(out long sensorTimeUs)
        {
#if UNITY_IOS && !UNITY_EDITOR
            try
            {
                sensorTimeUs = AsparagusIos_GetSensorClockUs();
                return true;
            }
            catch
            {
                sensorTimeUs = 0L;
                return false;
            }
#else
            sensorTimeUs = 0L;
            return false;
#endif
        }

        public int DrainPackedSamples(long[] destinationPacked, int maxSamples)
        {
#if UNITY_IOS && !UNITY_EDITOR
            if (destinationPacked == null || maxSamples <= 0)
            {
                return 0;
            }

            int capSamples = destinationPacked.Length / PackedMotionRecord.LongsPerSample;
            if (capSamples <= 0)
            {
                return 0;
            }

            int limit = maxSamples < capSamples ? maxSamples : capSamples;
            try
            {
                return AsparagusIos_DrainPackedSamples(destinationPacked, limit);
            }
            catch
            {
                return -1;
            }
#else
            return 0;
#endif
        }

        public bool TryConsumeOverflow(Sensor sensor, out long startTimeUs, out long endTimeUs)
        {
#if UNITY_IOS && !UNITY_EDITOR
            try
            {
                return AsparagusIos_ConsumeOverflow((int)sensor, out startTimeUs, out endTimeUs);
            }
            catch
            {
                startTimeUs = 0L;
                endTimeUs = 0L;
                return false;
            }
#else
            startTimeUs = 0L;
            endTimeUs = 0L;
            return false;
#endif
        }

        public int ConsumeErrorCount()
        {
#if UNITY_IOS && !UNITY_EDITOR
            try
            {
                int count = AsparagusIos_ConsumeErrorCount();
                return count > 0 ? count : 0;
            }
            catch
            {
                return 1;
            }
#else
            return 0;
#endif
        }

        public bool TryGetScreenBrightness(out double brightness01)
        {
#if UNITY_IOS && !UNITY_EDITOR
            try
            {
                float b = AsparagusIos_GetScreenBrightness();
                if (float.IsNaN(b) || float.IsInfinity(b) || b < 0f)
                {
                    brightness01 = 0d;
                    return false;
                }

                brightness01 = b > 1f ? 1d : (double)b;
                return true;
            }
            catch
            {
                brightness01 = 0d;
                return false;
            }
#else
            brightness01 = 0d;
            return false;
#endif
        }

        public bool TryGetScreenLocked(out bool isLocked)
        {
#if UNITY_IOS && !UNITY_EDITOR
            try
            {
                int state = AsparagusIos_GetScreenLockedState();
                if (state < 0)
                {
                    isLocked = false;
                    return false;
                }

                isLocked = state != 0;
                return true;
            }
            catch
            {
                isLocked = false;
                return false;
            }
#else
            isLocked = false;
            return false;
#endif
        }
    }
}
