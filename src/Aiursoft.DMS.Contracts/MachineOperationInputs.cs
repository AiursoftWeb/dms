namespace Aiursoft.DMS.Contracts;

public sealed record DirectoryUsageInput(string Path, int Depth = 1)
{
    public void Validate()
    {
        ValidatePath(Path);
        if (Depth is < 0 or > 3) throw new ArgumentException("Directory depth must be between 0 and 3.");
    }

    internal static void ValidatePath(string path)
    {
        if (string.IsNullOrWhiteSpace(path) || path.Length > 4096 || path[0] != '/'
            || path.Any(char.IsControl) || path != System.IO.Path.GetFullPath(path))
            throw new ArgumentException("A normalized absolute Linux path without control characters is required.");
    }
}

// Paths refer to existing files on the machine. Key contents never pass through the Controller.
public sealed record BoxGitInput(string IdentityFile, string KnownHostsFile)
{
    public void Validate()
    {
        DirectoryUsageInput.ValidatePath(IdentityFile);
        DirectoryUsageInput.ValidatePath(KnownHostsFile);
        if (new[] { IdentityFile, KnownHostsFile }.Any(path => path.Any(c => !char.IsAsciiLetterOrDigit(c) && !"/._-".Contains(c))))
            throw new ArgumentException("Box Git file paths may contain only ASCII letters, digits, slash, dot, underscore and hyphen.");
        if (IdentityFile == KnownHostsFile) throw new ArgumentException("Identity and known-hosts files must be different.");
    }
}
