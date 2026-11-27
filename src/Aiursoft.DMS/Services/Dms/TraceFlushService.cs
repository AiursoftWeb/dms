namespace Aiursoft.DMS.Services.Dms;

public sealed class TraceFlushService(IServiceScopeFactory scopes, TraceStore store, ILogger<TraceFlushService> logger) : BackgroundService
{
    private DateTime _lastAuditBacklogReport;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var initializeTrace = true;
        while (!stoppingToken.IsCancellationRequested)
        {
            using var scope = scopes.CreateScope();
            var archive = scope.ServiceProvider.GetRequiredService<WorkflowAuditArchive>();
            try
            {
                await archive.FlushAsync(stoppingToken);
                if (archive.Pending > 0 && DateTime.UtcNow - _lastAuditBacklogReport > TimeSpan.FromMinutes(1))
                {
                    logger.LogWarning("{Pending} structured workflow audit events await archive delivery.", archive.Pending);
                    _lastAuditBacklogReport = DateTime.UtcNow;
                }
            }
            catch (Exception exception) when (!stoppingToken.IsCancellationRequested)
            { logger.LogError(exception, "Structured workflow audit archive is unavailable; persisted backlog is retained. Last observed pending: {Pending}.", archive.Pending); }
            // Keep trace and structured-audit delivery independent, using the existing background loop.
            try
            {
                var database = scope.ServiceProvider.GetRequiredService<TraceDatabase>();
                if (initializeTrace && database.Enabled)
                {
                    // Apply retention on startup even when the old archive receives no further events.
                    await database.InitializeAsync(stoppingToken);
                    initializeTrace = false;
                }
                if (await store.FlushAsync(database, stoppingToken))
                    continue;
            }
            catch (Exception exception) when (!stoppingToken.IsCancellationRequested)
            { logger.LogError(exception, "Trace archive is unavailable; durable backlog is retained."); }
            await Task.Delay(TimeSpan.FromSeconds(2), stoppingToken);
        }
    }
}
