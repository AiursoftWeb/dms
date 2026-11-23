using Aiursoft.DMS.Workflows.Operations;

namespace Aiursoft.DMS.Agent;

internal static class AgentAptTransaction
{
    public const string VerifyArgument = "--verify-apt-transaction";

    public static AptTransactionHook Hook { get; } = CreateHook();

    private static AptTransactionHook CreateHook()
    {
        // APT identifies the protocol by the first word of the hook command.
        // The proc path stays absolute and contains neither spaces nor shell metacharacters.
        var executable = $"/proc/{Environment.ProcessId}/exe";
        var assembly = typeof(AgentAptTransaction).Assembly.Location;
        var argument = Path.GetFileNameWithoutExtension(Environment.ProcessPath) == "dotnet"
            ? " '" + assembly.Replace("'", "'\"'\"'", StringComparison.Ordinal) + "'"
            : "";
        return new AptTransactionHook(executable, $"{executable}{argument} {VerifyArgument}");
    }

    public static int Verify() => AptTransactionGuard.Verify(Console.In, Console.Error,
        Environment.GetEnvironmentVariable(AptTransactionGuard.ExpectedEnvironment) ?? "",
        Environment.GetEnvironmentVariable(AptTransactionGuard.ArchitectureEnvironment) ?? "");
}
