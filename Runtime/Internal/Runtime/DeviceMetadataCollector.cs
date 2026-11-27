using System;
using System.Globalization;
using Asparagus.Sdk.Contracts;
using Asparagus.Sdk.Internal.Capture;

namespace Asparagus.Sdk.Internal.Runtime
{
    public sealed class DeviceMetadataSnapshot
    {
        public Platform Platform;
        public string DeviceIdentifier = string.Empty;
        public string AppVersion = string.Empty;
        public string BuildNumber = string.Empty;
        public DeviceProperties DeviceProperties = new DeviceProperties();
    }

    public interface IDeviceMetadataProvider
    {
        DeviceMetadataSnapshot Collect(
            IScreenInfoProvider screenInfoProvider,
            bool hasGyroscope,
            string appVersionOverride = null,
            string buildNumberOverride = null);
    }

    /// <summary>
    /// Privacy-safe Unity device metadata provider.
    /// Uses non-personal hardware/display identifiers only and ensures <see cref="Header.DeviceModel"/>
    /// and <see cref="Header.OsVersion"/> match <see cref="DeviceProperties"/> identically.
    /// </summary>
    public sealed class UnityDeviceMetadataProvider : IDeviceMetadataProvider
    {
        private readonly string _fallbackDeviceId = Guid.NewGuid().ToString("N");

        public DeviceMetadataSnapshot Collect(
            IScreenInfoProvider screenInfoProvider,
            bool hasGyroscope,
            string appVersionOverride = null,
            string buildNumberOverride = null)
        {
            Platform platform = UnityCaptureSourceFactory.DetectRuntimePlatform();
            string deviceId = _fallbackDeviceId;
            string appVersion = !string.IsNullOrEmpty(appVersionOverride) ? appVersionOverride : "0.0.0";
            string buildNumber = !string.IsNullOrEmpty(buildNumberOverride) ? buildNumberOverride : string.Empty;
            string maker = string.Empty;
            string model = "Unknown";
            string osVersion = string.Empty;
            string deviceType = "Unknown";
            bool resolvedHasGyro = hasGyroscope;

#if UNITY_5_3_OR_NEWER
            try
            {
                string uid = UnityEngine.SystemInfo.deviceUniqueIdentifier;
                if (!string.IsNullOrEmpty(uid))
                {
                    deviceId = uid;
                }
            }
            catch
            {
            }

            try
            {
                if (string.IsNullOrEmpty(appVersionOverride) && !string.IsNullOrEmpty(UnityEngine.Application.version))
                {
                    appVersion = UnityEngine.Application.version;
                }
            }
            catch
            {
            }

            try
            {
                if (string.IsNullOrEmpty(buildNumberOverride) && !string.IsNullOrEmpty(UnityEngine.Application.buildGUID))
                {
                    buildNumber = UnityEngine.Application.buildGUID;
                }
            }
            catch
            {
            }

            try
            {
                string rawModel = UnityEngine.SystemInfo.deviceModel;
                if (!string.IsNullOrEmpty(rawModel))
                {
                    model = rawModel;
                }
            }
            catch
            {
            }

            try
            {
                string rawOs = UnityEngine.SystemInfo.operatingSystem;
                if (!string.IsNullOrEmpty(rawOs))
                {
                    osVersion = rawOs;
                }
            }
            catch
            {
            }

            try
            {
                deviceType = UnityEngine.SystemInfo.deviceType.ToString();
            }
            catch
            {
            }

            try
            {
                if (UnityEngine.SystemInfo.supportsGyroscope)
                {
                    resolvedHasGyro = true;
                }
            }
            catch
            {
            }

            maker = InferMaker(platform, model);
#else
            try
            {
                osVersion = Environment.OSVersion.ToString();
            }
            catch
            {
                osVersion = "Unknown";
            }

            maker = InferMaker(platform, model);
#endif

            ScreenRecord screen = default;
            if (screenInfoProvider != null)
            {
                try
                {
                    screen = screenInfoProvider.ReadCurrentScreen();
                }
                catch
                {
                }
            }

            string timezone = string.Empty;
            try
            {
                timezone = TimeZoneInfo.Local != null ? (TimeZoneInfo.Local.Id ?? string.Empty) : string.Empty;
            }
            catch
            {
            }

            string locale = string.Empty;
            try
            {
                locale = CultureInfo.CurrentCulture != null ? (CultureInfo.CurrentCulture.Name ?? string.Empty) : string.Empty;
            }
            catch
            {
            }

            DeviceProperties props = new DeviceProperties
            {
                Maker = maker,
                Model = model,
                OsVersion = osVersion,
                DeviceType = deviceType,
                ScreenWidthPx = screen.WidthPx,
                ScreenHeightPx = screen.HeightPx,
                ScreenDensityDpi = screen.DensityDpi,
                HasGyroscope = resolvedHasGyro,
                Timezone = timezone,
                Locale = locale
            };

            return new DeviceMetadataSnapshot
            {
                Platform = platform,
                DeviceIdentifier = deviceId,
                AppVersion = appVersion,
                BuildNumber = buildNumber,
                DeviceProperties = props
            };
        }

        private static string InferMaker(Platform platform, string model)
        {
            if (platform == Platform.Ios)
            {
                return "Apple";
            }

            if (!string.IsNullOrEmpty(model))
            {
                int spaceIdx = model.IndexOf(' ');
                if (spaceIdx > 0)
                {
                    return model.Substring(0, spaceIdx);
                }
            }

            return string.Empty;
        }
    }
}
