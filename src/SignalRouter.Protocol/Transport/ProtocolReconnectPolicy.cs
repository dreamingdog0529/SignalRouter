using System;

namespace SignalRouter.Protocol.Transport
{
    // Pure backoff calculator for the runtime bridge's reconnect loop
    // (ADR 0007: the runtime owns reconnection because the host only listens).
    // Full jitter over an exponentially growing cap: attempt N may wait
    // anywhere in [0, min(maxDelay, initialDelay * multiplier^N)], which
    // spreads thundering reconnects while keeping the first retries fast.
    // Randomness is injected so the delay series is testable.
    public sealed class ProtocolReconnectPolicy
    {
        private readonly TimeSpan initialDelay;
        private readonly double multiplier;
        private readonly TimeSpan maxDelay;
        private readonly Func<double> random;

        public ProtocolReconnectPolicy(
            TimeSpan initialDelay,
            double multiplier,
            TimeSpan maxDelay,
            Func<double> random)
        {
            if (initialDelay <= TimeSpan.Zero)
            {
                throw new ArgumentOutOfRangeException(
                    nameof(initialDelay),
                    initialDelay,
                    "The initial delay must be positive.");
            }

            if (multiplier < 1.0)
            {
                throw new ArgumentOutOfRangeException(
                    nameof(multiplier),
                    multiplier,
                    "The multiplier must be at least one.");
            }

            if (maxDelay < initialDelay)
            {
                throw new ArgumentOutOfRangeException(
                    nameof(maxDelay),
                    maxDelay,
                    "The delay cap must not be below the initial delay.");
            }

            this.initialDelay = initialDelay;
            this.multiplier = multiplier;
            this.maxDelay = maxDelay;
            this.random = random ?? throw new ArgumentNullException(nameof(random));
        }

        // 250 ms doubling to a 5 s cap: fast enough that an editor domain
        // reload reconnects almost immediately, bounded so a stopped host is
        // polled gently forever (the bridge retries indefinitely while
        // enabled).
        public static ProtocolReconnectPolicy CreateDefault(Func<double> random)
        {
            return new ProtocolReconnectPolicy(
                TimeSpan.FromMilliseconds(250),
                2.0,
                TimeSpan.FromSeconds(5),
                random);
        }

        public TimeSpan NextDelay(int attempt)
        {
            if (attempt < 0)
            {
                throw new ArgumentOutOfRangeException(
                    nameof(attempt),
                    attempt,
                    "Attempts are zero-based.");
            }

            var ceiling = initialDelay.TotalMilliseconds;
            for (var index = 0; index < attempt; index++)
            {
                ceiling *= multiplier;
                if (ceiling >= maxDelay.TotalMilliseconds)
                {
                    ceiling = maxDelay.TotalMilliseconds;
                    break;
                }
            }

            var sample = random();
            if (sample < 0.0 || sample >= 1.0)
            {
                throw new InvalidOperationException(
                    "The injected random source must return values in [0, 1).");
            }

            return TimeSpan.FromMilliseconds(ceiling * sample);
        }
    }
}
