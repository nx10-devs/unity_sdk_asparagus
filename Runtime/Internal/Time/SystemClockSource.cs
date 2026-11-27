using System;
using System.Diagnostics;
using System.Threading;

namespace Asparagus.Sdk.Internal.Time
{
    /// <summary>
    /// Thread-safe production <see cref="IClockSource"/> backed by <see cref="Stopwatch"/> with
    /// integer quotient/remainder microsecond conversion to avoid floating-point drift over long runs.
    /// </summary>
    public sealed class SystemClockSource : IClockSource
    {
        private const long MicrosecondsPerSecond = 1000000L;
        private const long MaxSafeRemainderFrequency = 9000000000000L;

        private readonly long _originTicks;
        private readonly long _frequency;
        private ISensorClockProvider _sensorClockProvider;

        public SystemClockSource(ISensorClockProvider sensorClockProvider = null)
        {
            _originTicks = Stopwatch.GetTimestamp();
            long freq = Stopwatch.Frequency;
            _frequency = freq > 0 ? freq : MicrosecondsPerSecond;
            _sensorClockProvider = sensorClockProvider;
        }

        public void SetSensorClockProvider(ISensorClockProvider provider)
        {
            Volatile.Write(ref _sensorClockProvider, provider);
        }

        public long SteadyTimeUs
        {
            get
            {
                long delta = Stopwatch.GetTimestamp() - _originTicks;
                return delta <= 0 ? 0L : TicksToMicroseconds(delta, _frequency);
            }
        }

        public long UnityTimeUs
        {
            get { return SteadyTimeUs; }
        }

        public long WallUtcMs
        {
            get { return DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(); }
        }

        public int UtcOffsetMinutes
        {
            get
            {
                try
                {
                    TimeSpan offset = TimeZoneInfo.Local.GetUtcOffset(DateTime.UtcNow);
                    return (int)Math.Round(offset.TotalMinutes);
                }
                catch
                {
                    return 0;
                }
            }
        }

        public bool TryGetSensorTimeUs(out long sensorUs)
        {
            ISensorClockProvider provider = Volatile.Read(ref _sensorClockProvider);
            if (provider != null)
            {
                try
                {
                    return provider.TryGetSensorTimeUs(out sensorUs);
                }
                catch
                {
                    sensorUs = 0L;
                    return false;
                }
            }

            sensorUs = 0L;
            return false;
        }

        public static long TicksToMicroseconds(long deltaTicks, long frequency)
        {
            if (deltaTicks <= 0 || frequency <= 0)
            {
                return 0L;
            }

            long wholeSeconds = deltaTicks / frequency;
            long remTicks = deltaTicks % frequency;
            long remMicros;
            if (frequency <= MaxSafeRemainderFrequency)
            {
                remMicros = (remTicks * MicrosecondsPerSecond) / frequency;
            }
            else
            {
                remMicros = (long)(((decimal)remTicks * MicrosecondsPerSecond) / frequency);
            }

            return wholeSeconds * MicrosecondsPerSecond + remMicros;
        }
    }
}
