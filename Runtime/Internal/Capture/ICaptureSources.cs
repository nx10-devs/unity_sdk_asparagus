using Asparagus.Sdk.Contracts;

namespace Asparagus.Sdk.Internal.Capture
{
    public interface IMotionSampleConsumer
    {
        void OnMotionSample(in RawMotionSample sample);
    }

    public interface ITouchSampleConsumer
    {
        void OnTouchSample(in RawTouchSample sample);
    }

    public interface IMotionCaptureSource
    {
        CaptureSource SourceKind { get; }
        bool IsRunning { get; }
        bool HasGyroscope { get; }
        void StartCapture(SessionSettings settings, FallbackReason fallbackReason = FallbackReason.Unspecified);
        void StopCapture();
        void DrainSamples(IMotionSampleConsumer consumer, long currentUnityTimeUs);
        bool TryConsumeOverflowGap(out GapRecord gap);
    }

    public interface ITouchCaptureSource
    {
        TouchMode CurrentTouchMode { get; }
        bool IsRunning { get; }
        void StartCapture(SessionSettings settings);
        void StopCapture();
        void SetTouchPaused(bool paused);
        void DrainSamples(ITouchSampleConsumer consumer, long currentUnityTimeUs);
        bool TryConsumeOverflowGap(out GapRecord gap);
    }
}
