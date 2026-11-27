using Asparagus.Sdk.Contracts;
using Asparagus.Sdk.Internal.Time;
using Asparagus.Sdk.Internal.Tracking;

namespace Asparagus.Sdk.Internal.Capture
{
    /// <summary>
    /// Fault-contained factory selecting native mobile bridges, Unity Input System event-stream
    /// capture (<c>ENABLE_INPUT_SYSTEM</c>), Legacy Input Manager polled capture
    /// (<c>ENABLE_LEGACY_INPUT_MANAGER</c>), or safe no-op stubs.
    /// </summary>
    public static class UnityCaptureSourceFactory
    {
        public static Platform DetectRuntimePlatform()
        {
#if UNITY_EDITOR
            return Platform.Editor;
#elif UNITY_ANDROID
            return Platform.Android;
#elif UNITY_IOS
            return Platform.Ios;
#else
            return Platform.Other;
#endif
        }

        public static INativeMotionBridge CreatePlatformNativeBridge()
        {
            try
            {
#if UNITY_ANDROID && !UNITY_EDITOR
                return new AndroidNativeMotionBridge();
#elif UNITY_IOS && !UNITY_EDITOR
                return new IosNativeMotionBridge();
#else
                return null;
#endif
            }
            catch
            {
                return null;
            }
        }

        public static CompositeMotionCaptureCoordinator CreatePlatformMotionCoordinator(
            IClockSource clockSource,
            DiagnosticRecorder diagnostics = null,
            INativeMotionBridge nativeBridge = null)
        {
            Platform platform = DetectRuntimePlatform();
            INativeMotionBridge bridge = nativeBridge ?? CreatePlatformNativeBridge();
            IMotionCaptureSource unityFallback = CreateUnityMotionSource(clockSource);
            return new CompositeMotionCaptureCoordinator(platform, bridge, unityFallback, diagnostics, clockSource);
        }

        public static ISystemStateProvider CreatePlatformSystemStateProvider(INativeMotionBridge existingBridge = null)
        {
            try
            {
                if (existingBridge is ISystemStateProvider providerFromBridge)
                {
                    return providerFromBridge;
                }

                INativeMotionBridge bridge = CreatePlatformNativeBridge();
                if (bridge is ISystemStateProvider platformProvider)
                {
                    return platformProvider;
                }
            }
            catch
            {
            }

            return NullSystemStateProvider.Instance;
        }

        public static IMotionCaptureSource CreateUnityMotionSource(IClockSource clockSource)
        {
            try
            {
#if ENABLE_INPUT_SYSTEM
                return new UnityInputSystemMotionSource(clockSource);
#elif ENABLE_LEGACY_INPUT_MANAGER
                return new UnityLegacyPolledMotionSource();
#else
                return new NullMotionCaptureSource();
#endif
            }
            catch
            {
                return new NullMotionCaptureSource();
            }
        }

        public static ITouchCaptureSource CreateTouchSource(IClockSource clockSource)
        {
            try
            {
#if ENABLE_INPUT_SYSTEM
                return new UnityInputSystemTouchSource(clockSource);
#elif ENABLE_LEGACY_INPUT_MANAGER
                return new UnityLegacyPolledTouchSource();
#else
                return new NullTouchCaptureSource();
#endif
            }
            catch
            {
                return new NullTouchCaptureSource();
            }
        }

        private sealed class NullMotionCaptureSource : IMotionCaptureSource
        {
            public CaptureSource SourceKind { get { return CaptureSource.Unspecified; } }
            public bool IsRunning { get { return false; } }
            public bool HasGyroscope { get { return false; } }
            public void StartCapture(SessionSettings settings, FallbackReason fallbackReason = FallbackReason.Unspecified) { }
            public void StopCapture() { }
            public void DrainSamples(IMotionSampleConsumer consumer, long currentUnityTimeUs) { }
            public bool TryConsumeOverflowGap(out GapRecord gap) { gap = default; return false; }
        }

        private sealed class NullTouchCaptureSource : ITouchCaptureSource
        {
            public TouchMode CurrentTouchMode { get { return TouchMode.Unspecified; } }
            public bool IsRunning { get { return false; } }
            public void StartCapture(SessionSettings settings) { }
            public void StopCapture() { }
            public void SetTouchPaused(bool paused) { }
            public void DrainSamples(ITouchSampleConsumer consumer, long currentUnityTimeUs) { }
            public bool TryConsumeOverflowGap(out GapRecord gap) { gap = default; return false; }
        }

        private sealed class NullSystemStateProvider : ISystemStateProvider
        {
            public static readonly NullSystemStateProvider Instance = new NullSystemStateProvider();

            public bool TryGetScreenBrightness(out double brightness01)
            {
                brightness01 = 0d;
                return false;
            }

            public bool TryGetScreenLocked(out bool isLocked)
            {
                isLocked = false;
                return false;
            }
        }
    }
}
