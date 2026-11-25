using System.Text.Json;

namespace Aiursoft.DMS.Client;

internal sealed record DmsCredentials(string Endpoint, string ApiKey)
{
    internal static string FilePath => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".DMS", "auth.json");

    internal static string NormalizeEndpoint(string endpoint)
    {
        if (!Uri.TryCreate(endpoint.Trim(), UriKind.Absolute, out var uri) || uri.Scheme is not ("http" or "https") ||
            !string.IsNullOrEmpty(uri.UserInfo) || !string.IsNullOrEmpty(uri.Query) || !string.IsNullOrEmpty(uri.Fragment))
            throw new ArgumentException($"Invalid DMS Controller URL. {DmsEndpointOptions.Name} expects an HTTP or HTTPS URL without credentials, query or fragment. Use --machine-id to select a machine in a machine command.");
        return uri.AbsoluteUri.TrimEnd('/');
    }

    private static DmsCredentials? Load()
    {
        try
        {
            if (!File.Exists(FilePath)) return null;
            var saved = JsonSerializer.Deserialize<DmsCredentials>(File.ReadAllText(FilePath));
            if (saved == null || string.IsNullOrWhiteSpace(saved.Endpoint) || string.IsNullOrWhiteSpace(saved.ApiKey))
                throw new JsonException();
            return saved with { Endpoint = NormalizeEndpoint(saved.Endpoint) };
        }
        catch (Exception exception) when (exception is JsonException or IOException or UnauthorizedAccessException or ArgumentException)
        {
            throw new ArgumentException($"Cannot read saved DMS authentication. Run 'dms login' to replace it, or supply both {DmsEndpointOptions.Name} and --api-key.");
        }
    }

    internal static DmsCredentials Resolve(string? endpoint, string? key)
    {
        endpoint ??= Environment.GetEnvironmentVariable("DMS_SERVER");
        key ??= Environment.GetEnvironmentVariable("DMS_API_KEY");
        var saved = endpoint == null || key == null ? Load() : null;
        endpoint = NormalizeEndpoint(endpoint ?? saved?.Endpoint ?? throw new ArgumentException($"Run 'dms login', or set {DmsEndpointOptions.Name} or DMS_SERVER."));
        // A stored key belongs to the endpoint verified at login, never a different override.
        key ??= saved?.Endpoint == endpoint ? saved.ApiKey : null;
        if (string.IsNullOrWhiteSpace(key)) throw new ArgumentException("Run 'dms login', or set --api-key or DMS_API_KEY for this Controller.");
        return new DmsCredentials(endpoint, key);
    }

    internal async Task SaveAsync()
    {
        var directory = Path.GetDirectoryName(FilePath)!;
        if (OperatingSystem.IsWindows()) Directory.CreateDirectory(directory);
        else Directory.CreateDirectory(directory, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        var temporary = Path.Combine(directory, Guid.NewGuid().ToString("N") + ".tmp");
        try
        {
            var options = new FileStreamOptions { Mode = FileMode.CreateNew, Access = FileAccess.Write, Share = FileShare.None };
            if (!OperatingSystem.IsWindows()) options.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
            await using (var stream = new FileStream(temporary, options))
                await JsonSerializer.SerializeAsync(stream, this, new JsonSerializerOptions { WriteIndented = true });
            File.Move(temporary, FilePath, overwrite: true);
        }
        finally
        {
            if (File.Exists(temporary)) File.Delete(temporary);
        }
    }
}
