using System;
using System.Collections.Generic;

namespace Asparagus.Sdk.Contracts
{
    public sealed class DeviceProperties
    {
        public string Maker = string.Empty;
        public string Model = string.Empty;
        public string OsVersion = string.Empty;
        public string DeviceType = string.Empty;
        public uint ScreenWidthPx;
        public uint ScreenHeightPx;
        public float ScreenDensityDpi;
        public bool HasGyroscope;
        public string Timezone = string.Empty;
        public string Locale = string.Empty;

        public void Clear()
        {
            Maker = string.Empty;
            Model = string.Empty;
            OsVersion = string.Empty;
            DeviceType = string.Empty;
            ScreenWidthPx = 0;
            ScreenHeightPx = 0;
            ScreenDensityDpi = 0f;
            HasGyroscope = false;
            Timezone = string.Empty;
            Locale = string.Empty;
        }
    }

    public sealed class SessionStartRequest
    {
        public string ApiKey = string.Empty;
        public string DeviceIdentifier = string.Empty;
        public bool HasSdkVersion;
        public SdkVersion SdkVersion;
        public Platform Platform;
        public DeviceProperties Device;
        public string AppVersion = string.Empty;
        public string BuildNumber = string.Empty;
        public readonly Dictionary<string, Value> SessionProperties = new Dictionary<string, Value>(StringComparer.Ordinal);
        public bool Demo;

        public void SetSdkVersion(SdkVersion version)
        {
            HasSdkVersion = true;
            SdkVersion = version;
        }

        public void Clear()
        {
            ApiKey = string.Empty;
            DeviceIdentifier = string.Empty;
            HasSdkVersion = false;
            SdkVersion = default;
            Platform = Platform.Unspecified;
            Device = null;
            AppVersion = string.Empty;
            BuildNumber = string.Empty;
            SessionProperties.Clear();
            Demo = false;
        }
    }

    public sealed class ModelReference
    {
        public string Purpose = string.Empty;
        public string Name = string.Empty;
        public string Version = string.Empty;
        public string DownloadUrl = string.Empty;
        public readonly List<string> Arguments = new List<string>();

        public void Clear()
        {
            Purpose = string.Empty;
            Name = string.Empty;
            Version = string.Empty;
            DownloadUrl = string.Empty;
            Arguments.Clear();
        }
    }

    /// <summary>
    /// All 26 session settings plus <see cref="Version"/> from <c>session.proto</c> and <c>defaults.json</c>.
    /// Built-in defaults are used strictly as a safety net when a setting is omitted or invalid per contract rules.
    /// </summary>
    public sealed class SessionSettings
    {
        public const uint DefaultAccelerometerHz = 100;
        public const uint DefaultGyroscopeHz = 100;
        public const uint DefaultGravityHz = 10;
        public const uint DefaultTouchHz = 60;
        public const MotionSource DefaultMotionSource = MotionSource.Native;
        public const uint DefaultNativeBufferS = 120;
        public const uint DefaultNativeStartLimitMs = 1000;
        public const uint DefaultNativeStallLimitMs = 5000;
        public const uint DefaultAccelerometerHoldTolerance = 0;
        public const uint DefaultGyroscopeHoldTolerance = 0;
        public const uint DefaultGravityHoldTolerance = 0;
        public const uint DefaultTouchHoldTolerance = 0;
        public const uint DefaultHoldRunLength = 10;
        public const uint DefaultGapThresholdMs = 250;
        public const uint DefaultGapThresholdPeriods = 5;
        public const uint DefaultBatchLengthS = 10;
        public const bool DefaultCompress = true;
        public const uint DefaultQueueMaxBytes = 50000000;
        public const uint DefaultQueueMaxAgeS = 900;
        public const uint DefaultRetryFirstWaitMs = 2000;
        public const uint DefaultRetryLongestWaitMs = 300000;
        public const bool DefaultPauseTouchOnKeyboard = false;
        public const uint DefaultTokenLifeS = 86400;
        public const uint DefaultNewSessionAfterS = 1800;
        public const uint MaxNewSessionAfterS = 86400;
        public const uint DefaultMagnetometerHz = 10;
        public const uint DefaultMagnetometerHoldTolerance = 0;

        public string Version = string.Empty;

        public uint AccelerometerHz = DefaultAccelerometerHz;
        public uint GyroscopeHz = DefaultGyroscopeHz;
        public uint GravityHz = DefaultGravityHz;
        public uint TouchHz = DefaultTouchHz;

        public MotionSource MotionSource = DefaultMotionSource;
        public uint NativeBufferS = DefaultNativeBufferS;
        public uint NativeStartLimitMs = DefaultNativeStartLimitMs;
        public uint NativeStallLimitMs = DefaultNativeStallLimitMs;

        public uint AccelerometerHoldTolerance = DefaultAccelerometerHoldTolerance;
        public uint GyroscopeHoldTolerance = DefaultGyroscopeHoldTolerance;
        public uint GravityHoldTolerance = DefaultGravityHoldTolerance;
        public uint TouchHoldTolerance = DefaultTouchHoldTolerance;
        public uint HoldRunLength = DefaultHoldRunLength;

        public uint GapThresholdMs = DefaultGapThresholdMs;
        public uint GapThresholdPeriods = DefaultGapThresholdPeriods;

        public uint BatchLengthS = DefaultBatchLengthS;
        public bool Compress = DefaultCompress;

        public uint QueueMaxBytes = DefaultQueueMaxBytes;
        public uint QueueMaxAgeS = DefaultQueueMaxAgeS;
        public uint RetryFirstWaitMs = DefaultRetryFirstWaitMs;
        public uint RetryLongestWaitMs = DefaultRetryLongestWaitMs;

        public bool PauseTouchOnKeyboard = DefaultPauseTouchOnKeyboard;
        public uint TokenLifeS = DefaultTokenLifeS;
        public uint NewSessionAfterS = DefaultNewSessionAfterS;

        public uint MagnetometerHz = DefaultMagnetometerHz;
        public uint MagnetometerHoldTolerance = DefaultMagnetometerHoldTolerance;

        /// <summary>
        /// Per <c>session.proto</c>: a first retry wait above the longest counts as the longest.
        /// </summary>
        public uint EffectiveRetryFirstWaitMs
        {
            get { return RetryFirstWaitMs > RetryLongestWaitMs ? RetryLongestWaitMs : RetryFirstWaitMs; }
        }

        public static SessionSettings CreateDefaults()
        {
            return new SessionSettings();
        }

        public void ResetToDefaults()
        {
            Version = string.Empty;
            AccelerometerHz = DefaultAccelerometerHz;
            GyroscopeHz = DefaultGyroscopeHz;
            GravityHz = DefaultGravityHz;
            TouchHz = DefaultTouchHz;
            MotionSource = DefaultMotionSource;
            NativeBufferS = DefaultNativeBufferS;
            NativeStartLimitMs = DefaultNativeStartLimitMs;
            NativeStallLimitMs = DefaultNativeStallLimitMs;
            AccelerometerHoldTolerance = DefaultAccelerometerHoldTolerance;
            GyroscopeHoldTolerance = DefaultGyroscopeHoldTolerance;
            GravityHoldTolerance = DefaultGravityHoldTolerance;
            TouchHoldTolerance = DefaultTouchHoldTolerance;
            HoldRunLength = DefaultHoldRunLength;
            GapThresholdMs = DefaultGapThresholdMs;
            GapThresholdPeriods = DefaultGapThresholdPeriods;
            BatchLengthS = DefaultBatchLengthS;
            Compress = DefaultCompress;
            QueueMaxBytes = DefaultQueueMaxBytes;
            QueueMaxAgeS = DefaultQueueMaxAgeS;
            RetryFirstWaitMs = DefaultRetryFirstWaitMs;
            RetryLongestWaitMs = DefaultRetryLongestWaitMs;
            PauseTouchOnKeyboard = DefaultPauseTouchOnKeyboard;
            TokenLifeS = DefaultTokenLifeS;
            NewSessionAfterS = DefaultNewSessionAfterS;
            MagnetometerHz = DefaultMagnetometerHz;
            MagnetometerHoldTolerance = DefaultMagnetometerHoldTolerance;
        }

        public SessionSettings Clone()
        {
            return new SessionSettings
            {
                Version = Version,
                AccelerometerHz = AccelerometerHz,
                GyroscopeHz = GyroscopeHz,
                GravityHz = GravityHz,
                TouchHz = TouchHz,
                MotionSource = MotionSource,
                NativeBufferS = NativeBufferS,
                NativeStartLimitMs = NativeStartLimitMs,
                NativeStallLimitMs = NativeStallLimitMs,
                AccelerometerHoldTolerance = AccelerometerHoldTolerance,
                GyroscopeHoldTolerance = GyroscopeHoldTolerance,
                GravityHoldTolerance = GravityHoldTolerance,
                TouchHoldTolerance = TouchHoldTolerance,
                HoldRunLength = HoldRunLength,
                GapThresholdMs = GapThresholdMs,
                GapThresholdPeriods = GapThresholdPeriods,
                BatchLengthS = BatchLengthS,
                Compress = Compress,
                QueueMaxBytes = QueueMaxBytes,
                QueueMaxAgeS = QueueMaxAgeS,
                RetryFirstWaitMs = RetryFirstWaitMs,
                RetryLongestWaitMs = RetryLongestWaitMs,
                PauseTouchOnKeyboard = PauseTouchOnKeyboard,
                TokenLifeS = TokenLifeS,
                NewSessionAfterS = NewSessionAfterS,
                MagnetometerHz = MagnetometerHz,
                MagnetometerHoldTolerance = MagnetometerHoldTolerance
            };
        }

        /// <summary>
        /// Applies a raw <see cref="MotionSource"/> override; <see cref="MotionSource.Unspecified"/> is treated as left out.
        /// </summary>
        public void ApplyMotionSource(MotionSource value)
        {
            if (value == MotionSource.Native || value == MotionSource.Unity)
            {
                MotionSource = value;
            }
        }

        /// <summary>
        /// Applies a <c>batch_length_s</c> override; values &lt; 1 are treated as left out.
        /// </summary>
        public void ApplyBatchLengthS(uint value)
        {
            if (value >= 1)
            {
                BatchLengthS = value;
            }
        }

        /// <summary>
        /// Applies a <c>retry_first_wait_ms</c> override; values &lt; 1 are treated as left out.
        /// </summary>
        public void ApplyRetryFirstWaitMs(uint value)
        {
            if (value >= 1)
            {
                RetryFirstWaitMs = value;
            }
        }

        /// <summary>
        /// Applies a <c>retry_longest_wait_ms</c> override; values &lt; 1 are treated as left out.
        /// </summary>
        public void ApplyRetryLongestWaitMs(uint value)
        {
            if (value >= 1)
            {
                RetryLongestWaitMs = value;
            }
        }

        /// <summary>
        /// Applies a <c>token_life_s</c> override; values &lt; 1 are treated as left out.
        /// </summary>
        public void ApplyTokenLifeS(uint value)
        {
            if (value >= 1)
            {
                TokenLifeS = value;
            }
        }

        /// <summary>
        /// Applies a <c>new_session_after_s</c> override; values outside [1, 86400] are treated as left out.
        /// </summary>
        public void ApplyNewSessionAfterS(uint value)
        {
            if (value >= 1 && value <= MaxNewSessionAfterS)
            {
                NewSessionAfterS = value;
            }
        }
    }

    public sealed class SessionStartResponse
    {
        public string IdOrg = string.Empty;
        public string IdDeployment = string.Empty;
        public string IdSource = string.Empty;
        public string IdSession = string.Empty;

        public string Token = string.Empty;
        public long TokenExpiresUtcMs;
        public long ServerUtcMs;

        public string BatchEndpoint = string.Empty;
        public string AccessRequestEndpoint = string.Empty;
        public string ForgetRequestEndpoint = string.Empty;

        public SessionSettings Settings = SessionSettings.CreateDefaults();
        public readonly List<ModelReference> Models = new List<ModelReference>();

        public void Clear()
        {
            IdOrg = string.Empty;
            IdDeployment = string.Empty;
            IdSource = string.Empty;
            IdSession = string.Empty;
            Token = string.Empty;
            TokenExpiresUtcMs = 0;
            ServerUtcMs = 0;
            BatchEndpoint = string.Empty;
            AccessRequestEndpoint = string.Empty;
            ForgetRequestEndpoint = string.Empty;
            if (Settings == null)
            {
                Settings = SessionSettings.CreateDefaults();
            }
            else
            {
                Settings.ResetToDefaults();
            }
            Models.Clear();
        }
    }
}
