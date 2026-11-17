using System.Text.Json;
using Aiursoft.DMS.Contracts;

namespace Aiursoft.DMS.Workflows;

public static class BoxGitConfiguration
{
    public const string FilePath = "/etc/aiursoft-dms-agent/box-git.json";

    public static BoxGitInput? Read(string path = FilePath)
    {
        if (!File.Exists(path)) return null;
        ValidateFile(path, secret: true);
        var value = MachineWorkflows.BoxConfigureGit.ReadInput(File.ReadAllText(path));
        ValidateFiles(value);
        return value;
    }

    public static void ValidateFiles(BoxGitInput value)
    {
        value.Validate();
        ValidateFile(value.IdentityFile, secret: true);
        ValidateFile(value.KnownHostsFile, secret: false);
    }

    private static void ValidateFile(string path, bool secret)
    {
        if (!OperatingSystem.IsLinux()) throw new PlatformNotSupportedException("Box Git configuration requires Linux.");
        if (!File.Exists(path) || (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
            throw new InvalidOperationException("Box Git files must be existing regular files, not symbolic links.");
        var mode = File.GetUnixFileMode(path);
        var forbidden = UnixFileMode.GroupWrite | UnixFileMode.OtherWrite;
        if (secret) forbidden |= UnixFileMode.GroupRead | UnixFileMode.OtherRead;
        if ((mode & forbidden) != 0) throw new InvalidOperationException("Box Git identity/config files must be private; known-hosts files must not be writable by group or others.");
    }

    public static string SshCommand(BoxGitInput value)
    {
        ValidateFiles(value);
        // Git interprets core.sshCommand through a shell. Quote each path as one literal argument.
        static string Quote(string path) => "'" + path.Replace("'", "'\"'\"'", StringComparison.Ordinal) + "'";
        return "/usr/bin/ssh -F /dev/null -o BatchMode=yes -o IdentitiesOnly=yes -o IdentityAgent=none"
            + " -o StrictHostKeyChecking=yes -o GlobalKnownHostsFile=/dev/null -o UserKnownHostsFile=" + Quote(value.KnownHostsFile)
            + " -i " + Quote(value.IdentityFile);
    }
}

public sealed class BoxConfigureGitWorkflow(BoxGitInput input, string configurationPath = BoxGitConfiguration.FilePath)
    : MachineWorkflow<BoxGitInput, BoxInspectionResult>(MachineWorkflows.BoxConfigureGit, input)
{
    public override async Task<BoxInspectionResult> ExecuteAsync(MachineWorkflowContext context, BoxGitInput value, CancellationToken token)
    {
        if (!OperatingSystem.IsLinux()) throw new PlatformNotSupportedException("Box Git configuration requires Linux.");
        BoxGitConfiguration.ValidateFiles(value);
        token.ThrowIfCancellationRequested();
        var directory = Path.GetDirectoryName(configurationPath)!;
        Directory.CreateDirectory(directory, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        if ((File.GetAttributes(directory) & FileAttributes.ReparsePoint) != 0
            || (File.GetUnixFileMode(directory) & (UnixFileMode.GroupWrite | UnixFileMode.OtherWrite)) != 0)
            throw new InvalidOperationException("The machine Git configuration directory must not be linked or writable by group or others.");
        var next = configurationPath + "." + context.Id.ToString("N") + ".next";
        try
        {
            await using (var file = new FileStream(next, new FileStreamOptions { Mode = FileMode.CreateNew, Access = FileAccess.Write,
                UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite }))
                await JsonSerializer.SerializeAsync(file, value, cancellationToken: token);
            token.ThrowIfCancellationRequested();
            File.Move(next, configurationPath, true);
        }
        finally { File.Delete(next); }
        await context.TraceAsync($"Machine Box Git identity configured: {value.IdentityFile}; known hosts: {value.KnownHostsFile}. Existing key files are referenced, not copied. SSH uses strict host verification; missing or mismatched trust will fail deployment.");
        return new BoxInspectionResult();
    }
}
