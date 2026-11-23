namespace Aiursoft.DMS.Contracts;

public sealed record FilesystemEntry(string Path, bool Directory, bool SymbolicLink, string? LinkTarget,
    long? Bytes, DateTime LastWriteTimeUtc, DateTime CreationTimeUtc, string? Mode);

public sealed record MachineFilesystemListResult(Guid MachineId, string Machine, Guid WorkflowId,
    RunState State, bool? Complete, IReadOnlyList<FilesystemEntry>? Entries);

public sealed record FilesystemListResult(Guid WorkflowId, RunState State, string Path,
    IReadOnlyList<MachineFilesystemListResult> Machines);
