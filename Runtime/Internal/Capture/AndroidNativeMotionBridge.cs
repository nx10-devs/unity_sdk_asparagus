using System;
using Asparagus.Sdk.Contracts;
#if UNITY_ANDROID && !UNITY_EDITOR
using UnityEngine;
#endif

namespace Asparagus.Sdk.Internal.Capture
{
    /// <summary>
    /// Cached-JNI Android native sensor bridge and system-state provider backed by
    /// <c>com.asparagus.sdk.AsparagusSensorPlugin</c>. Uses pre-allocated <c>jvalue[]</c> buffers
    /// and cached method IDs so steady-state polling allocates zero managed bytes.
    /// </summary>
    public sealed class AndroidNativeMotionBridge : INativeMotionBridge, ISystemStateProvider
    {
#if UNITY_ANDROID && !UNITY_EDITOR
        private const string PluginClassName = "com/asparagus/sdk/AsparagusSensorPlugin";

        private bool _initAttempted;
        private bool _available;
        private IntPtr _pluginClass;

        private IntPtr _mIsAvailable;
        private IntPtr _mHasSensor;
        private IntPtr _mStartSensor;
        private IntPtr _mStopSensor;
        private IntPtr _mStopAll;
        private IntPtr _mSetUnityPaused;
        private IntPtr _mGetSensorClockUs;
        private IntPtr _mGetPendingSampleCount;
        private IntPtr _mDrainPackedSamples;
        private IntPtr _mHasOverflow;
        private IntPtr _mConsumeOverflowRange;
        private IntPtr _mConsumeErrorCount;
        private IntPtr _mGetScreenBrightness;
        private IntPtr _mGetScreenLockedState;

        private readonly jvalue[] _args0 = new jvalue[0];
        private readonly jvalue[] _args1 = new jvalue[1];
        private readonly jvalue[] _args2 = new jvalue[2];
        private readonly jvalue[] _args3 = new jvalue[3];
#endif

        public CaptureSource SourceKind
        {
            get { return CaptureSource.NativeAndroid; }
        }

        public Platform NativePlatform
        {
            get { return Platform.Android; }
        }

        public bool IsPluginAvailable
        {
            get
            {
#if UNITY_ANDROID && !UNITY_EDITOR
                EnsureInitialized();
                if (!_available)
                {
                    return false;
                }

                try
                {
                    bool result = AndroidJNI.CallStaticBooleanMethod(_pluginClass, _mIsAvailable, _args0);
                    return !ClearJniException() && result;
                }
                catch
                {
                    ClearJniException();
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
                case Sensor.Accelerometer: return ValueUnit.MetresPerSecondSquared;
                case Sensor.Gyroscope: return ValueUnit.RadiansPerSecond;
                case Sensor.Gravity: return ValueUnit.MetresPerSecondSquared;
                case Sensor.Magnetometer: return ValueUnit.Microtesla;
                default: return ValueUnit.Unspecified;
            }
        }

        public bool HasSensor(Sensor sensor)
        {
#if UNITY_ANDROID && !UNITY_EDITOR
            EnsureInitialized();
            if (!_available)
            {
                return false;
            }

            try
            {
                _args1[0].i = (int)sensor;
                bool result = AndroidJNI.CallStaticBooleanMethod(_pluginClass, _mHasSensor, _args1);
                return !ClearJniException() && result;
            }
            catch
            {
                ClearJniException();
                return false;
            }
#else
            return false;
#endif
        }

        public NativeSensorStartResult StartSensor(Sensor sensor, uint requestedHz, uint bufferSeconds)
        {
#if UNITY_ANDROID && !UNITY_EDITOR
            EnsureInitialized();
            if (!_available)
            {
                return NativeSensorStartResult.Error;
            }

            try
            {
                _args3[0].i = (int)sensor;
                _args3[1].i = requestedHz > int.MaxValue ? int.MaxValue : (int)requestedHz;
                _args3[2].i = bufferSeconds > int.MaxValue ? int.MaxValue : (int)bufferSeconds;
                int status = AndroidJNI.CallStaticIntMethod(_pluginClass, _mStartSensor, _args3);
                if (ClearJniException())
                {
                    return NativeSensorStartResult.Error;
                }

                if (status == 1) return NativeSensorStartResult.Started;
                if (status == 0) return NativeSensorStartResult.MissingOrRefused;
                return NativeSensorStartResult.Error;
            }
            catch
            {
                ClearJniException();
                return NativeSensorStartResult.Error;
            }
#else
            return NativeSensorStartResult.Error;
#endif
        }

        public void StopSensor(Sensor sensor)
        {
#if UNITY_ANDROID && !UNITY_EDITOR
            EnsureInitialized();
            if (!_available)
            {
                return;
            }

            try
            {
                _args1[0].i = (int)sensor;
                AndroidJNI.CallStaticVoidMethod(_pluginClass, _mStopSensor, _args1);
                ClearJniException();
            }
            catch
            {
                ClearJniException();
            }
#endif
        }

        public void StopAll()
        {
#if UNITY_ANDROID && !UNITY_EDITOR
            EnsureInitialized();
            if (!_available)
            {
                return;
            }

            try
            {
                AndroidJNI.CallStaticVoidMethod(_pluginClass, _mStopAll, _args0);
                ClearJniException();
            }
            catch
            {
                ClearJniException();
            }
#endif
        }

        public void SetUnityPaused(bool paused, uint bufferSeconds)
        {
#if UNITY_ANDROID && !UNITY_EDITOR
            EnsureInitialized();
            if (!_available)
            {
                return;
            }

            try
            {
                _args2[0].z = paused;
                _args2[1].i = bufferSeconds > int.MaxValue ? int.MaxValue : (int)bufferSeconds;
                AndroidJNI.CallStaticVoidMethod(_pluginClass, _mSetUnityPaused, _args2);
                ClearJniException();
            }
            catch
            {
                ClearJniException();
            }
#endif
        }

        public bool TryGetSensorClockUs(out long sensorTimeUs)
        {
#if UNITY_ANDROID && !UNITY_EDITOR
            EnsureInitialized();
            if (!_available)
            {
                sensorTimeUs = 0L;
                return false;
            }

            try
            {
                long ts = AndroidJNI.CallStaticLongMethod(_pluginClass, _mGetSensorClockUs, _args0);
                if (ClearJniException())
                {
                    sensorTimeUs = 0L;
                    return false;
                }

                sensorTimeUs = ts;
                return true;
            }
            catch
            {
                ClearJniException();
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
#if UNITY_ANDROID && !UNITY_EDITOR
            EnsureInitialized();
            if (!_available || destinationPacked == null || maxSamples <= 0)
            {
                return 0;
            }

            int capacitySamples = destinationPacked.Length / PackedMotionRecord.LongsPerSample;
            if (capacitySamples <= 0)
            {
                return 0;
            }

            int limit = maxSamples < capacitySamples ? maxSamples : capacitySamples;
            IntPtr localArray = IntPtr.Zero;

            try
            {
                int pending = AndroidJNI.CallStaticIntMethod(_pluginClass, _mGetPendingSampleCount, _args0);
                if (ClearJniException())
                {
                    return -1;
                }

                if (pending <= 0)
                {
                    return 0;
                }

                _args1[0].i = limit;
                localArray = AndroidJNI.CallStaticObjectMethod(_pluginClass, _mDrainPackedSamples, _args1);
                if (ClearJniException() || localArray == IntPtr.Zero)
                {
                    return -1;
                }

                long[] drained = AndroidJNI.FromLongArray(localArray);
                if (drained == null || drained.Length < PackedMotionRecord.LongsPerSample)
                {
                    return 0;
                }

                int sampleCount = drained.Length / PackedMotionRecord.LongsPerSample;
                if (sampleCount > limit)
                {
                    sampleCount = limit;
                }

                Array.Copy(drained, 0, destinationPacked, 0, sampleCount * PackedMotionRecord.LongsPerSample);
                return sampleCount;
            }
            catch
            {
                ClearJniException();
                return -1;
            }
            finally
            {
                if (localArray != IntPtr.Zero)
                {
                    try
                    {
                        AndroidJNI.DeleteLocalRef(localArray);
                    }
                    catch
                    {
                    }
                }
            }
#else
            return 0;
#endif
        }

        public bool TryConsumeOverflow(Sensor sensor, out long startTimeUs, out long endTimeUs)
        {
#if UNITY_ANDROID && !UNITY_EDITOR
            startTimeUs = 0L;
            endTimeUs = 0L;
            EnsureInitialized();
            if (!_available)
            {
                return false;
            }

            IntPtr localArray = IntPtr.Zero;
            try
            {
                _args1[0].i = (int)sensor;
                bool hasOv = AndroidJNI.CallStaticBooleanMethod(_pluginClass, _mHasOverflow, _args1);
                if (ClearJniException() || !hasOv)
                {
                    return false;
                }

                localArray = AndroidJNI.CallStaticObjectMethod(_pluginClass, _mConsumeOverflowRange, _args1);
                if (ClearJniException() || localArray == IntPtr.Zero)
                {
                    return false;
                }

                long[] range = AndroidJNI.FromLongArray(localArray);
                if (range == null || range.Length < 3 || range[0] <= 0L)
                {
                    return false;
                }

                startTimeUs = range[1];
                endTimeUs = range[2];
                return true;
            }
            catch
            {
                ClearJniException();
                return false;
            }
            finally
            {
                if (localArray != IntPtr.Zero)
                {
                    try
                    {
                        AndroidJNI.DeleteLocalRef(localArray);
                    }
                    catch
                    {
                    }
                }
            }
#else
            startTimeUs = 0L;
            endTimeUs = 0L;
            return false;
#endif
        }

        public int ConsumeErrorCount()
        {
#if UNITY_ANDROID && !UNITY_EDITOR
            EnsureInitialized();
            if (!_available)
            {
                return 0;
            }

            try
            {
                int count = AndroidJNI.CallStaticIntMethod(_pluginClass, _mConsumeErrorCount, _args0);
                if (ClearJniException())
                {
                    return 1;
                }

                return count > 0 ? count : 0;
            }
            catch
            {
                ClearJniException();
                return 1;
            }
#else
            return 0;
#endif
        }

        public bool TryGetScreenBrightness(out double brightness01)
        {
#if UNITY_ANDROID && !UNITY_EDITOR
            brightness01 = 0d;
            EnsureInitialized();
            if (!_available)
            {
                return false;
            }

            try
            {
                float b = AndroidJNI.CallStaticFloatMethod(_pluginClass, _mGetScreenBrightness, _args0);
                if (ClearJniException() || float.IsNaN(b) || float.IsInfinity(b) || b < 0f)
                {
                    return false;
                }

                brightness01 = b > 1f ? 1d : (double)b;
                return true;
            }
            catch
            {
                ClearJniException();
                return false;
            }
#else
            brightness01 = 0d;
            return false;
#endif
        }

        public bool TryGetScreenLocked(out bool isLocked)
        {
#if UNITY_ANDROID && !UNITY_EDITOR
            isLocked = false;
            EnsureInitialized();
            if (!_available)
            {
                return false;
            }

            try
            {
                int state = AndroidJNI.CallStaticIntMethod(_pluginClass, _mGetScreenLockedState, _args0);
                if (ClearJniException() || state < 0)
                {
                    return false;
                }

                isLocked = state != 0;
                return true;
            }
            catch
            {
                ClearJniException();
                return false;
            }
#else
            isLocked = false;
            return false;
#endif
        }

#if UNITY_ANDROID && !UNITY_EDITOR
        private void EnsureInitialized()
        {
            if (_initAttempted)
            {
                return;
            }

            _initAttempted = true;
            IntPtr localClass = IntPtr.Zero;

            try
            {
                localClass = AndroidJNI.FindClass(PluginClassName);
                if (ClearJniException() || localClass == IntPtr.Zero)
                {
                    return;
                }

                _pluginClass = AndroidJNI.NewGlobalRef(localClass);
                if (_pluginClass == IntPtr.Zero)
                {
                    return;
                }

                _mIsAvailable = AndroidJNI.GetStaticMethodID(_pluginClass, "isAvailable", "()Z");
                _mHasSensor = AndroidJNI.GetStaticMethodID(_pluginClass, "hasSensor", "(I)Z");
                _mStartSensor = AndroidJNI.GetStaticMethodID(_pluginClass, "startSensor", "(III)I");
                _mStopSensor = AndroidJNI.GetStaticMethodID(_pluginClass, "stopSensor", "(I)V");
                _mStopAll = AndroidJNI.GetStaticMethodID(_pluginClass, "stopAll", "()V");
                _mSetUnityPaused = AndroidJNI.GetStaticMethodID(_pluginClass, "setUnityPaused", "(ZI)V");
                _mGetSensorClockUs = AndroidJNI.GetStaticMethodID(_pluginClass, "getSensorClockUs", "()J");
                _mGetPendingSampleCount = AndroidJNI.GetStaticMethodID(_pluginClass, "getPendingSampleCount", "()I");
                _mDrainPackedSamples = AndroidJNI.GetStaticMethodID(_pluginClass, "drainPackedSamples", "(I)[J");
                _mHasOverflow = AndroidJNI.GetStaticMethodID(_pluginClass, "hasOverflow", "(I)Z");
                _mConsumeOverflowRange = AndroidJNI.GetStaticMethodID(_pluginClass, "consumeOverflowRange", "(I)[J");
                _mConsumeErrorCount = AndroidJNI.GetStaticMethodID(_pluginClass, "consumeErrorCount", "()I");
                _mGetScreenBrightness = AndroidJNI.GetStaticMethodID(_pluginClass, "getScreenBrightness", "()F");
                _mGetScreenLockedState = AndroidJNI.GetStaticMethodID(_pluginClass, "getScreenLockedState", "()I");

                if (ClearJniException())
                {
                    return;
                }

                _available = _mIsAvailable != IntPtr.Zero
                    && _mHasSensor != IntPtr.Zero
                    && _mStartSensor != IntPtr.Zero
                    && _mStopSensor != IntPtr.Zero
                    && _mStopAll != IntPtr.Zero
                    && _mSetUnityPaused != IntPtr.Zero
                    && _mGetSensorClockUs != IntPtr.Zero
                    && _mGetPendingSampleCount != IntPtr.Zero
                    && _mDrainPackedSamples != IntPtr.Zero
                    && _mHasOverflow != IntPtr.Zero
                    && _mConsumeOverflowRange != IntPtr.Zero
                    && _mConsumeErrorCount != IntPtr.Zero
                    && _mGetScreenBrightness != IntPtr.Zero
                    && _mGetScreenLockedState != IntPtr.Zero;
            }
            catch
            {
                ClearJniException();
                _available = false;
            }
            finally
            {
                if (localClass != IntPtr.Zero)
                {
                    try
                    {
                        AndroidJNI.DeleteLocalRef(localClass);
                    }
                    catch
                    {
                    }
                }
            }
        }

        private static bool ClearJniException()
        {
            IntPtr exc = AndroidJNI.ExceptionOccurred();
            if (exc != IntPtr.Zero)
            {
                AndroidJNI.ExceptionClear();
                AndroidJNI.DeleteLocalRef(exc);
                return true;
            }

            return false;
        }
#endif
    }
}
