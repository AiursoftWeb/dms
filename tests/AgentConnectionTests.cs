using Aiursoft.DMS.Agent;
using Aiursoft.DMS.Contracts;

namespace Aiursoft.DMS.Tests;

[TestClass]
public class AgentConnectionTests
{
    [TestMethod]
    public async Task AgentReconnectsWhenAnOpenWebSocketStopsResponding()
    {
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        builder.Logging.ClearProviders();
        await using var app = builder.Build();
        app.UseWebSockets(new WebSocketOptions { KeepAliveInterval = TimeSpan.Zero });

        using var stopping = new CancellationTokenSource();
        var token = stopping.Token;
        var connections = 0;
        var stalled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var reconnected = new TaskCompletionSource<AgentMessage?>(TaskCreationOptions.RunContinuationsAsynchronously);
        app.MapGet("/api/agent/channel", async context =>
        {
            using var socket = await context.WebSockets.AcceptWebSocketAsync();
            if (Interlocked.Increment(ref connections) == 1)
            {
                stalled.SetResult();
                // Keep TCP open without reading frames, so the server never answers a WebSocket ping.
                await Task.Delay(Timeout.InfiniteTimeSpan, token);
                return;
            }

            reconnected.TrySetResult(await WebSocketJson.ReceiveAsync(socket, token));
            await Task.Delay(Timeout.InfiniteTimeSpan, token);
        });
        await app.StartAsync();

        var path = Directory.CreateTempSubdirectory("dms-agent-reconnect-").FullName;
        try
        {
            using var journal = new TaskJournal(path);
            var agent = new AgentClient(new Uri(app.Urls.Single()), "fixture-agent-key", journal);
            var execution = agent.RunAsync(token);
            try
            {
                await stalled.Task.WaitAsync(TimeSpan.FromSeconds(10));
                var hello = await reconnected.Task.WaitAsync(TimeSpan.FromSeconds(55));
                Assert.IsNotNull(hello);
                Assert.AreEqual("hello", hello.Kind);
                Assert.AreEqual(journal.InstallationId, hello.InstallationId);
            }
            finally
            {
                await stopping.CancelAsync();
                try { await execution.WaitAsync(TimeSpan.FromSeconds(10)); }
                catch (OperationCanceledException) when (token.IsCancellationRequested) { }
            }
        }
        finally
        {
            await app.StopAsync();
            Directory.Delete(path, recursive: true);
        }
    }
}
