using System.Security.Cryptography;
using System.Text.Json;
using Aiursoft.DMS.Contracts;

namespace Aiursoft.DMS.Services.Dms;

public sealed partial class DmsArtifactStore
{
    public const int ChunkBytes = 8 * 1024 * 1024;
    private string UploadDirectory(Guid id) => Path.Combine(_root, ".upload-" + id.ToString("N"));
    public StoredDmsArtifact UploadRecord(Guid id)
    {
        if (Directory.Exists(DirectoryFor(id))) return Get(id);
        var file = Path.Combine(UploadDirectory(id), "upload.json");
        if (!File.Exists(file)) throw new DmsException(404, "Upload not found.");
        var record = JsonSerializer.Deserialize<StoredDmsArtifact>(File.ReadAllText(file)) ?? throw new InvalidDataException("Invalid upload metadata.");
        if (record.Reference.Id != id || record.ExpiresAt <= DateTime.UtcNow) throw new DmsException(404, "Upload expired.");
        return record;
    }
    public ArtifactUploadStatus UploadStatus(Guid id)
    {
        var record = UploadRecord(id); var complete = Directory.Exists(DirectoryFor(id));
        var file = Path.Combine(complete ? DirectoryFor(id) : UploadDirectory(id), "payload");
        return new ArtifactUploadStatus(record.Reference, File.Exists(file) ? new FileInfo(file).Length : 0, complete);
    }
    public async Task<ArtifactUploadStatus> BeginUploadAsync(ArtifactReference reference, string owner, Guid? task, CancellationToken token)
    {
        reference.Validate(); reference = reference with { Sha256 = reference.Sha256.ToLowerInvariant() };
        await _writes.WaitAsync(token);
        try
        {
            Directory.CreateDirectory(_root);
            if (OperatingSystem.IsLinux()) File.SetUnixFileMode(_root, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
            Expire();
            if (Directory.Exists(UploadDirectory(reference.Id)) || Directory.Exists(DirectoryFor(reference.Id)))
            {
                var old = UploadRecord(reference.Id);
                if (old.OwnerId != owner || old.MachineTaskId != task || old.Reference != reference) throw new DmsException(409, "Upload ID already belongs to different bytes or ownership.");
                return UploadStatus(reference.Id);
            }
            long reserved = 0;
            foreach (var directory in Directory.EnumerateDirectories(_root))
            {
                var metadata = Path.Combine(directory, Path.GetFileName(directory).StartsWith(".upload-", StringComparison.Ordinal) ? "upload.json" : "artifact.json");
                if (File.Exists(metadata)) reserved += JsonSerializer.Deserialize<StoredDmsArtifact>(await File.ReadAllTextAsync(metadata, token))!.Reference.Length;
            }
            if (reserved + reference.Length > _maximumTotal) throw new DmsException(409, "Artifact storage capacity is reserved. Temporary transfers expire; audited script/SQL inputs are retained. Review Dms:Artifacts:MaximumTotalBytes if more capacity is required.");
            var path = UploadDirectory(reference.Id); Directory.CreateDirectory(path);
            if (OperatingSystem.IsLinux()) File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
            await using (var stream = new FileStream(Path.Combine(path, "upload.json"), FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                await JsonSerializer.SerializeAsync(stream, new StoredDmsArtifact(reference, owner, task, DateTime.UtcNow, DateTime.UtcNow.AddHours(24)), cancellationToken: token);
                stream.Flush(true);
            }
            return new ArtifactUploadStatus(reference, 0, false);
        }
        finally { _writes.Release(); }
    }
    public async Task<ArtifactUploadStatus> AppendUploadAsync(Guid id, long offset, Stream source, CancellationToken token)
    {
        if (offset < 0) throw new DmsException(400, "Chunk offset must be nonnegative.");
        using var buffer = new MemoryStream(); var bytes = new byte[65536]; int count;
        while ((count = await source.ReadAsync(bytes, token)) != 0)
        {
            if (buffer.Length + count > ChunkBytes) throw new DmsException(400, "An upload chunk is limited to eight MiB.");
            buffer.Write(bytes, 0, count);
        }
        await _writes.WaitAsync(token);
        try
        {
            var status = UploadStatus(id);
            if (offset > status.Offset || offset + buffer.Length > status.Artifact.Length) throw new DmsException(409, "Chunk offset differs from the recorded upload position.");
            var payload = Path.Combine(status.Complete ? DirectoryFor(id) : UploadDirectory(id), "payload");
            await using var file = new FileStream(payload, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.Read);
            var overlap = (int)Math.Min(buffer.Length, file.Length - offset);
            file.Position = offset;
            if (overlap > 0)
            {
                var existing = new byte[overlap]; await file.ReadExactlyAsync(existing, token);
                if (!CryptographicOperations.FixedTimeEquals(existing, buffer.GetBuffer().AsSpan(0, overlap))) throw new DmsException(409, "Repeated chunk contains different bytes.");
            }
            if (status.Complete && overlap != buffer.Length) throw new DmsException(409, "A published artifact is immutable.");
            if (!status.Complete)
            {
                file.Position = offset + overlap;
                await file.WriteAsync(buffer.GetBuffer().AsMemory(overlap, (int)buffer.Length - overlap), token); file.Flush(true);
            }
            return new ArtifactUploadStatus(status.Artifact, file.Length, status.Complete);
        }
        finally { _writes.Release(); }
    }
    public async Task<ArtifactUploadStatus> CompleteUploadAsync(Guid id, CancellationToken token)
    {
        await _writes.WaitAsync(token);
        try
        {
            var status = UploadStatus(id); if (status.Complete) return status;
            if (status.Offset != status.Artifact.Length) throw new DmsException(409, "Upload is incomplete.");
            var directory = UploadDirectory(id); var payload = Path.Combine(directory, "payload");
            if (!File.Exists(payload)) await File.WriteAllBytesAsync(payload, [], token);
            await using (var file = File.OpenRead(payload))
                if (!Convert.ToHexStringLower(await SHA256.HashDataAsync(file, token)).Equals(status.Artifact.Sha256, StringComparison.OrdinalIgnoreCase)) throw new DmsException(400, "Uploaded bytes do not match the declared SHA256.");
            await using (var metadata = new FileStream(Path.Combine(directory, "artifact.json"), FileMode.Create, FileAccess.Write, FileShare.None))
            { await JsonSerializer.SerializeAsync(metadata, UploadRecord(id), cancellationToken: token); metadata.Flush(true); }
            Directory.Move(directory, DirectoryFor(id));
            return status with { Complete = true };
        }
        finally { _writes.Release(); }
    }
    public async Task RetainAuditInputAsync(Guid id, string owner)
    {
        await _writes.WaitAsync();
        try
        {
            var record = Get(id);
            if (record.OwnerId != owner || record.Reference.Kind != "file" || record.Reference.Length > 262144)
                throw new DmsException(403, "Only the requester's bounded text input can be retained for audit.");
            if (record.AuditInput) return;
            var file = Path.Combine(DirectoryFor(id), "artifact.json"); var temporary = file + ".next";
            await using (var output = new FileStream(temporary, FileMode.Create, FileAccess.Write, FileShare.None))
            { await JsonSerializer.SerializeAsync(output, record with { AuditInput = true, ExpiresAt = DateTime.SpecifyKind(DateTime.MaxValue, DateTimeKind.Utc) }); output.Flush(true); }
            File.Move(temporary, file, true);
        }
        finally { _writes.Release(); }
    }
}
