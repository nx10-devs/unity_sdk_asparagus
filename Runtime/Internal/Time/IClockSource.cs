namespace Asparagus.Sdk.Internal.Time
{
    /// <summary>
    /// Optional provider for native sensor clock readings (<c>CLOCK_SENSOR</c>).
    /// </summary>
    public interface ISensorClockProvider
    {
        bool TryGetSensorTimeUs(out long sensorUs);
    }

    /// <summary>
    /// Abstraction over monotonic, Unity event, wall UTC, timezone offset, and optional native sensor clocks.
    /// </summary>
    public interface IClockSource
    {
        /// <summary>
        /// Monotonic microseconds from an arbitrary origin used to carry the session clock forward.
        /// </summary>
        long SteadyTimeUs { get; }

        /// <summary>
        /// <c>CLOCK_UNITY</c> microseconds since app/SDK clock initialization (monotonic, thread-safe).
        /// </summary>
        long UnityTimeUs { get; }

        /// <summary>
        /// Device wall-clock UTC milliseconds since 1970-01-01.
        /// </summary>
        long WallUtcMs { get; }

        /// <summary>
        /// Device local offset from UTC in minutes (east of Greenwich is positive; UTC is 0).
        /// </summary>
        int UtcOffsetMinutes { get; }

        /// <summary>
        /// Returns <c>true</c> with <c>CLOCK_SENSOR</c> microseconds only while a native sensor source is active.
        /// </summary>
        bool TryGetSensorTimeUs(out long sensorUs);
    }
}
