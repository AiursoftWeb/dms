using System.Text.RegularExpressions;

namespace Aiursoft.DMS.Contracts;

public sealed partial record BoxDeployInput(int Stage = 4, string? Filter = null)
{
    public void Validate()
    {
        if (Stage is < 0 or > 4) throw new ArgumentException("Select a starting stage from 0 to 4.");
        if (Filter != null && !StackFilter().IsMatch(Filter))
            throw new ArgumentException("Supply a literal stack-name substring, or omit the filter for all stage-4 stacks.");
    }

    [GeneratedRegex(@"\A[a-zA-Z0-9][a-zA-Z0-9_.-]{0,127}\z")]
    private static partial Regex StackFilter();
}

public sealed record BoxDeployResult(string Repository, string PreviousCommit, string Commit, int Stage,
    string? Filter, int ExitCode, bool HealthVerified = false);
