using System.Text.RegularExpressions;

namespace Aiursoft.DMS.Contracts;

public sealed partial record DockerPsInput(bool All = false, string? GrepName = null)
{
    public void Validate()
    {
        if (GrepName != null && !NameFragment().IsMatch(GrepName))
            throw new ArgumentException("The name filter must contain 1 to 128 letters, digits, dots, underscores or hyphens.");
    }

    [GeneratedRegex(@"\A[a-zA-Z0-9_.-]{1,128}\z")]
    private static partial Regex NameFragment();
}

public sealed partial record DockerContainerInput(string Container)
{
    public void Validate()
    {
        ArgumentNullException.ThrowIfNull(Container);
        if (!ContainerId().IsMatch(Container))
            throw new ArgumentException("Use the full 64-character container ID returned by docker ps.");
    }

    [GeneratedRegex(@"\A[0-9a-f]{64}\z")]
    private static partial Regex ContainerId();
}

public sealed record DockerLogsInput(string Container, int Tail = 100, DateTimeOffset? Since = null)
{
    public void Validate()
    {
        new DockerContainerInput(Container).Validate();
        if (Tail is < 1 or > 1000) throw new ArgumentException("Tail must be between 1 and 1000 lines.");
    }
}

public sealed record DockerExecInput(string Container, string[] Command)
{
    public void Validate()
    {
        new DockerContainerInput(Container).Validate();
        ArgumentNullException.ThrowIfNull(Command);
        foreach (var argument in Command) ArgumentNullException.ThrowIfNull(argument);
        if (Command.Length is < 1 or > 256 || string.IsNullOrWhiteSpace(Command[0]) ||
            Command[0].StartsWith('-') || Command.Any(argument => argument.Contains('\0')) ||
            Command.Sum(argument => (long)argument.Length) > 16384)
            throw new ArgumentException("Supply an executable and up to 255 arguments (maximum 16384 characters, no NUL characters).");
    }
}
