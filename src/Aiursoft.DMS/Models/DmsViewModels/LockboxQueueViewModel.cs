using Aiursoft.DMS.Contracts;
using Aiursoft.DMS.Entities;
using Aiursoft.UiStack.Layout;

namespace Aiursoft.DMS.Models.DmsViewModels;

public sealed class LockboxQueueViewModel : UiStackLayoutViewModel
{
    public required IReadOnlyList<LockboxQueueEntry> Entries { get; init; }
    public required bool OwnRequests { get; init; }
    public required string Scope { get; init; }
    public required string Search { get; init; }
    public required int Page { get; init; }
    public required bool HasNextPage { get; init; }
    public string? Notice { get; init; }
}

public sealed record LockboxQueueEntry(LockboxRequest Item, string Requester, IReadOnlyList<string> TargetNames,
    int TargetCount, bool CanDecide, RunState? ExecutionState);
