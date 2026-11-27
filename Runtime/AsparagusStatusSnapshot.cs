using Asparagus.Sdk.Contracts;

namespace Asparagus.Sdk
{
    /// <summary>
    /// Allocation-free read-only snapshot of live Asparagus SDK state for diagnostics and test UIs.
    /// </summary>
    public readonly struct AsparagusStatusSnapshot
    {
        public readonly bool IsInitialized;
        public readonly bool IsSessionActive;
        public readonly bool IsCollecting;
        public readonly bool IsTelemetryStoppedByGame;
        public readonly bool IsPaused;
        public readonly bool IsStageOverrideMode;
        public readonly bool IsRefusedForAppRun;

        public readonly string ActiveSessionId;
        public readonly string SettingsVersion;
        public readonly string SettingsLabel;
        public readonly ulong NextSequence;
        public readonly CaptureSource ActiveMotionSource;
        public readonly TouchMode ActiveTouchMode;

        public readonly int WindowBoundedMotionSamples;
        public readonly int WindowTotalMotionSamples;
        public readonly int WindowTouchContacts;
        public readonly int WindowTouchSamples;
        public readonly int WindowEvents;
        public readonly uint CurrentStateVersion;
        public readonly int OpenSpansCount;
        public readonly int WindowGapsCount;
        public readonly int ActiveDiagnosticsCount;

        public readonly int DiskQueuedBatches;
        public readonly long DiskQueuedBytes;
        public readonly int SessionRetryAttempt;
        public readonly int UploadRetryAttempt;
        public readonly int Consecutive401Count;

        public readonly uint HistorySessionsBefore;
        public readonly ulong HistoryCollectedSecondsBefore;
        public readonly uint HistoryLongestSessionSecondsBefore;

        public AsparagusStatusSnapshot(
            bool isInitialized,
            bool isSessionActive,
            bool isCollecting,
            bool isTelemetryStoppedByGame,
            bool isPaused,
            bool isStageOverrideMode,
            bool isRefusedForAppRun,
            string activeSessionId,
            string settingsVersion,
            ulong nextSequence,
            CaptureSource activeMotionSource,
            TouchMode activeTouchMode,
            int windowBoundedMotionSamples,
            int windowTotalMotionSamples,
            int windowTouchContacts,
            int windowTouchSamples,
            int windowEvents,
            uint currentStateVersion,
            int openSpansCount,
            int windowGapsCount,
            int activeDiagnosticsCount,
            int diskQueuedBatches,
            long diskQueuedBytes,
            int sessionRetryAttempt,
            int uploadRetryAttempt,
            int consecutive401Count,
            string settingsLabel = null,
            uint historySessionsBefore = 0u,
            ulong historyCollectedSecondsBefore = 0UL,
            uint historyLongestSessionSecondsBefore = 0u)
        {
            IsInitialized = isInitialized;
            IsSessionActive = isSessionActive;
            IsCollecting = isCollecting;
            IsTelemetryStoppedByGame = isTelemetryStoppedByGame;
            IsPaused = isPaused;
            IsStageOverrideMode = isStageOverrideMode;
            IsRefusedForAppRun = isRefusedForAppRun;

            ActiveSessionId = activeSessionId ?? string.Empty;
            SettingsVersion = settingsVersion ?? string.Empty;
            SettingsLabel = settingsLabel ?? string.Empty;
            NextSequence = nextSequence;
            ActiveMotionSource = activeMotionSource;
            ActiveTouchMode = activeTouchMode;

            WindowBoundedMotionSamples = windowBoundedMotionSamples;
            WindowTotalMotionSamples = windowTotalMotionSamples;
            WindowTouchContacts = windowTouchContacts;
            WindowTouchSamples = windowTouchSamples;
            WindowEvents = windowEvents;
            CurrentStateVersion = currentStateVersion;
            OpenSpansCount = openSpansCount;
            WindowGapsCount = windowGapsCount;
            ActiveDiagnosticsCount = activeDiagnosticsCount;

            DiskQueuedBatches = diskQueuedBatches;
            DiskQueuedBytes = diskQueuedBytes;
            SessionRetryAttempt = sessionRetryAttempt;
            UploadRetryAttempt = uploadRetryAttempt;
            Consecutive401Count = consecutive401Count;

            HistorySessionsBefore = historySessionsBefore;
            HistoryCollectedSecondsBefore = historyCollectedSecondsBefore;
            HistoryLongestSessionSecondsBefore = historyLongestSessionSecondsBefore;
        }
    }
}
