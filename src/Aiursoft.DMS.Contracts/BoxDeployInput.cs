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

public sealed partial record BoxDeployStackInput(string Stack, bool Pull = true)
{
    public void Validate()
    {
        if (string.IsNullOrEmpty(Stack) || !StackName().IsMatch(Stack))
            throw new ArgumentException("Specify one exact stage-4 stack name (letters, digits, '.', '_', '-').");
    }

    [GeneratedRegex(@"\A[a-zA-Z0-9][a-zA-Z0-9_.-]{0,127}\z")]
    private static partial Regex StackName();
}

public sealed record BoxDeployStackResult(string Repository, string PreviousCommit, string Commit,
    string Stack, string ComposeFile, int ExitCode, bool HealthVerified = false);
