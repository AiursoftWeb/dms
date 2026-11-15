using System.Text.Json;

namespace Aiursoft.DMS.Agent;

public sealed record AgentConfiguration(string Controller, string ClusterName, Guid EnrollmentId, Guid InstallationId,
    string MachineName, string ReceiptKey, string AgentKey, Guid? MachineId = null)
{
    private static string FilePath(string state) => Path.Combine(state, "connection.json");
    public static AgentConfiguration? Load(string state) => File.Exists(FilePath(state))
        ? JsonSerializer.Deserialize<AgentConfiguration>(File.ReadAllText(FilePath(state))) ?? throw new InvalidDataException("Invalid Agent configuration.") : null;
    public void Save(string state)
    {
        Directory.CreateDirectory(state);
        if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(state, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        var path = FilePath(state);
        using (var stream = new FileStream(path + ".tmp", FileMode.Create, FileAccess.Write, FileShare.None))
        {
            if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(path + ".tmp", UnixFileMode.UserRead | UnixFileMode.UserWrite);
            JsonSerializer.Serialize(stream, this); stream.Flush(true);
        }
        File.Move(path + ".tmp", path, true);
        if (MachineId != null) File.WriteAllText(Path.Combine(state, "joined"), MachineId.Value.ToString());
    }
    public static string ControllerUrl(string endpoint)
    {
        if (!Uri.TryCreate(endpoint, UriKind.Absolute, out var uri) || uri.Scheme is not ("http" or "https") || !string.IsNullOrEmpty(uri.UserInfo)
            || uri.AbsolutePath != "/" || !string.IsNullOrEmpty(uri.Query) || !string.IsNullOrEmpty(uri.Fragment)) throw new ArgumentException("Use an HTTP(S) Controller origin without credentials, a path, query or fragment.");
        return uri.GetLeftPart(UriPartial.Authority);
    }
}
