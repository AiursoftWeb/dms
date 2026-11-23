using System.Text;
using System.Text.RegularExpressions;

namespace Aiursoft.DMS.Contracts;

public sealed partial record BoxSecretInput(string Name)
{
    public void Validate()
    {
        if (string.IsNullOrEmpty(Name) || !KeyName().IsMatch(Name))
            throw new ArgumentException("Specify a valid .env.prod key name.");
    }

    [GeneratedRegex(@"\A[A-Za-z_][A-Za-z0-9_]{0,127}\z")]
    private static partial Regex KeyName();
}

public sealed record BoxSecretEntry(string Name, bool Configured);
public sealed record BoxSecretsResult(IReadOnlyList<BoxSecretEntry> Keys);
public sealed record BoxSecretSummary(string Name, bool Configured, string? Fingerprint);
public sealed record BoxSecretResult(string Name);
public sealed record BoxSecretSubmission(Guid MachineId, string Reason, BoxSecretInput Key, string Value)
{
    public override string ToString() => nameof(BoxSecretSubmission);
}
public sealed record BoxSecretValue(string Value)
{
    public override string ToString() => nameof(BoxSecretValue);

    public void Validate()
    {
        if (string.IsNullOrEmpty(Value) || Value.IndexOfAny(['\r', '\n', '\0']) >= 0 ||
            Encoding.UTF8.GetByteCount(Value) > 4096)
            throw new ArgumentException("Enter one nonempty secret value of at most 4096 UTF-8 bytes.");
    }
}
