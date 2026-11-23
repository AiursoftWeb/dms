namespace Aiursoft.DMS.Contracts;

public sealed record FrpsDeployInput(string? FrpsToken = null, string? ProtectedToken = null)
{
    public const string ProtectionPurpose = "DMS/FRPS deploy token/v1";

    public void Validate()
    {
        if (FrpsToken is { Length: > 1024 } ||
            FrpsToken?.Any(char.IsControl) == true)
            throw new ArgumentException("An FRPS token may contain at most 1024 characters and no control characters.");
        if (ProtectedToken is { Length: 0 or > 8192 } || FrpsToken != null && ProtectedToken != null)
            throw new ArgumentException("Invalid FRPS token input.");
    }
}

public sealed record FrpsDeployToken(string Token);

public sealed record FrpsDeployResult(string? PreviousVersion, string Version, bool Installed, bool TokenChanged);
