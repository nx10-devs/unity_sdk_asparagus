using System;
using Asparagus.Sdk.Contracts;

namespace Asparagus.Sdk.Internal.Time
{
    public enum SessionResumeResult : byte
    {
        ContinueSession = 0,
        StartNewSession = 1
    }

    /// <summary>
    /// Implements the asparagus session clock (Decision 78 &amp; Decision 148):
    /// anchored at session start from <c>server_utc_ms</c>, carried forward purely by the steady monotonic clock,
    /// stepped forward only in whole seconds after a background return if the steady clock lagged wall time,
    /// and triggering a new session if away longer than <c>new_session_after_s</c>.
    /// </summary>
    public sealed class SessionClock
    {
        private const long MicrosecondsPerSecond = 1000000L;
        private const long MicrosecondsPerMillisecond = 1000L;

        private readonly IClockSource _source;
        private readonly object _sync = new object();
        private readonly ServerAnchor _serverAnchor = new ServerAnchor();

        private bool _isSessionActive;
        private long _anchorSessionUs;
        private long _anchorSteadyUs;
        private long _stepOffsetUs;
        private long _currentWindowStartUs;

        private bool _isPaused;
        private long _pauseSteadyUs;
        private long _pauseWallUtcMs;

        public SessionClock(IClockSource source)
        {
            _source = source ?? new SystemClockSource();
        }

        public bool IsSessionActive
        {
            get
            {
                lock (_sync)
                {
                    return _isSessionActive;
                }
            }
        }

        public long CurrentWindowStartUs
        {
            get
            {
                lock (_sync)
                {
                    return _currentWindowStartUs;
                }
            }
        }

        public long CurrentSessionUs
        {
            get
            {
                lock (_sync)
                {
                    return ComputeSessionUsLocked(_source.SteadyTimeUs);
                }
            }
        }

        public long CurrentUnityUs
        {
            get { return _source.UnityTimeUs; }
        }

        public int UtcOffsetMinutes
        {
            get { return _source.UtcOffsetMinutes; }
        }

        public static long FloorToWholeSecondUs(long timeUs)
        {
            long rem = timeUs % MicrosecondsPerSecond;
            if (rem < 0)
            {
                return timeUs - rem - MicrosecondsPerSecond;
            }

            return timeUs - rem;
        }

        public void StartSession(long serverUtcMs, uint roundTripMs)
        {
            lock (_sync)
            {
                long steadyUs = _source.SteadyTimeUs;
                long unityUs = _source.UnityTimeUs;
                long wallUtcMs = _source.WallUtcMs;
                bool hasSensor = _source.TryGetSensorTimeUs(out long sensorUs);

                long initialSessionUs = serverUtcMs * MicrosecondsPerMillisecond;

                _isSessionActive = true;
                _anchorSessionUs = initialSessionUs;
                _anchorSteadyUs = steadyUs;
                _stepOffsetUs = 0L;
                _isPaused = false;
                _pauseSteadyUs = 0L;
                _pauseWallUtcMs = 0L;

                ClockReading atReceipt = new ClockReading
                {
                    SessionUs = initialSessionUs,
                    UnityUs = unityUs,
                    WallUtcMs = wallUtcMs
                };
                if (hasSensor)
                {
                    atReceipt.SetSensorUs(sensorUs);
                }

                _serverAnchor.ServerUtcMs = serverUtcMs;
                _serverAnchor.SetAtReceipt(atReceipt);
                _serverAnchor.RoundTripMs = roundTripMs;

                _currentWindowStartUs = FloorToWholeSecondUs(initialSessionUs);
            }
        }

        public void Reset()
        {
            lock (_sync)
            {
                _isSessionActive = false;
                _anchorSessionUs = 0L;
                _anchorSteadyUs = 0L;
                _stepOffsetUs = 0L;
                _currentWindowStartUs = 0L;
                _isPaused = false;
                _pauseSteadyUs = 0L;
                _pauseWallUtcMs = 0L;
                _serverAnchor.Clear();
            }
        }

        public ClockReading ReadClocks()
        {
            lock (_sync)
            {
                long steadyUs = _source.SteadyTimeUs;
                long unityUs = _source.UnityTimeUs;
                long wallUtcMs = _source.WallUtcMs;
                bool hasSensor = _source.TryGetSensorTimeUs(out long sensorUs);

                ClockReading reading = new ClockReading
                {
                    SessionUs = ComputeSessionUsLocked(steadyUs),
                    UnityUs = unityUs,
                    WallUtcMs = wallUtcMs
                };
                if (hasSensor)
                {
                    reading.SetSensorUs(sensorUs);
                }

                return reading;
            }
        }

        public void CopyServerAnchorTo(ServerAnchor target)
        {
            if (target == null)
            {
                return;
            }

            lock (_sync)
            {
                target.ServerUtcMs = _serverAnchor.ServerUtcMs;
                target.HasAtReceipt = _serverAnchor.HasAtReceipt;
                target.AtReceipt = _serverAnchor.AtReceipt;
                target.RoundTripMs = _serverAnchor.RoundTripMs;
            }
        }

        public long GetWindowEndUs(uint batchLengthS)
        {
            lock (_sync)
            {
                uint clamped = ClampWindowLengthS(batchLengthS);
                return _currentWindowStartUs + (long)clamped * MicrosecondsPerSecond;
            }
        }

        public bool IsCurrentWindowFull(uint batchLengthS)
        {
            lock (_sync)
            {
                if (!_isSessionActive)
                {
                    return false;
                }

                uint clamped = ClampWindowLengthS(batchLengthS);
                long endUs = _currentWindowStartUs + (long)clamped * MicrosecondsPerSecond;
                return ComputeSessionUsLocked(_source.SteadyTimeUs) >= endUs;
            }
        }

        /// <summary>
        /// Computes the valid window length in whole seconds (<c>[1, 150]</c>) for the closing window
        /// and advances <see cref="CurrentWindowStartUs"/> to the next whole second.
        /// </summary>
        public uint AdvanceWindow(long closedUs, bool wasFullWindow, uint batchLengthS)
        {
            lock (_sync)
            {
                uint clampedBatchS = ClampWindowLengthS(batchLengthS);
                uint windowLengthS;

                if (wasFullWindow)
                {
                    windowLengthS = clampedBatchS;
                    _currentWindowStartUs = _currentWindowStartUs + (long)clampedBatchS * MicrosecondsPerSecond;
                }
                else
                {
                    long elapsedUs = closedUs - _currentWindowStartUs;
                    if (elapsedUs <= 0L)
                    {
                        windowLengthS = 1u;
                    }
                    else
                    {
                        long roundedUpSeconds = (elapsedUs + (MicrosecondsPerSecond - 1L)) / MicrosecondsPerSecond;
                        if (roundedUpSeconds < 1L)
                        {
                            roundedUpSeconds = 1L;
                        }
                        else if (roundedUpSeconds > BatchConstants.MaxWindowLengthS)
                        {
                            roundedUpSeconds = BatchConstants.MaxWindowLengthS;
                        }

                        windowLengthS = (uint)roundedUpSeconds;
                    }

                    long nowSessionUs = ComputeSessionUsLocked(_source.SteadyTimeUs);
                    long nextStartSourceUs = closedUs >= nowSessionUs ? closedUs : nowSessionUs;
                    _currentWindowStartUs = FloorToWholeSecondUs(nextStartSourceUs);
                }

                return windowLengthS;
            }
        }

        /// <summary>
        /// Advances <see cref="CurrentWindowStartUs"/> to the current session whole second if behind,
        /// preventing empty catch-up batches after a background pause or main-thread stall.
        /// </summary>
        public void SnapWindowStartToCurrentSecond()
        {
            lock (_sync)
            {
                if (!_isSessionActive)
                {
                    return;
                }

                long currentWholeSecond = FloorToWholeSecondUs(ComputeSessionUsLocked(_source.SteadyTimeUs));
                if (currentWholeSecond > _currentWindowStartUs)
                {
                    _currentWindowStartUs = currentWholeSecond;
                }
            }
        }

        /// <summary>
        /// Judges token expiry strictly against the session clock, never the device wall clock.
        /// </summary>
        public bool IsTokenExpired(long tokenExpiresUtcMs)
        {
            if (tokenExpiresUtcMs <= 0L)
            {
                return false;
            }

            lock (_sync)
            {
                if (!_isSessionActive)
                {
                    return false;
                }

                long sessionMs = ComputeSessionUsLocked(_source.SteadyTimeUs) / MicrosecondsPerMillisecond;
                return sessionMs >= tokenExpiresUtcMs;
            }
        }

        public void OnPause()
        {
            lock (_sync)
            {
                if (!_isSessionActive)
                {
                    return;
                }

                _isPaused = true;
                _pauseSteadyUs = _source.SteadyTimeUs;
                _pauseWallUtcMs = _source.WallUtcMs;
            }
        }

        /// <summary>
        /// Evaluates a return from background pause. Returns <see cref="SessionResumeResult.StartNewSession"/>
        /// when elapsed away time exceeds <paramref name="newSessionAfterS"/>; otherwise steps the session
        /// clock forward by whole seconds if the steady clock lagged behind wall time during sleep.
        /// </summary>
        public SessionResumeResult OnResume(uint newSessionAfterS, out LifecycleRecord? clockSteppedRecord)
        {
            clockSteppedRecord = null;

            lock (_sync)
            {
                if (!_isSessionActive || !_isPaused)
                {
                    _isPaused = false;
                    return SessionResumeResult.ContinueSession;
                }

                _isPaused = false;

                long nowSteadyUs = _source.SteadyTimeUs;
                long nowUnityUs = _source.UnityTimeUs;
                long nowWallUtcMs = _source.WallUtcMs;

                long steadyDeltaUs = nowSteadyUs - _pauseSteadyUs;
                if (steadyDeltaUs < 0L)
                {
                    steadyDeltaUs = 0L;
                }

                long wallDeltaUs = (nowWallUtcMs - _pauseWallUtcMs) * MicrosecondsPerMillisecond;
                if (wallDeltaUs < 0L)
                {
                    wallDeltaUs = 0L;
                }

                long awayUs = wallDeltaUs > steadyDeltaUs ? wallDeltaUs : steadyDeltaUs;
                uint thresholdS = newSessionAfterS >= 1u ? newSessionAfterS : SessionSettings.DefaultNewSessionAfterS;
                if (awayUs > (long)thresholdS * MicrosecondsPerSecond)
                {
                    return SessionResumeResult.StartNewSession;
                }

                long lagUs = wallDeltaUs - steadyDeltaUs;
                if (lagUs >= MicrosecondsPerSecond)
                {
                    long stepSeconds = lagUs / MicrosecondsPerSecond;
                    _stepOffsetUs += stepSeconds * MicrosecondsPerSecond;

                    long steppedSessionUs = ComputeSessionUsLocked(nowSteadyUs);
                    long floored = FloorToWholeSecondUs(steppedSessionUs);
                    if (floored > _currentWindowStartUs)
                    {
                        _currentWindowStartUs = floored;
                    }

                    clockSteppedRecord = new LifecycleRecord(nowUnityUs, LifecycleKind.ClockStepped, stepSeconds);
                }

                return SessionResumeResult.ContinueSession;
            }
        }

        private long ComputeSessionUsLocked(long steadyUs)
        {
            if (!_isSessionActive)
            {
                return _source.WallUtcMs * MicrosecondsPerMillisecond;
            }

            long deltaSteadyUs = steadyUs - _anchorSteadyUs;
            if (deltaSteadyUs < 0L)
            {
                deltaSteadyUs = 0L;
            }

            return _anchorSessionUs + deltaSteadyUs + _stepOffsetUs;
        }

        private static uint ClampWindowLengthS(uint batchLengthS)
        {
            if (batchLengthS < 1u)
            {
                return 1u;
            }

            if (batchLengthS > BatchConstants.MaxWindowLengthS)
            {
                return BatchConstants.MaxWindowLengthS;
            }

            return batchLengthS;
        }
    }
}
