using System;
using Asparagus.Sdk.Contracts;

namespace Asparagus.Sdk.Internal.Capture
{
    public enum NativeSensorStartResult : byte
    {
        Started = 0,
        MissingOrRefused = 1,
        Error = 2
    }

    /// <summary>
    /// Lossless, zero-unsafe packing and unpacking for the shared 3-int64 bulk-drain native format:
    /// <c>[timestampUs, (xBits &lt;&lt; 32) | yBits, (sensorId &lt;&lt; 32) | zBits]</c>.
    /// </summary>
    public static class PackedMotionRecord
    {
        public const int LongsPerSample = 3;

        public static void Pack(
            long[] buffer,
            int sampleIndex,
            Sensor sensor,
            long timestampUs,
            float x,
            float y,
            float z)
        {
            int offset = sampleIndex * LongsPerSample;
            uint xBits = unchecked((uint)BitConverter.SingleToInt32Bits(x));
            uint yBits = unchecked((uint)BitConverter.SingleToInt32Bits(y));
            uint zBits = unchecked((uint)BitConverter.SingleToInt32Bits(z));

            buffer[offset] = timestampUs;
            buffer[offset + 1] = (((long)xBits) << 32) | yBits;
            buffer[offset + 2] = (((long)(int)sensor) << 32) | zBits;
        }

        public static void Unpack(
            long[] buffer,
            int sampleIndex,
            out Sensor sensor,
            out long timestampUs,
            out float x,
            out float y,
            out float z)
        {
            int offset = sampleIndex * LongsPerSample;
            timestampUs = buffer[offset];
            long word1 = buffer[offset + 1];
            long word2 = buffer[offset + 2];

            unchecked
            {
                x = BitConverter.Int32BitsToSingle((int)(word1 >> 32));
                y = BitConverter.Int32BitsToSingle((int)word1);
                sensor = (Sensor)(int)(word2 >> 32);
                z = BitConverter.Int32BitsToSingle((int)word2);
            }
        }
    }

    /// <summary>
    /// Platform-neutral interface over Android JNI and iOS Objective-C++ off-thread sensor ring buffers.
    /// </summary>
    public interface INativeMotionBridge
    {
        CaptureSource SourceKind { get; }
        Platform NativePlatform { get; }
        bool IsPluginAvailable { get; }
        bool HasSensor(Sensor sensor);
        ValueUnit GetSensorUnit(Sensor sensor);
        NativeSensorStartResult StartSensor(Sensor sensor, uint requestedHz, uint bufferSeconds);
        void StopSensor(Sensor sensor);
        void StopAll();
        void SetUnityPaused(bool paused, uint bufferSeconds);
        bool TryGetSensorClockUs(out long sensorTimeUs);
        int DrainPackedSamples(long[] destinationPacked, int maxSamples);
        bool TryConsumeOverflow(Sensor sensor, out long startTimeUs, out long endTimeUs);
        int ConsumeErrorCount();
    }

    /// <summary>
    /// Platform provider for SDK-owned system state (<c>nx10_screen_brightness</c> and <c>nx10_screen_locked</c>).
    /// </summary>
    public interface ISystemStateProvider
    {
        bool TryGetScreenBrightness(out double brightness01);
        bool TryGetScreenLocked(out bool isLocked);
    }
}
