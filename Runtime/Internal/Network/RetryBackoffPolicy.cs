using System;
using Asparagus.Sdk.Contracts;

namespace Asparagus.Sdk.Internal.Network
{
    /// <summary>
    /// Exponential retry backoff shortened by up to 50% random jitter per <c>session.proto</c> lines 212-219
    /// and <c>BUILDING_AN_SDK.md</c> line 226.
    /// </summary>
    public sealed class RetryBackoffPolicy
    {
        private readonly Func<double> _randomDouble01;
        private readonly object _rngLock = new object();
        private readonly Random _rng;

        public RetryBackoffPolicy(Func<double> randomDouble01 = null)
        {
            if (randomDouble01 != null)
            {
                _randomDouble01 = randomDouble01;
            }
            else
            {
                _rng = new Random();
                _randomDouble01 = NextThreadSafeDouble;
            }
        }

        public long ComputeDelayMs(int attempt, uint firstWaitMs, uint longestWaitMs)
        {
            uint longest = longestWaitMs >= 1u ? longestWaitMs : SessionSettings.DefaultRetryLongestWaitMs;
            uint first = firstWaitMs >= 1u ? firstWaitMs : SessionSettings.DefaultRetryFirstWaitMs;
            if (first > longest)
            {
                first = longest;
            }

            int clampedAttempt = attempt < 0 ? 0 : attempt;
            long baseMs = first;
            long maxMs = longest;

            for (int i = 0; i < clampedAttempt && baseMs < maxMs; i++)
            {
                if (baseMs > maxMs / 2L)
                {
                    baseMs = maxMs;
                    break;
                }

                baseMs *= 2L;
            }

            double r = 0d;
            try
            {
                r = _randomDouble01();
                if (double.IsNaN(r) || double.IsInfinity(r) || r < 0d) r = 0d;
                else if (r > 1d) r = 1d;
            }
            catch
            {
                r = 0d;
            }

            // Shorten wait by a random fraction in [0.0, 0.5] -> factor in [0.5, 1.0].
            double factor = 1.0d - (0.5d * r);
            long jittered = (long)Math.Round(baseMs * factor, MidpointRounding.AwayFromZero);
            return jittered < 1L ? 1L : jittered;
        }

        private double NextThreadSafeDouble()
        {
            lock (_rngLock)
            {
                return _rng.NextDouble();
            }
        }
    }
}
