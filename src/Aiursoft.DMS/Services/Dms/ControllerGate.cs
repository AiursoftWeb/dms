namespace Aiursoft.DMS.Services.Dms;

// The first phase runs one Controller. All workflow state transitions share this gate.
public sealed class ControllerGate : IDisposable
{
    private readonly SemaphoreSlim _semaphore = new(1, 1);
    public async Task<T> RunAsync<T>(Func<Task<T>> action)
    {
        await _semaphore.WaitAsync();
        try { return await action(); }
        finally { _semaphore.Release(); }
    }
    public void Dispose() => _semaphore.Dispose();
}
