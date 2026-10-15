using System;

namespace UsageWidget
{
    // One schedule per provider: a failing provider must not delay healthy ones.
    internal sealed class ProviderRefreshSchedule
    {
        internal int Failures { get; private set; }
        internal DateTimeOffset NextAttempt { get; private set; }
        internal DateTimeOffset ServerNotBefore { get; private set; }

        internal bool CanAttempt(DateTimeOffset now, bool manual)
        {
            return now >= ServerNotBefore && (manual || now >= NextAttempt);
        }

        internal void Succeeded(DateTimeOffset now, int intervalSeconds)
        {
            Failures = 0;
            ServerNotBefore = DateTimeOffset.MinValue;
            NextAttempt = now.AddSeconds(Math.Max(15, intervalSeconds));
        }

        internal void Failed(DateTimeOffset now, int intervalSeconds, DateTimeOffset? retryNotBefore)
        {
            Failures = Math.Min(10, Failures + 1);
            double delay = Math.Min(900, Math.Max(30, intervalSeconds) * Math.Pow(2, Failures - 1));
            NextAttempt = now.AddSeconds(delay);
            if (retryNotBefore.HasValue && retryNotBefore.Value > ServerNotBefore)
                ServerNotBefore = retryNotBefore.Value;
            if (ServerNotBefore > NextAttempt) NextAttempt = ServerNotBefore;
        }

        internal void NetworkRestored(DateTimeOffset now)
        {
            // A connectivity change must not bypass a server's Retry-After.
            if (Failures > 0) NextAttempt = ServerNotBefore > now ? ServerNotBefore : now;
        }

        internal void ChangeInterval(DateTimeOffset now, int intervalSeconds)
        {
            if (Failures == 0) NextAttempt = now.AddSeconds(Math.Max(15,intervalSeconds));
        }
    }
}
