using System;
using System.Collections.Generic;

namespace Asparagus.Sdk.Contracts
{
    /// <summary>
    /// Contract constants and W8-26 intake/collector bounds for Schema Version 3.
    /// </summary>
    public static class BatchConstants
    {
        public const uint SchemaVersion = 3;
        public const int MaxHeaderStringBytes = 1024;

        // W8-26 bounds (Decision 142)
        public const int MaxUploadBytes = 262144;
        public const int MaxUnzippedBytes = 262144;
        public const uint MaxWindowLengthS = 150;
        public const int MaxEvents = 750;
        public const int MaxGaps = 750;
        public const int MaxMotionSamples = 7500;
        public const int MaxTouchContacts = 750;
        public const int MaxScreenChanges = 750;
        public const int MaxTouchSamples = 7500;
        public const int MaxTableRows = 750;
        public const int MaxTotalRowBytes = 2500000;
    }

    public struct SdkVersion : IEquatable<SdkVersion>
    {
        public uint Major;
        public uint Minor;
        public uint Patch;

        public SdkVersion(uint major, uint minor, uint patch)
        {
            Major = major;
            Minor = minor;
            Patch = patch;
        }

        public bool IsZero
        {
            get { return Major == 0 && Minor == 0 && Patch == 0; }
        }

        public bool Equals(SdkVersion other)
        {
            return Major == other.Major && Minor == other.Minor && Patch == other.Patch;
        }

        public override bool Equals(object obj)
        {
            return obj is SdkVersion other && Equals(other);
        }

        public override int GetHashCode()
        {
            unchecked
            {
                int hash = (int)Major;
                hash = (hash * 397) ^ (int)Minor;
                hash = (hash * 397) ^ (int)Patch;
                return hash;
            }
        }

        public static bool operator ==(SdkVersion left, SdkVersion right)
        {
            return left.Equals(right);
        }

        public static bool operator !=(SdkVersion left, SdkVersion right)
        {
            return !left.Equals(right);
        }

        public override string ToString()
        {
            return Major.ToString() + "." + Minor.ToString() + "." + Patch.ToString();
        }
    }

    public struct ClockReading : IEquatable<ClockReading>
    {
        public long SessionUs;
        public long UnityUs;
        public bool HasSensorUs;
        public long SensorUs;
        public long WallUtcMs;

        public bool IsEmpty
        {
            get { return SessionUs == 0 && UnityUs == 0 && !HasSensorUs && WallUtcMs == 0; }
        }

        public void SetSensorUs(long sensorUs)
        {
            HasSensorUs = true;
            SensorUs = sensorUs;
        }

        public void ClearSensorUs()
        {
            HasSensorUs = false;
            SensorUs = 0;
        }

        public bool Equals(ClockReading other)
        {
            return SessionUs == other.SessionUs
                && UnityUs == other.UnityUs
                && HasSensorUs == other.HasSensorUs
                && (!HasSensorUs || SensorUs == other.SensorUs)
                && WallUtcMs == other.WallUtcMs;
        }

        public override bool Equals(object obj)
        {
            return obj is ClockReading other && Equals(other);
        }

        public override int GetHashCode()
        {
            unchecked
            {
                int hash = SessionUs.GetHashCode();
                hash = (hash * 397) ^ UnityUs.GetHashCode();
                hash = (hash * 397) ^ (HasSensorUs ? 1 : 0);
                if (HasSensorUs)
                {
                    hash = (hash * 397) ^ SensorUs.GetHashCode();
                }
                hash = (hash * 397) ^ WallUtcMs.GetHashCode();
                return hash;
            }
        }

        public static bool operator ==(ClockReading left, ClockReading right)
        {
            return left.Equals(right);
        }

        public static bool operator !=(ClockReading left, ClockReading right)
        {
            return !left.Equals(right);
        }
    }

    public sealed class ServerAnchor
    {
        public long ServerUtcMs;
        public bool HasAtReceipt;
        public ClockReading AtReceipt;
        public uint RoundTripMs;

        public void SetAtReceipt(ClockReading reading)
        {
            HasAtReceipt = true;
            AtReceipt = reading;
        }

        public void Clear()
        {
            ServerUtcMs = 0;
            HasAtReceipt = false;
            AtReceipt = default;
            RoundTripMs = 0;
        }
    }

    public struct ScreenRecord : IEquatable<ScreenRecord>
    {
        public long TimeUs;
        public uint WidthPx;
        public uint HeightPx;
        public float DensityDpi;
        public Orientation Orientation;
        public CoordinateOrigin Origin;

        public bool IsEmpty
        {
            get
            {
                return TimeUs == 0
                    && WidthPx == 0
                    && HeightPx == 0
                    && DensityDpi == 0f
                    && Orientation == Orientation.Unspecified
                    && Origin == CoordinateOrigin.Unspecified;
            }
        }

        public bool Equals(ScreenRecord other)
        {
            return TimeUs == other.TimeUs
                && WidthPx == other.WidthPx
                && HeightPx == other.HeightPx
                && DensityDpi.Equals(other.DensityDpi)
                && Orientation == other.Orientation
                && Origin == other.Origin;
        }

        public override bool Equals(object obj)
        {
            return obj is ScreenRecord other && Equals(other);
        }

        public override int GetHashCode()
        {
            unchecked
            {
                int hash = TimeUs.GetHashCode();
                hash = (hash * 397) ^ (int)WidthPx;
                hash = (hash * 397) ^ (int)HeightPx;
                hash = (hash * 397) ^ DensityDpi.GetHashCode();
                hash = (hash * 397) ^ (int)Orientation;
                hash = (hash * 397) ^ (int)Origin;
                return hash;
            }
        }

        public static bool operator ==(ScreenRecord left, ScreenRecord right)
        {
            return left.Equals(right);
        }

        public static bool operator !=(ScreenRecord left, ScreenRecord right)
        {
            return !left.Equals(right);
        }
    }

    public sealed class Header
    {
        public string IdOrg = string.Empty;
        public string IdDeployment = string.Empty;
        public string IdSource = string.Empty;
        public string IdSession = string.Empty;
        public ulong Sequence;
        public uint SchemaVersion = BatchConstants.SchemaVersion;
        public bool HasSdkVersion;
        public SdkVersion SdkVersion;
        public Platform Platform;
        public string OsVersion = string.Empty;
        public string DeviceModel = string.Empty;
        public string SettingsVersion = string.Empty;

        public long WindowStartUs;
        public uint WindowLengthS;
        public long ClosedUs;
        public CloseReason CloseReason;

        public bool HasClocks;
        public ClockReading Clocks;
        public ServerAnchor ServerAnchor;

        public bool HasScreen;
        public ScreenRecord Screen;
        public bool HasGyroscope;
        public uint BatchesDropped;

        public bool HasUtcOffsetMinutes;
        public int UtcOffsetMinutes;

        public void SetSdkVersion(SdkVersion version)
        {
            HasSdkVersion = true;
            SdkVersion = version;
        }

        public void SetClocks(ClockReading clocks)
        {
            HasClocks = true;
            Clocks = clocks;
        }

        public void SetScreen(ScreenRecord screen)
        {
            HasScreen = true;
            Screen = screen;
        }

        public void SetUtcOffsetMinutes(int minutes)
        {
            HasUtcOffsetMinutes = true;
            UtcOffsetMinutes = minutes;
        }

        public void ClearUtcOffsetMinutes()
        {
            HasUtcOffsetMinutes = false;
            UtcOffsetMinutes = 0;
        }

        public void Clear()
        {
            IdOrg = string.Empty;
            IdDeployment = string.Empty;
            IdSource = string.Empty;
            IdSession = string.Empty;
            Sequence = 0;
            SchemaVersion = BatchConstants.SchemaVersion;
            HasSdkVersion = false;
            SdkVersion = default;
            Platform = Platform.Unspecified;
            OsVersion = string.Empty;
            DeviceModel = string.Empty;
            SettingsVersion = string.Empty;

            WindowStartUs = 0;
            WindowLengthS = 0;
            ClosedUs = 0;
            CloseReason = CloseReason.Unspecified;

            HasClocks = false;
            Clocks = default;
            ServerAnchor = null;

            HasScreen = false;
            Screen = default;
            HasGyroscope = false;
            BatchesDropped = 0;

            HasUtcOffsetMinutes = false;
            UtcOffsetMinutes = 0;
        }
    }

    public struct HoldStats : IEquatable<HoldStats>
    {
        public long Sum;
        public double SumOfSquares;
        public int Minimum;
        public int Maximum;

        public HoldStats(long sum, double sumOfSquares, int minimum, int maximum)
        {
            Sum = sum;
            SumOfSquares = sumOfSquares;
            Minimum = minimum;
            Maximum = maximum;
        }

        public bool Equals(HoldStats other)
        {
            return Sum == other.Sum
                && SumOfSquares.Equals(other.SumOfSquares)
                && Minimum == other.Minimum
                && Maximum == other.Maximum;
        }

        public override bool Equals(object obj)
        {
            return obj is HoldStats other && Equals(other);
        }

        public override int GetHashCode()
        {
            unchecked
            {
                int hash = Sum.GetHashCode();
                hash = (hash * 397) ^ SumOfSquares.GetHashCode();
                hash = (hash * 397) ^ Minimum;
                hash = (hash * 397) ^ Maximum;
                return hash;
            }
        }

        public static bool operator ==(HoldStats left, HoldStats right)
        {
            return left.Equals(right);
        }

        public static bool operator !=(HoldStats left, HoldStats right)
        {
            return !left.Equals(right);
        }
    }

    public sealed class Hold
    {
        public long StartTimeUs;
        public long EndTimeUs;
        public uint SampleCount;
        public int X;
        public int Y;
        public int Z;
        public readonly List<HoldStats> Stats = new List<HoldStats>(3);

        public void Clear()
        {
            StartTimeUs = 0;
            EndTimeUs = 0;
            SampleCount = 0;
            X = 0;
            Y = 0;
            Z = 0;
            Stats.Clear();
        }
    }

    public sealed class SensorBlock
    {
        public Sensor Sensor;
        public CaptureSource Source;
        public FallbackReason FallbackReason;
        public Clock Clock;
        public ValueUnit Unit;
        public double Scale;
        public float RequestedHz;
        public uint SampleCount;
        public uint LongestGapUs;

        public long FirstTimeUs;
        public readonly List<int> TimeStepsUs = new List<int>();
        public readonly List<int> X = new List<int>();
        public readonly List<int> Y = new List<int>();
        public readonly List<int> Z = new List<int>();
        public readonly List<Hold> Holds = new List<Hold>();

        public void Clear()
        {
            Sensor = Sensor.Unspecified;
            Source = CaptureSource.Unspecified;
            FallbackReason = FallbackReason.Unspecified;
            Clock = Clock.Unspecified;
            Unit = ValueUnit.Unspecified;
            Scale = 0d;
            RequestedHz = 0f;
            SampleCount = 0;
            LongestGapUs = 0;
            FirstTimeUs = 0;
            TimeStepsUs.Clear();
            X.Clear();
            Y.Clear();
            Z.Clear();
            Holds.Clear();
        }
    }

    public sealed class ContactBefore
    {
        public long DownTimeUs;
        public int MinX;
        public int MaxX;
        public int MinY;
        public int MaxY;

        public void Clear()
        {
            DownTimeUs = 0;
            MinX = 0;
            MaxX = 0;
            MinY = 0;
            MaxY = 0;
        }
    }

    public sealed class TouchHold
    {
        public long StartTimeUs;
        public long EndTimeUs;
        public uint SampleCount;
        public int X;
        public int Y;
        public int SizeMajor;
        public int SizeMinor;
        public int Pressure;
        public readonly List<HoldStats> Stats = new List<HoldStats>(5);

        public void Clear()
        {
            StartTimeUs = 0;
            EndTimeUs = 0;
            SampleCount = 0;
            X = 0;
            Y = 0;
            SizeMajor = 0;
            SizeMinor = 0;
            Pressure = 0;
            Stats.Clear();
        }
    }

    public sealed class TouchContact
    {
        public uint FingerId;
        public bool Continued;
        public TouchEnd End;
        public long FirstTimeUs;
        public readonly List<int> TimeStepsUs = new List<int>();
        public readonly List<int> X = new List<int>();
        public readonly List<int> Y = new List<int>();
        public readonly List<int> SizeMajor = new List<int>();
        public readonly List<int> SizeMinor = new List<int>();
        public readonly List<int> Pressure = new List<int>();
        public readonly List<TouchHold> Holds = new List<TouchHold>();
        public uint EventsSeen;
        public ContactBefore Before;

        public void Clear()
        {
            FingerId = 0;
            Continued = false;
            End = TouchEnd.Unspecified;
            FirstTimeUs = 0;
            TimeStepsUs.Clear();
            X.Clear();
            Y.Clear();
            SizeMajor.Clear();
            SizeMinor.Clear();
            Pressure.Clear();
            Holds.Clear();
            EventsSeen = 0;
            Before = null;
        }
    }

    public sealed class Touch
    {
        public TouchMode Mode;
        public float RequestedHz;
        public double PositionScale;
        public double SizeScale;
        public double PressureScale;
        public readonly List<TouchContact> Contacts = new List<TouchContact>();

        public void Clear()
        {
            Mode = TouchMode.Unspecified;
            RequestedHz = 0f;
            PositionScale = 0d;
            SizeScale = 0d;
            PressureScale = 0d;
            Contacts.Clear();
        }
    }

    public sealed class GameEvent
    {
        public long TimeUs;
        public string Name = string.Empty;
        public readonly Dictionary<string, Value> Fields = new Dictionary<string, Value>(StringComparer.Ordinal);
        public bool HasOutcome;
        public string Outcome = string.Empty;
        public string SpanName = string.Empty;
        public SpanMarker SpanMarker;
        public uint SpanNumber;
        public uint Order;

        public void SetOutcome(string outcome)
        {
            HasOutcome = true;
            Outcome = outcome ?? string.Empty;
        }

        public void ClearOutcome()
        {
            HasOutcome = false;
            Outcome = string.Empty;
        }

        public void Clear()
        {
            TimeUs = 0;
            Name = string.Empty;
            Fields.Clear();
            HasOutcome = false;
            Outcome = string.Empty;
            SpanName = string.Empty;
            SpanMarker = SpanMarker.Unspecified;
            SpanNumber = 0;
            Order = 0;
        }
    }

    public sealed class StateRecord
    {
        public long TimeUs;
        public StateKind Kind;
        public readonly Dictionary<string, Value> Values = new Dictionary<string, Value>(StringComparer.Ordinal);
        public readonly List<string> RemovedKeys = new List<string>();
        public uint Order;
        public uint Version;

        public void Clear()
        {
            TimeUs = 0;
            Kind = StateKind.Unspecified;
            Values.Clear();
            RemovedKeys.Clear();
            Order = 0;
            Version = 0;
        }
    }

    public sealed class State
    {
        public readonly Dictionary<string, Value> Values = new Dictionary<string, Value>(StringComparer.Ordinal);
        public uint Version;

        public void Clear()
        {
            Values.Clear();
            Version = 0;
        }
    }

    public struct OpenSpan : IEquatable<OpenSpan>
    {
        public string Name;
        public uint Number;
        public long OpenedTimeUs;

        public OpenSpan(string name, uint number, long openedTimeUs)
        {
            Name = name ?? string.Empty;
            Number = number;
            OpenedTimeUs = openedTimeUs;
        }

        public bool Equals(OpenSpan other)
        {
            return string.Equals(Name ?? string.Empty, other.Name ?? string.Empty, StringComparison.Ordinal)
                && Number == other.Number
                && OpenedTimeUs == other.OpenedTimeUs;
        }

        public override bool Equals(object obj)
        {
            return obj is OpenSpan other && Equals(other);
        }

        public override int GetHashCode()
        {
            unchecked
            {
                int hash = StringComparer.Ordinal.GetHashCode(Name ?? string.Empty);
                hash = (hash * 397) ^ (int)Number;
                hash = (hash * 397) ^ OpenedTimeUs.GetHashCode();
                return hash;
            }
        }

        public static bool operator ==(OpenSpan left, OpenSpan right)
        {
            return left.Equals(right);
        }

        public static bool operator !=(OpenSpan left, OpenSpan right)
        {
            return !left.Equals(right);
        }
    }

    public sealed class OpenSpans
    {
        public readonly List<OpenSpan> Spans = new List<OpenSpan>();

        public void Clear()
        {
            Spans.Clear();
        }
    }

    /// <summary>
    /// What was true as the window opened.
    /// Null <see cref="State"/> or <see cref="OpenSpans"/> means absent (not tracked);
    /// non-null empty instances mean present-and-empty.
    /// </summary>
    public sealed class Carried
    {
        public State State;
        public OpenSpans OpenSpans;
        public bool HasLastEventTimeUs;
        public long LastEventTimeUs;

        public void SetLastEventTimeUs(long timeUs)
        {
            HasLastEventTimeUs = true;
            LastEventTimeUs = timeUs;
        }

        public void ClearLastEventTimeUs()
        {
            HasLastEventTimeUs = false;
            LastEventTimeUs = 0;
        }

        public void Clear()
        {
            State = null;
            OpenSpans = null;
            HasLastEventTimeUs = false;
            LastEventTimeUs = 0;
        }
    }

    public struct LifecycleRecord : IEquatable<LifecycleRecord>
    {
        public long TimeUs;
        public LifecycleKind Kind;
        public long ClockStepS;

        public LifecycleRecord(long timeUs, LifecycleKind kind, long clockStepS = 0)
        {
            TimeUs = timeUs;
            Kind = kind;
            ClockStepS = clockStepS;
        }

        public bool Equals(LifecycleRecord other)
        {
            return TimeUs == other.TimeUs
                && Kind == other.Kind
                && ClockStepS == other.ClockStepS;
        }

        public override bool Equals(object obj)
        {
            return obj is LifecycleRecord other && Equals(other);
        }

        public override int GetHashCode()
        {
            unchecked
            {
                int hash = TimeUs.GetHashCode();
                hash = (hash * 397) ^ (int)Kind;
                hash = (hash * 397) ^ ClockStepS.GetHashCode();
                return hash;
            }
        }

        public static bool operator ==(LifecycleRecord left, LifecycleRecord right)
        {
            return left.Equals(right);
        }

        public static bool operator !=(LifecycleRecord left, LifecycleRecord right)
        {
            return !left.Equals(right);
        }
    }

    public struct GapRecord : IEquatable<GapRecord>
    {
        public GapSignal Signal;
        public Clock Clock;
        public long StartTimeUs;
        public long EndTimeUs;
        public GapCause Cause;
        public uint BatchesDropped;
        public ulong FirstDroppedSequence;

        public bool Equals(GapRecord other)
        {
            return Signal == other.Signal
                && Clock == other.Clock
                && StartTimeUs == other.StartTimeUs
                && EndTimeUs == other.EndTimeUs
                && Cause == other.Cause
                && BatchesDropped == other.BatchesDropped
                && FirstDroppedSequence == other.FirstDroppedSequence;
        }

        public override bool Equals(object obj)
        {
            return obj is GapRecord other && Equals(other);
        }

        public override int GetHashCode()
        {
            unchecked
            {
                int hash = (int)Signal;
                hash = (hash * 397) ^ (int)Clock;
                hash = (hash * 397) ^ StartTimeUs.GetHashCode();
                hash = (hash * 397) ^ EndTimeUs.GetHashCode();
                hash = (hash * 397) ^ (int)Cause;
                hash = (hash * 397) ^ (int)BatchesDropped;
                hash = (hash * 397) ^ FirstDroppedSequence.GetHashCode();
                return hash;
            }
        }

        public static bool operator ==(GapRecord left, GapRecord right)
        {
            return left.Equals(right);
        }

        public static bool operator !=(GapRecord left, GapRecord right)
        {
            return !left.Equals(right);
        }
    }

    public sealed class Diagnostic
    {
        public long TimeUs;
        public Severity Severity;
        public string Code = string.Empty;
        public string Detail = string.Empty;
        public uint Count;

        public void Clear()
        {
            TimeUs = 0;
            Severity = Severity.Unspecified;
            Code = string.Empty;
            Detail = string.Empty;
            Count = 0;
        }
    }

    public struct UploadResponse : IEquatable<UploadResponse>
    {
        public UploadStatus Status;

        public UploadResponse(UploadStatus status)
        {
            Status = status;
        }

        public bool Equals(UploadResponse other)
        {
            return Status == other.Status;
        }

        public override bool Equals(object obj)
        {
            return obj is UploadResponse other && Equals(other);
        }

        public override int GetHashCode()
        {
            return (int)Status;
        }

        public static bool operator ==(UploadResponse left, UploadResponse right)
        {
            return left.Equals(right);
        }

        public static bool operator !=(UploadResponse left, UploadResponse right)
        {
            return !left.Equals(right);
        }
    }

    public sealed class Batch
    {
        public Header Header;
        public readonly List<SensorBlock> SensorBlocks = new List<SensorBlock>(4);
        public Touch Touch;
        public readonly List<GameEvent> Events = new List<GameEvent>();
        public readonly List<StateRecord> StateRecords = new List<StateRecord>();
        public readonly List<LifecycleRecord> Lifecycle = new List<LifecycleRecord>();
        public readonly List<GapRecord> Gaps = new List<GapRecord>();
        public readonly List<ScreenRecord> ScreenChanges = new List<ScreenRecord>();
        public readonly List<Diagnostic> Diagnostics = new List<Diagnostic>();
        public Carried Carried;

        public void Clear()
        {
            if (Header != null)
            {
                Header.Clear();
            }

            SensorBlocks.Clear();
            Touch = null;
            Events.Clear();
            StateRecords.Clear();
            Lifecycle.Clear();
            Gaps.Clear();
            ScreenChanges.Clear();
            Diagnostics.Clear();
            Carried = null;
        }
    }
}
