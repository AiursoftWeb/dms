using System.Security.Cryptography;
using Aiursoft.DMS.Contracts;
using Aiursoft.DMS.Services.Dms;
using Aiursoft.DMS.SDK;

namespace Aiursoft.DMS.Tests;

[TestClass]
public class ArtifactTransferTests
{
    [TestMethod]
    public async Task InterruptedDownloadResumesTheSameArtifactAndChecksTheCompleteHash()
    {
        var directory = Directory.CreateTempSubdirectory("dms-download-resume-").FullName;
        var bytes = RandomNumberGenerator.GetBytes(1024 * 1024 + 17);
        var reference = new ArtifactReference(Guid.NewGuid(), Convert.ToHexStringLower(SHA256.HashData(bytes)), bytes.Length);
        var builder = WebApplication.CreateBuilder(); builder.WebHost.UseUrls("http://127.0.0.1:0"); builder.Logging.ClearProviders();
        await using var app = builder.Build(); var requests = 0; long resumedAt = 0;
        app.MapGet("/api/v1/dms/artifacts/{id:guid}/content", async context =>
        {
            var call = Interlocked.Increment(ref requests);
            if (call == 1)
            {
                context.Response.ContentLength = bytes.Length;
                await context.Response.Body.WriteAsync(bytes.AsMemory(0, 65536)); await context.Response.Body.FlushAsync();
                await Task.Delay(100); context.Abort(); return;
            }
            var range = System.Net.Http.Headers.RangeHeaderValue.Parse(context.Request.Headers.Range.ToString());
            resumedAt = range.Ranges.Single().From!.Value;
            context.Response.StatusCode = 206;
            context.Response.Headers.ContentRange = $"bytes {resumedAt}-{bytes.Length - 1}/{bytes.Length}";
            context.Response.ContentLength = bytes.Length - resumedAt;
            await context.Response.Body.WriteAsync(bytes.AsMemory((int)resumedAt));
        });
        await app.StartAsync();
        try
        {
            var path = Path.Combine(directory, "download.bin");
            await new DmsArtifactClient(app.Urls.Single(), "fixture-download-key").DownloadAsync(reference, path);
            Assert.AreEqual(2, requests); Assert.IsGreaterThan(0L, resumedAt);
            Assert.AreEqual(reference.Sha256, Convert.ToHexStringLower(SHA256.HashData(await File.ReadAllBytesAsync(path))));
        }
        finally { await app.StopAsync(); Directory.Delete(directory, true); }
    }
    [TestMethod]
    public async Task ChunkRetriesCompareBytesAndPublishedArtifactsAreImmutable()
    {
        var path = Directory.CreateTempSubdirectory("dms-artifact-chunks-").FullName;
        try
        {
            var store = new DmsArtifactStore(new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?> { ["Dms:StatePath"] = path }).Build());
            var bytes = RandomNumberGenerator.GetBytes(DmsArtifactStore.ChunkBytes + 17);
            var reference = new ArtifactReference(Guid.NewGuid(), Convert.ToHexStringLower(SHA256.HashData(bytes)), bytes.Length, Name: "payload.bin");
            var begin = await store.BeginUploadAsync(reference, "owner", null, CancellationToken.None); Assert.AreEqual(0, begin.Offset);
            var chunk = bytes.AsSpan(0, DmsArtifactStore.ChunkBytes).ToArray();
            var first = await store.AppendUploadAsync(reference.Id, 0, new MemoryStream(chunk), CancellationToken.None); Assert.AreEqual(chunk.Length, first.Offset);
            var repeated = await store.AppendUploadAsync(reference.Id, 0, new MemoryStream(chunk), CancellationToken.None); Assert.AreEqual(first.Offset, repeated.Offset);
            Assert.AreEqual(409, (await Assert.ThrowsAsync<DmsException>(() => store.AppendUploadAsync(reference.Id, 0, new MemoryStream([(byte)(chunk[0] ^ 255)]), CancellationToken.None))).StatusCode);
            Assert.AreEqual(409, (await Assert.ThrowsAsync<DmsException>(() => store.CompleteUploadAsync(reference.Id, CancellationToken.None))).StatusCode);
            await store.AppendUploadAsync(reference.Id, first.Offset, new MemoryStream(bytes.AsSpan(chunk.Length).ToArray()), CancellationToken.None);
            var complete = await store.CompleteUploadAsync(reference.Id, CancellationToken.None); Assert.IsTrue(complete.Complete); Assert.AreEqual(reference, complete.Artifact);
            Assert.IsTrue((await store.CompleteUploadAsync(reference.Id, CancellationToken.None)).Complete);
            Assert.AreEqual(409, (await Assert.ThrowsAsync<DmsException>(() => store.BeginUploadAsync(reference with { Name = "different" }, "owner", null, CancellationToken.None))).StatusCode);
            Assert.AreEqual(409, (await Assert.ThrowsAsync<DmsException>(() => store.BeginUploadAsync(reference, "other-owner", null, CancellationToken.None))).StatusCode);
            await using var file = store.Open(reference.Id); Assert.AreEqual(reference.Length, file.Length);
            Assert.AreEqual(reference.Sha256, Convert.ToHexStringLower(await SHA256.HashDataAsync(file)));
        }
        finally { Directory.Delete(path, true); }
    }
    [TestMethod]
    public async Task WrongHashAndAnOversizedChunkCannotPublishAnArtifact()
    {
        var path = Directory.CreateTempSubdirectory("dms-artifact-invalid-").FullName;
        try
        {
            var store = new DmsArtifactStore(new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?> { ["Dms:StatePath"] = path }).Build());
            var reference = new ArtifactReference(Guid.NewGuid(), new string('a', 64), 1);
            await store.BeginUploadAsync(reference, "owner", null, CancellationToken.None);
            await store.AppendUploadAsync(reference.Id, 0, new MemoryStream([1]), CancellationToken.None);
            Assert.AreEqual(400, (await Assert.ThrowsAsync<DmsException>(() => store.CompleteUploadAsync(reference.Id, CancellationToken.None))).StatusCode);
            Assert.AreEqual(404, Assert.Throws<DmsException>(() => store.Get(reference.Id)).StatusCode);
            Assert.AreEqual(400, (await Assert.ThrowsAsync<DmsException>(() => store.AppendUploadAsync(reference.Id, 0, new MemoryStream(new byte[DmsArtifactStore.ChunkBytes + 1]), CancellationToken.None))).StatusCode);
            Assert.AreEqual(1, store.UploadStatus(reference.Id).Offset);
        }
        finally { Directory.Delete(path, true); }
    }
    [TestMethod]
    public async Task IncompleteUploadsReserveCapacityWithoutAllocatingDeclaredGigabytes()
    {
        var path = Directory.CreateTempSubdirectory("dms-artifact-quota-").FullName;
        try
        {
            var store = new DmsArtifactStore(new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?> { ["Dms:StatePath"] = path }).Build());
            for (var i = 0; i < 10; i++) await store.BeginUploadAsync(new ArtifactReference(Guid.NewGuid(), new string('0', 64), 1073741824), "owner", null, CancellationToken.None);
            Assert.AreEqual(409, (await Assert.ThrowsAsync<DmsException>(() => store.BeginUploadAsync(new ArtifactReference(Guid.NewGuid(), new string('0', 64), 1), "owner", null, CancellationToken.None))).StatusCode);
            Assert.IsTrue(Directory.EnumerateFiles(path, "*", SearchOption.AllDirectories).Sum(f => new FileInfo(f).Length) < 65536);
        }
        finally { Directory.Delete(path, true); }
    }
}
