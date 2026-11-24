using System.Net;
using System.Security.Cryptography;
using Aiursoft.DMS.Workflows;

namespace Aiursoft.DMS.Tests;

[TestClass]
public class FrpsReleaseDownloadTests
{
    private static readonly Uri GitHub = new("https://api.github.com/repos/fatedier/frp/releases/assets/710291514");
    private static readonly Uri Proxy = new("https://gh-proxy.com/https://github.com/fatedier/frp/releases/download/v0.72.0/frp_0.72.0_linux_amd64.tar.gz");
    private static readonly byte[] Archive = "verified archive"u8.ToArray();
    private static readonly string Hash = Convert.ToHexString(SHA256.HashData(Archive));

    [TestMethod]
    public async Task SuccessfulGitHubDownloadDoesNotContactTheProxy()
    {
        using var handler = new ReleaseHandler(_ => Ok(new ByteArrayContent(Archive)));
        using var client = new HttpClient(handler);
        CollectionAssert.AreEqual(Archive, await DownloadAsync(client));
        CollectionAssert.AreEqual(new[] { GitHub }, handler.Requests);
    }

    [TestMethod]
    public async Task IncompleteGitHubBodyTimesOutAndTheProxyStartsAFreshDownload()
    {
        using var blocked = new PartialArchive();
        using var handler = new ReleaseHandler(url => url == GitHub
            ? Ok(new StreamContent(blocked))
            : Ok(new ByteArrayContent(Archive)));
        using var client = new HttpClient(handler);
        var trace = new List<string>();
        var downloaded = await DownloadAsync(client, githubTimeout: TimeSpan.FromSeconds(1), trace: trace);

        CollectionAssert.AreEqual(Archive, downloaded);
        CollectionAssert.AreEqual(new[] { GitHub, Proxy }, handler.Requests);
        StringAssert.Contains(trace.Single(), "timed out");
    }

    [TestMethod]
    public async Task UnavailableGitHubFallsBackButAnUnverifiedProxyArchiveIsRejected()
    {
        using var handler = new ReleaseHandler(url => url == GitHub
            ? new HttpResponseMessage(HttpStatusCode.ServiceUnavailable)
            : Ok(new ByteArrayContent("proxy error page"u8.ToArray())));
        using var client = new HttpClient(handler);
        var error = await Assert.ThrowsAsync<InvalidDataException>(() => DownloadAsync(client));

        StringAssert.Contains(error.Message, "SHA-256");
        CollectionAssert.AreEqual(new[] { GitHub, Proxy }, handler.Requests);
    }

    [TestMethod]
    public async Task UnverifiedGitHubArchiveStopsTheDeployment()
    {
        using var handler = new ReleaseHandler(_ => Ok(new ByteArrayContent("unexpected archive"u8.ToArray())));
        using var client = new HttpClient(handler);
        await Assert.ThrowsAsync<InvalidDataException>(() => DownloadAsync(client));
        CollectionAssert.AreEqual(new[] { GitHub }, handler.Requests);
    }

    [TestMethod]
    public async Task OperatorCancellationDoesNotContactTheProxy()
    {
        using var cancellation = new CancellationTokenSource();
        using var handler = new ReleaseHandler(_ =>
        {
            cancellation.Cancel();
            throw new OperationCanceledException(cancellation.Token);
        });
        using var client = new HttpClient(handler);
        await Assert.ThrowsAsync<OperationCanceledException>(() => DownloadAsync(client, cancellation.Token));
        CollectionAssert.AreEqual(new[] { GitHub }, handler.Requests);
    }

    private static Task<byte[]> DownloadAsync(HttpClient client, CancellationToken token = default,
        TimeSpan? githubTimeout = null, List<string>? trace = null) =>
        FrpsDeployWorkflow.DownloadArchiveAsync(client, GitHub, Proxy, Hash, githubTimeout ?? TimeSpan.FromSeconds(10),
            message =>
            {
                trace?.Add(message);
                return Task.CompletedTask;
            }, token);

    private static HttpResponseMessage Ok(HttpContent content) => new(HttpStatusCode.OK) { Content = content };

    private sealed class ReleaseHandler(Func<Uri, HttpResponseMessage> respond) : HttpMessageHandler
    {
        public List<Uri> Requests { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var url = request.RequestUri!;
            Requests.Add(url);
            return Task.FromResult(respond(url));
        }
    }

    private sealed class PartialArchive() : MemoryStream(Archive)
    {
        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            if (Position == 0) return await base.ReadAsync(buffer[..1], cancellationToken);
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            return 0;
        }
    }
}
