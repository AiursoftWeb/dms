namespace Aiursoft.DMS.Services.Dms;

public sealed class LockboxReviewSignal
{
    private TaskCompletionSource _change = NewSignal();

    public Task Snapshot() => Volatile.Read(ref _change).Task;

    internal void Pulse() => Interlocked.Exchange(ref _change, NewSignal()).TrySetResult();

    private static TaskCompletionSource NewSignal() => new(TaskCreationOptions.RunContinuationsAsynchronously);
}
