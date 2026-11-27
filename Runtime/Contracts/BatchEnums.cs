using System.Collections.Generic;

namespace Asparagus.Sdk.Contracts
{
    public enum Platform
    {
        Unspecified = 0,
        Android = 1,
        Ios = 2,
        Editor = 3,
        Other = 4
    }

    public enum CloseReason
    {
        Unspecified = 0,
        WindowFull = 1,
        Paused = 2,
        Quit = 3,
        CaptureStopped = 4,
        SessionEnded = 5
    }

    public enum Clock
    {
        Unspecified = 0,
        Session = 1,
        Unity = 2,
        Sensor = 3
    }

    public enum Sensor
    {
        Unspecified = 0,
        Accelerometer = 1,
        Gyroscope = 2,
        Gravity = 3,
        Magnetometer = 4
    }

    public enum CaptureSource
    {
        Unspecified = 0,
        NativeAndroid = 1,
        NativeIos = 2,
        UnityEvents = 3,
        UnityPolled = 4
    }

    public enum FallbackReason
    {
        Unspecified = 0,
        PluginMissing = 1,
        SensorRefused = 2,
        NoFirstSample = 3,
        SamplesStopped = 4,
        NativeError = 5,
        ForcedBySettings = 6
    }

    public enum ValueUnit
    {
        Unspecified = 0,
        MetresPerSecondSquared = 1,
        G = 2,
        RadiansPerSecond = 3,
        Microtesla = 4
    }

    public enum TouchMode
    {
        Unspecified = 0,
        Events = 1,
        EventsMerged = 2,
        Polled = 3
    }

    public enum TouchEnd
    {
        Unspecified = 0,
        StillDown = 1,
        Lifted = 2,
        Cancelled = 3
    }

    public enum Orientation
    {
        Unspecified = 0,
        Portrait = 1,
        PortraitUpsideDown = 2,
        LandscapeLeft = 3,
        LandscapeRight = 4
    }

    public enum CoordinateOrigin
    {
        Unspecified = 0,
        BottomLeft = 1,
        TopLeft = 2
    }

    public enum SpanMarker
    {
        Unspecified = 0,
        Open = 1,
        Close = 2
    }

    public enum StateKind
    {
        Unspecified = 0,
        Put = 1,
        Patch = 2
    }

    public enum LifecycleKind
    {
        Unspecified = 0,
        Paused = 1,
        Resumed = 2,
        FocusLost = 3,
        FocusGained = 4,
        Quitting = 5,
        CaptureStoppedByGame = 6,
        CaptureStartedByGame = 7,
        ClockStepped = 8
    }

    public enum GapSignal
    {
        Unspecified = 0,
        Accelerometer = 1,
        Gyroscope = 2,
        Gravity = 3,
        Touch = 4,
        Everything = 5,
        Magnetometer = 6
    }

    public enum GapCause
    {
        Unspecified = 0,
        SensorSilent = 1,
        BufferOverflowed = 2,
        BatchesDropped = 3,
        CaptureStopped = 4
    }

    public enum Severity
    {
        Unspecified = 0,
        Warning = 1,
        Error = 2
    }

    public enum UploadStatus
    {
        Unspecified = 0,
        Stored = 1
    }

    public enum MotionSource
    {
        Unspecified = 0,
        Native = 1,
        Unity = 2
    }

    /// <summary>
    /// Zero-allocation Protobuf enum name conversions for Schema v3 batch.proto and session.proto enums.
    /// </summary>
    public static class ProtoEnumNames
    {
        public static string ToProtoName(this Platform value)
        {
            switch (value)
            {
                case Platform.Unspecified: return "PLATFORM_UNSPECIFIED";
                case Platform.Android: return "PLATFORM_ANDROID";
                case Platform.Ios: return "PLATFORM_IOS";
                case Platform.Editor: return "PLATFORM_EDITOR";
                case Platform.Other: return "PLATFORM_OTHER";
                default: return "PLATFORM_UNSPECIFIED";
            }
        }

        public static bool TryParsePlatform(string name, out Platform value)
        {
            switch (name)
            {
                case "PLATFORM_UNSPECIFIED": value = Platform.Unspecified; return true;
                case "PLATFORM_ANDROID": value = Platform.Android; return true;
                case "PLATFORM_IOS": value = Platform.Ios; return true;
                case "PLATFORM_EDITOR": value = Platform.Editor; return true;
                case "PLATFORM_OTHER": value = Platform.Other; return true;
                default: value = Platform.Unspecified; return false;
            }
        }

        public static string ToProtoName(this CloseReason value)
        {
            switch (value)
            {
                case CloseReason.Unspecified: return "CLOSE_REASON_UNSPECIFIED";
                case CloseReason.WindowFull: return "CLOSE_REASON_WINDOW_FULL";
                case CloseReason.Paused: return "CLOSE_REASON_PAUSED";
                case CloseReason.Quit: return "CLOSE_REASON_QUIT";
                case CloseReason.CaptureStopped: return "CLOSE_REASON_CAPTURE_STOPPED";
                case CloseReason.SessionEnded: return "CLOSE_REASON_SESSION_ENDED";
                default: return "CLOSE_REASON_UNSPECIFIED";
            }
        }

        public static bool TryParseCloseReason(string name, out CloseReason value)
        {
            switch (name)
            {
                case "CLOSE_REASON_UNSPECIFIED": value = CloseReason.Unspecified; return true;
                case "CLOSE_REASON_WINDOW_FULL": value = CloseReason.WindowFull; return true;
                case "CLOSE_REASON_PAUSED": value = CloseReason.Paused; return true;
                case "CLOSE_REASON_QUIT": value = CloseReason.Quit; return true;
                case "CLOSE_REASON_CAPTURE_STOPPED": value = CloseReason.CaptureStopped; return true;
                case "CLOSE_REASON_SESSION_ENDED": value = CloseReason.SessionEnded; return true;
                default: value = CloseReason.Unspecified; return false;
            }
        }

        public static string ToProtoName(this Clock value)
        {
            switch (value)
            {
                case Clock.Unspecified: return "CLOCK_UNSPECIFIED";
                case Clock.Session: return "CLOCK_SESSION";
                case Clock.Unity: return "CLOCK_UNITY";
                case Clock.Sensor: return "CLOCK_SENSOR";
                default: return "CLOCK_UNSPECIFIED";
            }
        }

        public static bool TryParseClock(string name, out Clock value)
        {
            switch (name)
            {
                case "CLOCK_UNSPECIFIED": value = Clock.Unspecified; return true;
                case "CLOCK_SESSION": value = Clock.Session; return true;
                case "CLOCK_UNITY": value = Clock.Unity; return true;
                case "CLOCK_SENSOR": value = Clock.Sensor; return true;
                default: value = Clock.Unspecified; return false;
            }
        }

        public static string ToProtoName(this Sensor value)
        {
            switch (value)
            {
                case Sensor.Unspecified: return "SENSOR_UNSPECIFIED";
                case Sensor.Accelerometer: return "SENSOR_ACCELEROMETER";
                case Sensor.Gyroscope: return "SENSOR_GYROSCOPE";
                case Sensor.Gravity: return "SENSOR_GRAVITY";
                case Sensor.Magnetometer: return "SENSOR_MAGNETOMETER";
                default: return "SENSOR_UNSPECIFIED";
            }
        }

        public static bool TryParseSensor(string name, out Sensor value)
        {
            switch (name)
            {
                case "SENSOR_UNSPECIFIED": value = Sensor.Unspecified; return true;
                case "SENSOR_ACCELEROMETER": value = Sensor.Accelerometer; return true;
                case "SENSOR_GYROSCOPE": value = Sensor.Gyroscope; return true;
                case "SENSOR_GRAVITY": value = Sensor.Gravity; return true;
                case "SENSOR_MAGNETOMETER": value = Sensor.Magnetometer; return true;
                default: value = Sensor.Unspecified; return false;
            }
        }

        public static string ToProtoName(this CaptureSource value)
        {
            switch (value)
            {
                case CaptureSource.Unspecified: return "CAPTURE_SOURCE_UNSPECIFIED";
                case CaptureSource.NativeAndroid: return "CAPTURE_SOURCE_NATIVE_ANDROID";
                case CaptureSource.NativeIos: return "CAPTURE_SOURCE_NATIVE_IOS";
                case CaptureSource.UnityEvents: return "CAPTURE_SOURCE_UNITY_EVENTS";
                case CaptureSource.UnityPolled: return "CAPTURE_SOURCE_UNITY_POLLED";
                default: return "CAPTURE_SOURCE_UNSPECIFIED";
            }
        }

        public static bool TryParseCaptureSource(string name, out CaptureSource value)
        {
            switch (name)
            {
                case "CAPTURE_SOURCE_UNSPECIFIED": value = CaptureSource.Unspecified; return true;
                case "CAPTURE_SOURCE_NATIVE_ANDROID": value = CaptureSource.NativeAndroid; return true;
                case "CAPTURE_SOURCE_NATIVE_IOS": value = CaptureSource.NativeIos; return true;
                case "CAPTURE_SOURCE_UNITY_EVENTS": value = CaptureSource.UnityEvents; return true;
                case "CAPTURE_SOURCE_UNITY_POLLED": value = CaptureSource.UnityPolled; return true;
                default: value = CaptureSource.Unspecified; return false;
            }
        }

        public static string ToProtoName(this FallbackReason value)
        {
            switch (value)
            {
                case FallbackReason.Unspecified: return "FALLBACK_REASON_UNSPECIFIED";
                case FallbackReason.PluginMissing: return "FALLBACK_REASON_PLUGIN_MISSING";
                case FallbackReason.SensorRefused: return "FALLBACK_REASON_SENSOR_REFUSED";
                case FallbackReason.NoFirstSample: return "FALLBACK_REASON_NO_FIRST_SAMPLE";
                case FallbackReason.SamplesStopped: return "FALLBACK_REASON_SAMPLES_STOPPED";
                case FallbackReason.NativeError: return "FALLBACK_REASON_NATIVE_ERROR";
                case FallbackReason.ForcedBySettings: return "FALLBACK_REASON_FORCED_BY_SETTINGS";
                default: return "FALLBACK_REASON_UNSPECIFIED";
            }
        }

        public static bool TryParseFallbackReason(string name, out FallbackReason value)
        {
            switch (name)
            {
                case "FALLBACK_REASON_UNSPECIFIED": value = FallbackReason.Unspecified; return true;
                case "FALLBACK_REASON_PLUGIN_MISSING": value = FallbackReason.PluginMissing; return true;
                case "FALLBACK_REASON_SENSOR_REFUSED": value = FallbackReason.SensorRefused; return true;
                case "FALLBACK_REASON_NO_FIRST_SAMPLE": value = FallbackReason.NoFirstSample; return true;
                case "FALLBACK_REASON_SAMPLES_STOPPED": value = FallbackReason.SamplesStopped; return true;
                case "FALLBACK_REASON_NATIVE_ERROR": value = FallbackReason.NativeError; return true;
                case "FALLBACK_REASON_FORCED_BY_SETTINGS": value = FallbackReason.ForcedBySettings; return true;
                default: value = FallbackReason.Unspecified; return false;
            }
        }

        public static string ToProtoName(this ValueUnit value)
        {
            switch (value)
            {
                case ValueUnit.Unspecified: return "VALUE_UNIT_UNSPECIFIED";
                case ValueUnit.MetresPerSecondSquared: return "VALUE_UNIT_METRES_PER_SECOND_SQUARED";
                case ValueUnit.G: return "VALUE_UNIT_G";
                case ValueUnit.RadiansPerSecond: return "VALUE_UNIT_RADIANS_PER_SECOND";
                case ValueUnit.Microtesla: return "VALUE_UNIT_MICROTESLA";
                default: return "VALUE_UNIT_UNSPECIFIED";
            }
        }

        public static bool TryParseValueUnit(string name, out ValueUnit value)
        {
            switch (name)
            {
                case "VALUE_UNIT_UNSPECIFIED": value = ValueUnit.Unspecified; return true;
                case "VALUE_UNIT_METRES_PER_SECOND_SQUARED": value = ValueUnit.MetresPerSecondSquared; return true;
                case "VALUE_UNIT_G": value = ValueUnit.G; return true;
                case "VALUE_UNIT_RADIANS_PER_SECOND": value = ValueUnit.RadiansPerSecond; return true;
                case "VALUE_UNIT_MICROTESLA": value = ValueUnit.Microtesla; return true;
                default: value = ValueUnit.Unspecified; return false;
            }
        }

        public static string ToProtoName(this TouchMode value)
        {
            switch (value)
            {
                case TouchMode.Unspecified: return "TOUCH_MODE_UNSPECIFIED";
                case TouchMode.Events: return "TOUCH_MODE_EVENTS";
                case TouchMode.EventsMerged: return "TOUCH_MODE_EVENTS_MERGED";
                case TouchMode.Polled: return "TOUCH_MODE_POLLED";
                default: return "TOUCH_MODE_UNSPECIFIED";
            }
        }

        public static bool TryParseTouchMode(string name, out TouchMode value)
        {
            switch (name)
            {
                case "TOUCH_MODE_UNSPECIFIED": value = TouchMode.Unspecified; return true;
                case "TOUCH_MODE_EVENTS": value = TouchMode.Events; return true;
                case "TOUCH_MODE_EVENTS_MERGED": value = TouchMode.EventsMerged; return true;
                case "TOUCH_MODE_POLLED": value = TouchMode.Polled; return true;
                default: value = TouchMode.Unspecified; return false;
            }
        }

        public static string ToProtoName(this TouchEnd value)
        {
            switch (value)
            {
                case TouchEnd.Unspecified: return "TOUCH_END_UNSPECIFIED";
                case TouchEnd.StillDown: return "TOUCH_END_STILL_DOWN";
                case TouchEnd.Lifted: return "TOUCH_END_LIFTED";
                case TouchEnd.Cancelled: return "TOUCH_END_CANCELLED";
                default: return "TOUCH_END_UNSPECIFIED";
            }
        }

        public static bool TryParseTouchEnd(string name, out TouchEnd value)
        {
            switch (name)
            {
                case "TOUCH_END_UNSPECIFIED": value = TouchEnd.Unspecified; return true;
                case "TOUCH_END_STILL_DOWN": value = TouchEnd.StillDown; return true;
                case "TOUCH_END_LIFTED": value = TouchEnd.Lifted; return true;
                case "TOUCH_END_CANCELLED": value = TouchEnd.Cancelled; return true;
                default: value = TouchEnd.Unspecified; return false;
            }
        }

        public static string ToProtoName(this Orientation value)
        {
            switch (value)
            {
                case Orientation.Unspecified: return "ORIENTATION_UNSPECIFIED";
                case Orientation.Portrait: return "ORIENTATION_PORTRAIT";
                case Orientation.PortraitUpsideDown: return "ORIENTATION_PORTRAIT_UPSIDE_DOWN";
                case Orientation.LandscapeLeft: return "ORIENTATION_LANDSCAPE_LEFT";
                case Orientation.LandscapeRight: return "ORIENTATION_LANDSCAPE_RIGHT";
                default: return "ORIENTATION_UNSPECIFIED";
            }
        }

        public static bool TryParseOrientation(string name, out Orientation value)
        {
            switch (name)
            {
                case "ORIENTATION_UNSPECIFIED": value = Orientation.Unspecified; return true;
                case "ORIENTATION_PORTRAIT": value = Orientation.Portrait; return true;
                case "ORIENTATION_PORTRAIT_UPSIDE_DOWN": value = Orientation.PortraitUpsideDown; return true;
                case "ORIENTATION_LANDSCAPE_LEFT": value = Orientation.LandscapeLeft; return true;
                case "ORIENTATION_LANDSCAPE_RIGHT": value = Orientation.LandscapeRight; return true;
                default: value = Orientation.Unspecified; return false;
            }
        }

        public static string ToProtoName(this CoordinateOrigin value)
        {
            switch (value)
            {
                case CoordinateOrigin.Unspecified: return "COORDINATE_ORIGIN_UNSPECIFIED";
                case CoordinateOrigin.BottomLeft: return "COORDINATE_ORIGIN_BOTTOM_LEFT";
                case CoordinateOrigin.TopLeft: return "COORDINATE_ORIGIN_TOP_LEFT";
                default: return "COORDINATE_ORIGIN_UNSPECIFIED";
            }
        }

        public static bool TryParseCoordinateOrigin(string name, out CoordinateOrigin value)
        {
            switch (name)
            {
                case "COORDINATE_ORIGIN_UNSPECIFIED": value = CoordinateOrigin.Unspecified; return true;
                case "COORDINATE_ORIGIN_BOTTOM_LEFT": value = CoordinateOrigin.BottomLeft; return true;
                case "COORDINATE_ORIGIN_TOP_LEFT": value = CoordinateOrigin.TopLeft; return true;
                default: value = CoordinateOrigin.Unspecified; return false;
            }
        }

        public static string ToProtoName(this SpanMarker value)
        {
            switch (value)
            {
                case SpanMarker.Unspecified: return "SPAN_MARKER_UNSPECIFIED";
                case SpanMarker.Open: return "SPAN_MARKER_OPEN";
                case SpanMarker.Close: return "SPAN_MARKER_CLOSE";
                default: return "SPAN_MARKER_UNSPECIFIED";
            }
        }

        public static bool TryParseSpanMarker(string name, out SpanMarker value)
        {
            switch (name)
            {
                case "SPAN_MARKER_UNSPECIFIED": value = SpanMarker.Unspecified; return true;
                case "SPAN_MARKER_OPEN": value = SpanMarker.Open; return true;
                case "SPAN_MARKER_CLOSE": value = SpanMarker.Close; return true;
                default: value = SpanMarker.Unspecified; return false;
            }
        }

        public static string ToProtoName(this StateKind value)
        {
            switch (value)
            {
                case StateKind.Unspecified: return "STATE_KIND_UNSPECIFIED";
                case StateKind.Put: return "STATE_KIND_PUT";
                case StateKind.Patch: return "STATE_KIND_PATCH";
                default: return "STATE_KIND_UNSPECIFIED";
            }
        }

        public static bool TryParseStateKind(string name, out StateKind value)
        {
            switch (name)
            {
                case "STATE_KIND_UNSPECIFIED": value = StateKind.Unspecified; return true;
                case "STATE_KIND_PUT": value = StateKind.Put; return true;
                case "STATE_KIND_PATCH": value = StateKind.Patch; return true;
                default: value = StateKind.Unspecified; return false;
            }
        }

        public static string ToProtoName(this LifecycleKind value)
        {
            switch (value)
            {
                case LifecycleKind.Unspecified: return "LIFECYCLE_KIND_UNSPECIFIED";
                case LifecycleKind.Paused: return "LIFECYCLE_KIND_PAUSED";
                case LifecycleKind.Resumed: return "LIFECYCLE_KIND_RESUMED";
                case LifecycleKind.FocusLost: return "LIFECYCLE_KIND_FOCUS_LOST";
                case LifecycleKind.FocusGained: return "LIFECYCLE_KIND_FOCUS_GAINED";
                case LifecycleKind.Quitting: return "LIFECYCLE_KIND_QUITTING";
                case LifecycleKind.CaptureStoppedByGame: return "LIFECYCLE_KIND_CAPTURE_STOPPED_BY_GAME";
                case LifecycleKind.CaptureStartedByGame: return "LIFECYCLE_KIND_CAPTURE_STARTED_BY_GAME";
                case LifecycleKind.ClockStepped: return "LIFECYCLE_KIND_CLOCK_STEPPED";
                default: return "LIFECYCLE_KIND_UNSPECIFIED";
            }
        }

        public static bool TryParseLifecycleKind(string name, out LifecycleKind value)
        {
            switch (name)
            {
                case "LIFECYCLE_KIND_UNSPECIFIED": value = LifecycleKind.Unspecified; return true;
                case "LIFECYCLE_KIND_PAUSED": value = LifecycleKind.Paused; return true;
                case "LIFECYCLE_KIND_RESUMED": value = LifecycleKind.Resumed; return true;
                case "LIFECYCLE_KIND_FOCUS_LOST": value = LifecycleKind.FocusLost; return true;
                case "LIFECYCLE_KIND_FOCUS_GAINED": value = LifecycleKind.FocusGained; return true;
                case "LIFECYCLE_KIND_QUITTING": value = LifecycleKind.Quitting; return true;
                case "LIFECYCLE_KIND_CAPTURE_STOPPED_BY_GAME": value = LifecycleKind.CaptureStoppedByGame; return true;
                case "LIFECYCLE_KIND_CAPTURE_STARTED_BY_GAME": value = LifecycleKind.CaptureStartedByGame; return true;
                case "LIFECYCLE_KIND_CLOCK_STEPPED": value = LifecycleKind.ClockStepped; return true;
                default: value = LifecycleKind.Unspecified; return false;
            }
        }

        public static string ToProtoName(this GapSignal value)
        {
            switch (value)
            {
                case GapSignal.Unspecified: return "GAP_SIGNAL_UNSPECIFIED";
                case GapSignal.Accelerometer: return "GAP_SIGNAL_ACCELEROMETER";
                case GapSignal.Gyroscope: return "GAP_SIGNAL_GYROSCOPE";
                case GapSignal.Gravity: return "GAP_SIGNAL_GRAVITY";
                case GapSignal.Touch: return "GAP_SIGNAL_TOUCH";
                case GapSignal.Everything: return "GAP_SIGNAL_EVERYTHING";
                case GapSignal.Magnetometer: return "GAP_SIGNAL_MAGNETOMETER";
                default: return "GAP_SIGNAL_UNSPECIFIED";
            }
        }

        public static bool TryParseGapSignal(string name, out GapSignal value)
        {
            switch (name)
            {
                case "GAP_SIGNAL_UNSPECIFIED": value = GapSignal.Unspecified; return true;
                case "GAP_SIGNAL_ACCELEROMETER": value = GapSignal.Accelerometer; return true;
                case "GAP_SIGNAL_GYROSCOPE": value = GapSignal.Gyroscope; return true;
                case "GAP_SIGNAL_GRAVITY": value = GapSignal.Gravity; return true;
                case "GAP_SIGNAL_TOUCH": value = GapSignal.Touch; return true;
                case "GAP_SIGNAL_EVERYTHING": value = GapSignal.Everything; return true;
                case "GAP_SIGNAL_MAGNETOMETER": value = GapSignal.Magnetometer; return true;
                default: value = GapSignal.Unspecified; return false;
            }
        }

        public static string ToProtoName(this GapCause value)
        {
            switch (value)
            {
                case GapCause.Unspecified: return "GAP_CAUSE_UNSPECIFIED";
                case GapCause.SensorSilent: return "GAP_CAUSE_SENSOR_SILENT";
                case GapCause.BufferOverflowed: return "GAP_CAUSE_BUFFER_OVERFLOWED";
                case GapCause.BatchesDropped: return "GAP_CAUSE_BATCHES_DROPPED";
                case GapCause.CaptureStopped: return "GAP_CAUSE_CAPTURE_STOPPED";
                default: return "GAP_CAUSE_UNSPECIFIED";
            }
        }

        public static bool TryParseGapCause(string name, out GapCause value)
        {
            switch (name)
            {
                case "GAP_CAUSE_UNSPECIFIED": value = GapCause.Unspecified; return true;
                case "GAP_CAUSE_SENSOR_SILENT": value = GapCause.SensorSilent; return true;
                case "GAP_CAUSE_BUFFER_OVERFLOWED": value = GapCause.BufferOverflowed; return true;
                case "GAP_CAUSE_BATCHES_DROPPED": value = GapCause.BatchesDropped; return true;
                case "GAP_CAUSE_CAPTURE_STOPPED": value = GapCause.CaptureStopped; return true;
                default: value = GapCause.Unspecified; return false;
            }
        }

        public static string ToProtoName(this Severity value)
        {
            switch (value)
            {
                case Severity.Unspecified: return "SEVERITY_UNSPECIFIED";
                case Severity.Warning: return "SEVERITY_WARNING";
                case Severity.Error: return "SEVERITY_ERROR";
                default: return "SEVERITY_UNSPECIFIED";
            }
        }

        public static bool TryParseSeverity(string name, out Severity value)
        {
            switch (name)
            {
                case "SEVERITY_UNSPECIFIED": value = Severity.Unspecified; return true;
                case "SEVERITY_WARNING": value = Severity.Warning; return true;
                case "SEVERITY_ERROR": value = Severity.Error; return true;
                default: value = Severity.Unspecified; return false;
            }
        }

        public static string ToProtoName(this UploadStatus value)
        {
            switch (value)
            {
                case UploadStatus.Unspecified: return "UPLOAD_STATUS_UNSPECIFIED";
                case UploadStatus.Stored: return "UPLOAD_STATUS_STORED";
                default: return "UPLOAD_STATUS_UNSPECIFIED";
            }
        }

        public static bool TryParseUploadStatus(string name, out UploadStatus value)
        {
            switch (name)
            {
                case "UPLOAD_STATUS_UNSPECIFIED": value = UploadStatus.Unspecified; return true;
                case "UPLOAD_STATUS_STORED": value = UploadStatus.Stored; return true;
                default: value = UploadStatus.Unspecified; return false;
            }
        }

        public static string ToProtoName(this MotionSource value)
        {
            switch (value)
            {
                case MotionSource.Unspecified: return "MOTION_SOURCE_UNSPECIFIED";
                case MotionSource.Native: return "MOTION_SOURCE_NATIVE";
                case MotionSource.Unity: return "MOTION_SOURCE_UNITY";
                default: return "MOTION_SOURCE_UNSPECIFIED";
            }
        }

        public static bool TryParseMotionSource(string name, out MotionSource value)
        {
            switch (name)
            {
                case "MOTION_SOURCE_UNSPECIFIED": value = MotionSource.Unspecified; return true;
                case "MOTION_SOURCE_NATIVE": value = MotionSource.Native; return true;
                case "MOTION_SOURCE_UNITY": value = MotionSource.Unity; return true;
                default: value = MotionSource.Unspecified; return false;
            }
        }
    }

    /// <summary>
    /// Zero-boxing equality comparer for <see cref="Sensor"/> dictionary keys.
    /// </summary>
    public sealed class SensorEqualityComparer : IEqualityComparer<Sensor>
    {
        public static readonly SensorEqualityComparer Instance = new SensorEqualityComparer();

        public bool Equals(Sensor x, Sensor y)
        {
            return x == y;
        }

        public int GetHashCode(Sensor obj)
        {
            return (int)obj;
        }
    }

    /// <summary>
    /// Zero-boxing equality comparer for <see cref="GapSignal"/> dictionary keys.
    /// </summary>
    public sealed class GapSignalEqualityComparer : IEqualityComparer<GapSignal>
    {
        public static readonly GapSignalEqualityComparer Instance = new GapSignalEqualityComparer();

        public bool Equals(GapSignal x, GapSignal y)
        {
            return x == y;
        }

        public int GetHashCode(GapSignal obj)
        {
            return (int)obj;
        }
    }
}
