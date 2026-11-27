using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Text.Json;
using Aiursoft.DMS.Contracts;
using Microsoft.Win32.SafeHandles;

namespace Aiursoft.DMS.Agent;

public sealed partial class TaskJournal
{
    // A regular JSON checkpoint followed by RFC 7464 records (RS, compact JSON, LF).
    // An object is a WorkflowEvent. An integer acknowledges every event through that sequence.
    private const byte RecordSeparator = 0x1e;
    private bool _disposed;
    private bool _faulted;

    private static JournalTask ReadTask(string path)
    {
        var bytes = File.ReadAllBytes(path);
        var offset = Array.IndexOf(bytes, RecordSeparator);
        if (offset < 0) offset = bytes.Length;
        var checkpoint = bytes.AsSpan(0, offset);
        if (checkpoint.StartsWith(System.Text.Encoding.UTF8.Preamble)) checkpoint = checkpoint[3..];
        var task = JsonSerializer.Deserialize<JournalTask>(checkpoint) ?? throw new InvalidDataException("Missing Agent journal checkpoint.");
        if (!Guid.TryParse(Path.GetFileNameWithoutExtension(path), out var id) || id != task.Id || task.Sequence < task.Pending.Count)
            throw new InvalidDataException("Invalid Agent journal checkpoint.");
        var sequence = task.Sequence - task.Pending.Count;
        foreach (var item in task.Pending)
        {
            if (item.Sequence != ++sequence) throw new InvalidDataException("Discontinuous checkpoint sequence.");
            var size = JsonSerializer.SerializeToUtf8Bytes(item).Length;
            task.EventSizes.Enqueue(size);
            task.PendingBytes += size;
        }
        while (offset < bytes.Length)
        {
            if (bytes[offset] != RecordSeparator) throw new InvalidDataException("Missing Agent journal record separator.");
            var body = bytes.AsSpan(offset + 1);
            var newline = body.IndexOf((byte)'\n');
            if (newline < 0)
            {
                if (body.Contains(RecordSeparator)) throw new InvalidDataException("Broken Agent journal record before end of file.");
                // The writer never published an incomplete last record. Preserve all earlier committed events.
                using var stream = new FileStream(path, FileMode.Open, FileAccess.Write, FileShare.None);
                stream.SetLength(offset);
                stream.Flush(flushToDisk: true);
                break;
            }
            var record = body[..newline];
            if (!record.IsEmpty && record[0] == (byte)'{')
            {
                var item = JsonSerializer.Deserialize<WorkflowEvent>(record) ?? throw new InvalidDataException("Empty Agent journal event.");
                task.AddEvent(item, record.Length);
            }
            else
            {
                var ack = JsonSerializer.Deserialize<long>(record);
                if (ack <= 0 || ack > task.Sequence) throw new InvalidDataException("Invalid Agent journal acknowledgement.");
                task.Confirm(ack);
                task.ReclaimableBytes += record.Length + 2;
            }
            offset += record.Length + 2;
            task.HasRecords = true;
        }
        task.FileLength = offset;
        return task;
    }

    private void AppendRecord(JournalTask task, byte[] bytes)
    {
        EnsureWritable();
        using var stream = new FileStream(Path.Combine(_path, $"{task.Id:N}.json"), FileMode.Open, FileAccess.Write, FileShare.Read);
        VerifyLength(task, stream.Length);
        stream.Position = task.FileLength;
        try
        {
            stream.WriteByte(RecordSeparator);
            stream.Write(bytes);
            stream.WriteByte((byte)'\n');
            stream.Flush(flushToDisk: true);
        }
        catch
        {
            try { stream.SetLength(task.FileLength); stream.Flush(flushToDisk: true); }
            catch { _faulted = true; throw; }
            throw;
        }
        task.FileLength = stream.Position;
        task.HasRecords = true;
    }

    private void Save(JournalTask task, JournalTask? checkpoint = null)
    {
        EnsureWritable();
        var file = Path.Combine(_path, $"{task.Id:N}.json");
        if (task.FileLength > 0) VerifyLength(task, new FileInfo(file).Length);
        var temporary = file + ".tmp";
        long length;
        using (var stream = new FileStream(temporary, FileMode.Create, FileAccess.Write, FileShare.None))
        {
            JsonSerializer.Serialize(stream, checkpoint ?? task);
            stream.Flush(flushToDisk: true);
            length = stream.Length;
        }
        File.Move(temporary, file, overwrite: true);
        try { SyncDirectory(); }
        catch { _faulted = true; throw; }
        task.FileLength = length;
        task.HasRecords = false;
        task.ReclaimableBytes = 0;
    }

    private void VerifyLength(JournalTask task, long actual)
    {
        if (task.FileLength == actual) return;
        _faulted = true; // Disposal must also leave any unexpected bytes for recovery.
        throw new IOException("Agent journal length changed. Reopen the journal before writing.");
    }

    private void EnsureWritable()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_faulted) throw new IOException("The last Agent journal write could not be verified. Reopen the journal before writing.");
    }

    private void SyncDirectory()
    {
        if (!OperatingSystem.IsLinux()) return;
        // File.OpenHandle rejects directories. On the supported Linux hosts the rename
        // is durable only after the directory itself has been synced.
        const int closeOnExec = 0x80000;
        using var handle = OpenDirectory(_path, closeOnExec);
        if (handle.IsInvalid) throw new IOException("Cannot open Agent journal directory for sync.", new Win32Exception(Marshal.GetLastPInvokeError()));
        RandomAccess.FlushToDisk(handle);
    }

    [DllImport("libc", EntryPoint = "open", SetLastError = true)]
    private static extern SafeFileHandle OpenDirectory([MarshalAs(UnmanagedType.LPUTF8Str)] string path, int flags);
}
