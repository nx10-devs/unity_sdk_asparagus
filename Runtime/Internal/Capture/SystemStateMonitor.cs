using Asparagus.Sdk.Internal.Tracking;

namespace Asparagus.Sdk.Internal.Capture
{
    /// <summary>
    /// Low-cadence (4 Hz max) system-state monitor that polls <see cref="ISystemStateProvider"/>
    /// and forwards valid readings to <see cref="StateTracker"/> for SDK-owned
    /// <c>nx10_screen_brightness</c> and <c>nx10_screen_locked</c> state keys (W15-04).
    /// </summary>
    public sealed class SystemStateMonitor
    {
        public const long PollIntervalUs = 250000L;

        private readonly ISystemStateProvider _provider;
        private readonly StateTracker _stateTracker;

        private bool _hasPolled;
        private long _lastPollUnityUs;

        public SystemStateMonitor(ISystemStateProvider provider, StateTracker stateTracker)
        {
            _provider = provider;
            _stateTracker = stateTracker;
        }

        public void Reset()
        {
            _hasPolled = false;
            _lastPollUnityUs = 0L;
        }

        public bool Poll(long currentUnityTimeUs)
        {
            if (_provider == null || _stateTracker == null)
            {
                return false;
            }

            if (_hasPolled && (currentUnityTimeUs - _lastPollUnityUs) < PollIntervalUs)
            {
                return false;
            }

            _hasPolled = true;
            _lastPollUnityUs = currentUnityTimeUs;

            try
            {
                if (_provider.TryGetScreenBrightness(out double brightness01))
                {
                    _stateTracker.UpdateSdkScreenBrightness(brightness01, currentUnityTimeUs);
                }
            }
            catch
            {
                // Fault containment: never throw into game loop.
            }

            try
            {
                if (_provider.TryGetScreenLocked(out bool isLocked))
                {
                    _stateTracker.UpdateSdkScreenLocked(isLocked, currentUnityTimeUs);
                }
            }
            catch
            {
            }

            return true;
        }
    }
}
