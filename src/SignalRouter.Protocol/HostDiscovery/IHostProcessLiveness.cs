using System;
using System.Diagnostics;

namespace SignalRouter.Protocol.HostDiscovery
{
    // Decides whether a descriptor's (pid, startedAt) still names a live host.
    // This is a stale-descriptor heuristic guarded against pid reuse, NOT proof of
    // host identity (ADR 0008). Injectable so the descriptor resolver can be tested
    // without spawning processes.
    public interface IHostProcessLiveness
    {
        bool IsAlive(int processId, DateTimeOffset startedAt);
    }

    // Default liveness check over the OS process table. A process is live only when
    // one exists for the pid AND its start time matches the recorded startedAt
    // (within a small tolerance), so a reused pid does not read as the old host.
    // Every Process failure mode — no such process, already exited, access denied —
    // resolves to "not alive" rather than throwing.
    public sealed class ProcessHostLiveness : IHostProcessLiveness
    {
        private static readonly TimeSpan StartTimeTolerance = TimeSpan.FromSeconds(2);

        public bool IsAlive(int processId, DateTimeOffset startedAt)
        {
            if (processId <= 0)
            {
                return false;
            }

            try
            {
                using var process = Process.GetProcessById(processId);
                if (process.HasExited)
                {
                    return false;
                }

                var actualStart = new DateTimeOffset(process.StartTime.ToUniversalTime());
                var difference = actualStart - startedAt;
                if (difference < TimeSpan.Zero)
                {
                    difference = difference.Negate();
                }

                return difference <= StartTimeTolerance;
            }
            catch (ArgumentException)
            {
                // No process has that id.
                return false;
            }
            catch (InvalidOperationException)
            {
                // The process exited between lookup and inspection.
                return false;
            }
            catch (System.ComponentModel.Win32Exception)
            {
                // The start time is inaccessible (permissions or a system process).
                return false;
            }
        }
    }
}
