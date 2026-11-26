namespace Aiursoft.DMS.Lockbox.Android;

internal sealed class DeferredReviewNavigation
{
    private bool _pending;

    public void Request() => _pending = true;

    public bool TryConsume(bool busy, CancellationToken lifetime)
    {
        if (!_pending || busy || lifetime.IsCancellationRequested) return false;
        _pending = false;
        return true;
    }
}
