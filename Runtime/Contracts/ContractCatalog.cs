using System;

namespace Asparagus.Sdk.Contracts
{
    /// <summary>
    /// All nine core span names defined in <c>core_events.json</c> (asparagus-contract-v5).
    /// </summary>
    public static class CoreSpans
    {
        public const string Level = "level";
        public const string Ad = "ad";
        public const string PurchaseView = "purchase_view";
        public const string Round = "round";
        public const string RewardedOffer = "rewarded_offer";
        public const string Backgrounded = "backgrounded";
        public const string Settings = "settings";
        public const string Popup = "popup";
        public const string TutorialOrOnboarding = "tutorial_or_onboarding";

        public static readonly string[] All =
        {
            Level,
            Ad,
            PurchaseView,
            Round,
            RewardedOffer,
            Backgrounded,
            Settings,
            Popup,
            TutorialOrOnboarding
        };

        /// <summary>
        /// Formats an ad span name: <c>"ad"</c> if <paramref name="adType"/> is null or empty,
        /// otherwise <c>"ad_" + adType</c>.
        /// </summary>
        public static string AdForType(string adType)
        {
            return string.IsNullOrEmpty(adType) ? Ad : "ad_" + adType;
        }
    }

    public enum CoreEventRole : byte
    {
        None = 0,
        Opens = 1,
        Closes = 2
    }

    public readonly struct CoreEventMetadata
    {
        public readonly string Name;
        public readonly string DefaultSpan;
        public readonly CoreEventRole Role;
        public readonly bool CarriesOutcome;

        public CoreEventMetadata(string name, string defaultSpan, CoreEventRole role, bool carriesOutcome)
        {
            Name = name;
            DefaultSpan = defaultSpan;
            Role = role;
            CarriesOutcome = carriesOutcome;
        }
    }

    /// <summary>
    /// Names and zero-allocation role metadata lookup for all eleven core events in <c>core_events.json</c>.
    /// </summary>
    public static class CoreEventNames
    {
        public const string GameLevelStarted = "game_level_started";
        public const string GameLevelSucceeded = "game_level_succeeded";
        public const string GameLevelFailed = "game_level_failed";
        public const string AdStarted = "ad_started";
        public const string AdFinished = "ad_finished";
        public const string AdRevenue = "ad_revenue";
        public const string InAppPurchase = "in_app_purchase";
        public const string InAppPurchaseViewOpened = "in_app_purchase_view_opened";
        public const string InAppPurchaseViewClosed = "in_app_purchase_view_closed";
        public const string GamblingRoundStarted = "gambling_round_started";
        public const string GamblingRoundFinished = "gambling_round_finished";

        public static readonly string[] All =
        {
            GameLevelStarted,
            GameLevelSucceeded,
            GameLevelFailed,
            AdStarted,
            AdFinished,
            AdRevenue,
            InAppPurchase,
            InAppPurchaseViewOpened,
            InAppPurchaseViewClosed,
            GamblingRoundStarted,
            GamblingRoundFinished
        };

        public static bool TryGetMetadata(string eventName, out CoreEventMetadata metadata)
        {
            switch (eventName)
            {
                case GameLevelStarted:
                    metadata = new CoreEventMetadata(GameLevelStarted, CoreSpans.Level, CoreEventRole.Opens, false);
                    return true;
                case GameLevelSucceeded:
                    metadata = new CoreEventMetadata(GameLevelSucceeded, CoreSpans.Level, CoreEventRole.Closes, false);
                    return true;
                case GameLevelFailed:
                    metadata = new CoreEventMetadata(GameLevelFailed, CoreSpans.Level, CoreEventRole.Closes, false);
                    return true;
                case AdStarted:
                    metadata = new CoreEventMetadata(AdStarted, CoreSpans.Ad, CoreEventRole.Opens, false);
                    return true;
                case AdFinished:
                    metadata = new CoreEventMetadata(AdFinished, CoreSpans.Ad, CoreEventRole.Closes, false);
                    return true;
                case AdRevenue:
                    metadata = new CoreEventMetadata(AdRevenue, null, CoreEventRole.None, true);
                    return true;
                case InAppPurchase:
                    metadata = new CoreEventMetadata(InAppPurchase, null, CoreEventRole.None, true);
                    return true;
                case InAppPurchaseViewOpened:
                    metadata = new CoreEventMetadata(InAppPurchaseViewOpened, CoreSpans.PurchaseView, CoreEventRole.Opens, false);
                    return true;
                case InAppPurchaseViewClosed:
                    metadata = new CoreEventMetadata(InAppPurchaseViewClosed, CoreSpans.PurchaseView, CoreEventRole.Closes, false);
                    return true;
                case GamblingRoundStarted:
                    metadata = new CoreEventMetadata(GamblingRoundStarted, CoreSpans.Round, CoreEventRole.Opens, false);
                    return true;
                case GamblingRoundFinished:
                    metadata = new CoreEventMetadata(GamblingRoundFinished, CoreSpans.Round, CoreEventRole.Closes, false);
                    return true;
                default:
                    metadata = default;
                    return false;
            }
        }

        public static CoreEventRole GetRole(string eventName)
        {
            return TryGetMetadata(eventName, out CoreEventMetadata metadata)
                ? metadata.Role
                : CoreEventRole.None;
        }
    }

    public static class StandardStateKeys
    {
        public const string GameLevel = "game_level";
        public const string View = "view";
        public const string Mode = "mode";
    }

    public static class SdkStateKeys
    {
        public const string ReservedPrefix = "nx10_";
        public const string ScreenBrightness = "nx10_screen_brightness";
        public const string ScreenLocked = "nx10_screen_locked";

        public static bool IsReservedKey(string key)
        {
            return !string.IsNullOrEmpty(key) && key.StartsWith(ReservedPrefix, StringComparison.Ordinal);
        }
    }

    public static class Outcomes
    {
        public const string Converted = "Converted";
        public const string UnConverted = "UnConverted";
    }
}
