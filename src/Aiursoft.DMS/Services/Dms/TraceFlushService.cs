namespace Aiursoft.DMS.Services.Dms;

public sealed class TraceFlushService(IServiceScopeFactory scopes, TraceStore store, ILogger<TraceFlushService> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                using var scope = scopes.CreateScope();
                await store.FlushAsync(scope.ServiceProvider.GetRequiredService<TraceDatabase>());
            }
            catch (Exception exception) { logger.LogError(exception, "Trace archive is unavailable; durable backlog is retained."); }
            await Task.Delay(TimeSpan.FromSeconds(2), stoppingToken);
        }
    }
}
