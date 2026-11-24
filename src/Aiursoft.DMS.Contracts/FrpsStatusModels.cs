namespace Aiursoft.DMS.Contracts;

public sealed record FrpsStatusResult(DateTimeOffset CheckedAtUtc, IReadOnlyList<FrpsServiceStatus> Services);
public sealed record FrpsServiceStatus(string Unit, string Installation, string LoadState, string ActiveState,
    string SubState, string UnitFileState, int? MainPid, DateTimeOffset? StartedAtUtc, int? AutomaticRestarts,
    string? Executable, string? InstalledVersion, string? RunningVersion, string? RunningExecutable,
    string? ConfigurationPath, bool? ConfigurationPresent, bool? InMemoryConfiguration,
    IReadOnlyList<FrpsListener>? Listeners, FrpsJournalSummary? RecentJournal, IReadOnlyList<string> Diagnostics);
public sealed record FrpsListener(string Protocol, string LocalEndpoint);
public sealed record FrpsJournalSummary(int WindowMinutes, int SampleLimit, int Entries, bool LimitReached,
    int Warnings, int Errors, int ClientLogins, int ClientReplacements, int ProxyStarts, int ProxyClosures,
    int WorkConnectionFailures, int WorkPoolFull);
