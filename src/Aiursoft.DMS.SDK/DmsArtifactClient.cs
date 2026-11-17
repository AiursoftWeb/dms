using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using Aiursoft.DMS.Contracts;

namespace Aiursoft.DMS.SDK;

// Binary bytes use HTTP streaming; metadata retains the ordinary AiurProtocol envelope.
public sealed class DmsArtifactClient(string endpoint, string key, Guid? machineTask = null)
{
    private static readonly HttpClient Http = new(new HttpClientHandler { AllowAutoRedirect = false }) { Timeout = TimeSpan.FromMinutes(15) };
    private string Route => endpoint.TrimEnd('/') + (machineTask == null ? "/api/v1/dms/artifacts" : "/api/agent/artifacts");
    private string TaskQuery => machineTask == null ? "" : "taskId=" + machineTask;
    private HttpRequestMessage Request(HttpMethod method, string url)
    {
        var request = new HttpRequestMessage(method, url); request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", key); return request;
    }
    public async Task<ArtifactReference> UploadAsync(string file, string name, string kind, CancellationToken token = default, Guid? requestId = null)
    {
        await using var stream = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.Read);
        if (stream.Length > 1073741824) throw new ArgumentException("Artifacts are limited to one GiB.");
        var length = stream.Length; var hash = Convert.ToHexStringLower(await SHA256.HashDataAsync(stream, token)); stream.Position = 0;
        // Retrying the same CLI submission freezes the same artifact identity as well as the same bytes.
        var id = requestId == null ? Guid.NewGuid() : new Guid(SHA256.HashData(Encoding.UTF8.GetBytes($"{requestId:D}\n{hash}\n{length}\n{kind}\n{name}")).AsSpan(0, 16));
        var reference = new ArtifactReference(id, hash, length, kind, name); reference.Validate();
        var status = await SendUploadAsync(HttpMethod.Post, Route + "/uploads?" + TaskQuery, () => JsonContent.Create(reference), token);
        var buffer = new byte[8 * 1024 * 1024];
        while (!status.Complete && status.Offset < length)
        {
            if (status.Offset < 0 || status.Offset > length) throw new InvalidDataException("Controller returned an invalid upload position.");
            var offset = status.Offset; stream.Position = offset;
            var count = (int)Math.Min(buffer.Length, length - offset); await stream.ReadExactlyAsync(buffer.AsMemory(0, count), token);
            status = await SendUploadAsync(HttpMethod.Put, Route + "/uploads/" + reference.Id + "?offset=" + offset + "&" + TaskQuery,
                () => new ByteArrayContent(buffer, 0, count), token);
            if (status.Offset <= offset || status.Offset > length) throw new InvalidDataException("Controller did not confirm the uploaded chunk position.");
        }
        status = await SendUploadAsync(HttpMethod.Post, Route + "/uploads/" + reference.Id + "/complete?" + TaskQuery, () => new ByteArrayContent([]), token);
        if (!status.Complete || status.Artifact != reference) throw new InvalidDataException("Uploaded bytes differ from the frozen local length or SHA256.");
        return status.Artifact;
    }
    private async Task<ArtifactUploadStatus> SendUploadAsync(HttpMethod method, string url, Func<HttpContent> body, CancellationToken token)
    {
        // Every retry uses the same immutable upload ID, byte offset and bytes. It never resubmits a workflow.
        for (var attempt = 0; ; attempt++)
        {
            try
            {
                using var request = Request(method, url); request.Content = body();
                using var response = await Http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, token); response.EnsureSuccessStatusCode();
                return (await response.Content.ReadFromJsonAsync<ArtifactUploadResponse>(cancellationToken: token))?.Data ?? throw new InvalidDataException("Controller returned no upload position.");
            }
            catch (HttpRequestException exception) when (attempt < 2 && (exception.StatusCode == null || (int)exception.StatusCode >= 500 || exception.StatusCode is System.Net.HttpStatusCode.RequestTimeout or System.Net.HttpStatusCode.TooManyRequests))
            { await Task.Delay(TimeSpan.FromMilliseconds(250 * (attempt + 1)), token); }
            catch (TaskCanceledException) when (attempt < 2 && !token.IsCancellationRequested)
            { await Task.Delay(TimeSpan.FromMilliseconds(250 * (attempt + 1)), token); }
        }
    }
    public async Task<ArtifactReference> GetAsync(Guid id, CancellationToken token = default)
    {
        using var request = Request(HttpMethod.Get, Route + "/" + id + "?" + TaskQuery);
        using var response = await Http.SendAsync(request, token); response.EnsureSuccessStatusCode();
        var artifact = (await response.Content.ReadFromJsonAsync<ArtifactResponse>(cancellationToken: token))?.Data ?? throw new InvalidDataException("Controller returned no artifact metadata."); artifact.Validate(); return artifact;
    }
    public async Task DownloadAsync(ArtifactReference artifact, string destination, bool overwrite = false, CancellationToken token = default)
    {
        artifact.Validate(); destination = Path.GetFullPath(destination);
        if (!overwrite && File.Exists(destination)) throw new IOException("The local destination exists.");
        var temporary = Path.Combine(Path.GetDirectoryName(destination)!, ".dms-download-" + Guid.NewGuid().ToString("N"));
        try
        {
            await using (var output = new FileStream(temporary, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None))
            {
                if (OperatingSystem.IsLinux()) File.SetUnixFileMode(temporary, UnixFileMode.UserRead | UnixFileMode.UserWrite);
                for (var attempt = 0; ; attempt++)
                {
                    try
                    {
                        var offset = output.Position;
                        using var request = Request(HttpMethod.Get, Route + "/" + artifact.Id + "/content?" + TaskQuery);
                        if (offset != 0) request.Headers.Range = new RangeHeaderValue(offset, null);
                        using var response = await Http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, token); response.EnsureSuccessStatusCode();
                        if (response.StatusCode == System.Net.HttpStatusCode.PartialContent)
                        {
                            var range = response.Content.Headers.ContentRange;
                            if (range?.From != offset || range.Length != artifact.Length) throw new InvalidDataException("Controller returned a different artifact byte range.");
                        }
                        else if (offset != 0) { output.SetLength(0); output.Position = 0; }
                        await using var input = await response.Content.ReadAsStreamAsync(token);
                        var buffer = new byte[65536]; int count;
                        while (true)
                        {
                            try { count = await input.ReadAsync(buffer, token); }
                            catch (IOException exception) { throw new HttpRequestException("Artifact connection was interrupted.", exception); }
                            if (count == 0) break;
                            if (output.Position + count > artifact.Length) throw new InvalidDataException("Artifact exceeded its approved byte length.");
                            await output.WriteAsync(buffer.AsMemory(0, count), token);
                        }
                        if (output.Position != artifact.Length) throw new HttpRequestException("Artifact stream ended before the declared length.");
                        break;
                    }
                    catch (HttpRequestException exception) when (attempt < 2 && (exception.StatusCode == null || (int)exception.StatusCode >= 500 || exception.StatusCode is System.Net.HttpStatusCode.RequestTimeout or System.Net.HttpStatusCode.TooManyRequests))
                    { await Task.Delay(TimeSpan.FromMilliseconds(250 * (attempt + 1)), token); }
                    catch (HttpIOException) when (attempt < 2) { await Task.Delay(TimeSpan.FromMilliseconds(250 * (attempt + 1)), token); }
                    catch (TaskCanceledException) when (attempt < 2 && !token.IsCancellationRequested) { await Task.Delay(TimeSpan.FromMilliseconds(250 * (attempt + 1)), token); }
                }
                await output.FlushAsync(token);
                output.Position = 0;
                if (!Convert.ToHexStringLower(await SHA256.HashDataAsync(output, token)).Equals(artifact.Sha256, StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("Artifact SHA256 does not match.");
            }
            File.Move(temporary, destination, overwrite);
        }
        finally { File.Delete(temporary); }
    }
}
