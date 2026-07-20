using System.Collections.Concurrent;

namespace SignalRouter.Core.Tests;

internal sealed class SingleThreadSynchronizationContext : SynchronizationContext, IDisposable
{
    private readonly BlockingCollection<WorkItem> queue = new();
    private readonly Thread thread;

    public SingleThreadSynchronizationContext()
    {
        thread = new Thread(RunLoop)
        {
            IsBackground = true,
            Name = "signalrouter-test-main",
        };
        thread.Start();
    }

    public int ThreadId
    {
        get { return thread.ManagedThreadId; }
    }

    public override void Post(SendOrPostCallback d, object? state)
    {
        queue.Add(new WorkItem(d, state));
    }

    public override void Send(SendOrPostCallback d, object? state)
    {
        throw new NotSupportedException("Synchronous send is not supported.");
    }

    public Task Run(Func<Task> asyncMethod)
    {
        var completion = new TaskCompletionSource<bool>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        Post(
            async _ =>
            {
                try
                {
                    await asyncMethod();
                    completion.SetResult(true);
                }
                catch (Exception exception)
                {
                    completion.SetException(exception);
                }
            },
            null);
        return completion.Task;
    }

    public void Dispose()
    {
        queue.CompleteAdding();
    }

    private void RunLoop()
    {
        SetSynchronizationContext(this);
        foreach (var item in queue.GetConsumingEnumerable())
        {
            item.Callback(item.State);
        }
    }

    private readonly struct WorkItem
    {
        public WorkItem(SendOrPostCallback callback, object? state)
        {
            Callback = callback;
            State = state;
        }

        public SendOrPostCallback Callback { get; }

        public object? State { get; }
    }
}
