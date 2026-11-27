using System;
using Asparagus.Sdk.Contracts;

namespace Asparagus.Sdk.Internal.Capture
{
    public enum TouchPhaseKind : byte
    {
        Began = 0,
        Moved = 1,
        Stationary = 2,
        Ended = 3,
        Cancelled = 4
    }

    public readonly struct RawMotionSample
    {
        public readonly Sensor Sensor;
        public readonly CaptureSource Source;
        public readonly FallbackReason FallbackReason;
        public readonly Clock Clock;
        public readonly ValueUnit Unit;
        public readonly long TimeUs;
        public readonly float X;
        public readonly float Y;
        public readonly float Z;

        public RawMotionSample(
            Sensor sensor,
            CaptureSource source,
            FallbackReason fallbackReason,
            Clock clock,
            ValueUnit unit,
            long timeUs,
            float x,
            float y,
            float z)
        {
            Sensor = sensor;
            Source = source;
            FallbackReason = fallbackReason;
            Clock = clock;
            Unit = unit;
            TimeUs = timeUs;
            X = x;
            Y = y;
            Z = z;
        }
    }

    public readonly struct RawTouchSample
    {
        public readonly uint FingerId;
        public readonly TouchPhaseKind Phase;
        public readonly long TimeUs;
        public readonly float X;
        public readonly float Y;
        public readonly float SizeMajor;
        public readonly float SizeMinor;
        public readonly float Pressure;
        public readonly bool HasPressure;

        public RawTouchSample(
            uint fingerId,
            TouchPhaseKind phase,
            long timeUs,
            float x,
            float y,
            float sizeMajor,
            float sizeMinor,
            float pressure,
            bool hasPressure)
        {
            FingerId = fingerId;
            Phase = phase;
            TimeUs = timeUs;
            X = x;
            Y = y;
            SizeMajor = sizeMajor;
            SizeMinor = sizeMinor;
            Pressure = pressure;
            HasPressure = hasPressure;
        }
    }

    /// <summary>
    /// Quantization scales, overflow-safe integer quantization, and contract gap-threshold math.
    /// </summary>
    public static class SampleQuantizer
    {
        public const double AccelerometerScale = 1e-6;
        public const double GyroscopeScale = 1e-6;
        public const double GravityScale = 1e-6;
        public const double MagnetometerScale = 0.01;

        public const double TouchPositionScale = 0.1;
        public const double TouchSizeScale = 0.01;
        public const double TouchPressureScale = 0.001;

        public static double GetDefaultMotionScale(Sensor sensor)
        {
            switch (sensor)
            {
                case Sensor.Accelerometer: return AccelerometerScale;
                case Sensor.Gyroscope: return GyroscopeScale;
                case Sensor.Gravity: return GravityScale;
                case Sensor.Magnetometer: return MagnetometerScale;
                default: return 1e-6;
            }
        }

        public static int Quantize(double rawValue, double scale)
        {
            if (double.IsNaN(rawValue) || double.IsInfinity(rawValue) || scale <= 0d || double.IsNaN(scale) || double.IsInfinity(scale))
            {
                return 0;
            }

            double scaled = rawValue / scale;
            if (double.IsNaN(scaled) || double.IsInfinity(scaled))
            {
                return scaled > 0d ? int.MaxValue : (scaled < 0d ? int.MinValue : 0);
            }

            double rounded = Math.Round(scaled, MidpointRounding.AwayFromZero);
            if (rounded >= int.MaxValue)
            {
                return int.MaxValue;
            }

            if (rounded <= int.MinValue)
            {
                return int.MinValue;
            }

            return (int)rounded;
        }

        /// <summary>
        /// Computes the silence threshold in microseconds per <c>session.proto</c> lines 195-200:
        /// returns <c>0</c> (disabled) when both <paramref name="gapThresholdMs"/> is 0 and
        /// <paramref name="gapThresholdPeriods"/> (or <paramref name="requestedHz"/>) is 0;
        /// otherwise returns the larger of the millisecond threshold and the period-count threshold.
        /// A silence <c>dtUs</c> is a gap strictly when <c>thresholdUs &gt; 0 &amp;&amp; dtUs &gt; thresholdUs</c>.
        /// </summary>
        public static long ComputeGapThresholdUs(uint gapThresholdMs, uint gapThresholdPeriods, float requestedHz)
        {
            long msUs = gapThresholdMs > 0u ? (long)gapThresholdMs * 1000L : 0L;
            long periodsUs = (gapThresholdPeriods > 0u && requestedHz > 0f && !float.IsNaN(requestedHz) && !float.IsInfinity(requestedHz))
                ? (long)Math.Round((gapThresholdPeriods * 1000000.0) / requestedHz, MidpointRounding.AwayFromZero)
                : 0L;

            if (msUs <= 0L && periodsUs <= 0L)
            {
                return 0L;
            }

            return msUs >= periodsUs ? msUs : periodsUs;
        }

        public static GapSignal ToGapSignal(Sensor sensor)
        {
            switch (sensor)
            {
                case Sensor.Accelerometer: return GapSignal.Accelerometer;
                case Sensor.Gyroscope: return GapSignal.Gyroscope;
                case Sensor.Gravity: return GapSignal.Gravity;
                case Sensor.Magnetometer: return GapSignal.Magnetometer;
                default: return GapSignal.Unspecified;
            }
        }
    }
}
