using System;
using System.Threading;

namespace DriveMapper;

// Cancellation tokens cannot interrupt synchronous network-provider calls. A separate timer
// terminates the short-lived process even when a manual reconciliation is stuck in one.
internal sealed class ExecutionDeadline : IDisposable
{
    private readonly Timer timer;
    public ExecutionDeadline(TimeSpan timeout)
        => timer = new Timer(_ => Environment.Exit(10), null, timeout, Timeout.InfiniteTimeSpan);
    public void Dispose() => timer.Dispose();
}
