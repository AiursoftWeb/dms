using System.Diagnostics;
using System.Text.Json;
using Aiursoft.DMS.Entities;
using Aiursoft.DMS.Services.Authentication;
using Microsoft.AspNetCore.Identity;

namespace Aiursoft.DMS.Tests.IntegrationTests;

[TestClass]
public class CliLoginTests : TestBase
{
    private readonly string _home = Directory.CreateTempSubdirectory("DmsLogin-").FullName;
    private string AuthPath => Path.Combine(_home, ".DMS", "auth.json");

    private async Task<string> CreateKeyAsync()
    {
        using var scope = Server!.Services.CreateScope();
        var name = "login-" + Guid.NewGuid().ToString("N");
        var user = new User { UserName = name, Email = name + "@example.test", DisplayName = "CLI login" };
        var created = await scope.ServiceProvider.GetRequiredService<UserManager<User>>().CreateAsync(user);
        Assert.IsTrue(created.Succeeded, string.Join("; ", created.Errors.Select(e => e.Description)));
        var key = DmsKeyAuthenticationHandler.CreateKey(false);
        var db = scope.ServiceProvider.GetRequiredService<TemplateDbContext>();
        db.PersonalApiKeys.Add(new PersonalApiKey { UserId = user.Id, Name = "Login", KeyHash = DmsKeyAuthenticationHandler.Hash(key), ExpiresAt = DateTime.UtcNow.AddHours(1) });
        await db.SaveChangesAsync();
        return key;
    }

    private async Task<(int Code, string Output, string Error)> RunAsync(string[] arguments, string input = "", Dictionary<string, string>? environment = null)
    {
        var start = new ProcessStartInfo("dotnet") { RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true };
        var root = new DirectoryInfo(AppContext.BaseDirectory);
        while (root != null && !File.Exists(Path.Combine(root.FullName, "Aiursoft.DMS.sln"))) root = root.Parent;
        var configuration = new DirectoryInfo(AppContext.BaseDirectory).Parent!.Name;
        start.ArgumentList.Add(Path.Combine(root!.FullName, "src", "Aiursoft.DMS.Client", "bin", configuration, "net10.0", "dms.dll"));
        foreach (var argument in arguments) start.ArgumentList.Add(argument);
        start.Environment["HOME"] = _home;
        start.Environment["USERPROFILE"] = _home;
        start.Environment["DOTNET_CLI_HOME"] = _home;
        start.Environment.Remove("DMS_SERVER"); start.Environment.Remove("DMS_API_KEY");
        if (environment != null) foreach (var entry in environment) start.Environment[entry.Key] = entry.Value;
        using var process = Process.Start(start)!;
        var output = process.StandardOutput.ReadToEndAsync(); var error = process.StandardError.ReadToEndAsync();
        await process.StandardInput.WriteAsync(input); process.StandardInput.Close();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        try { await process.WaitForExitAsync(timeout.Token); }
        catch (OperationCanceledException) { process.Kill(entireProcessTree: true); throw; }
        return (process.ExitCode, await output, await error);
    }

    private async Task LoginAsync(string key)
    {
        var result = await RunAsync(["login"], $"{Http.BaseAddress}\n{key}\n");
        Assert.AreEqual(0, result.Code, result.Error);
        StringAssert.Contains(result.Output, $"{Http.BaseAddress}DmsAccess/Keys");
        Assert.IsFalse((result.Output + result.Error).Contains(key, StringComparison.Ordinal));
    }

    [TestMethod]
    public async Task LoginPersistsVerifiedKeyAndNewProcessUsesIt()
    {
        var key = await CreateKeyAsync();
        await LoginAsync(key);
        using var saved = JsonDocument.Parse(await File.ReadAllTextAsync(AuthPath));
        Assert.AreEqual(key, saved.RootElement.GetProperty("ApiKey").GetString());
        if (!OperatingSystem.IsWindows())
            Assert.AreEqual(UnixFileMode.UserRead | UnixFileMode.UserWrite, File.GetUnixFileMode(AuthPath));
        var whoami = await RunAsync(["access", "whoami"]);
        Assert.AreEqual(0, whoami.Code, whoami.Error);
        StringAssert.Contains(whoami.Output, "login-");
        Assert.IsFalse((whoami.Output + whoami.Error).Contains(key, StringComparison.Ordinal));
    }

    [TestMethod]
    public async Task FailedAndAbortedLoginPreservePreviousAuthentication()
    {
        await LoginAsync(await CreateKeyAsync());
        var previous = await File.ReadAllTextAsync(AuthPath);
        var rejected = await RunAsync(["login", "--dms-server-endpoint", Http.BaseAddress!.ToString()], "invalid-test-key\n");
        Assert.AreEqual(1, rejected.Code);
        Assert.IsFalse((rejected.Output + rejected.Error).Contains("invalid-test-key", StringComparison.Ordinal));
        Assert.AreEqual(previous, await File.ReadAllTextAsync(AuthPath));
        var aborted = await RunAsync(["login"]);
        Assert.AreEqual(1, aborted.Code);
        Assert.AreEqual(previous, await File.ReadAllTextAsync(AuthPath));
        var whoami = await RunAsync(["access", "whoami"]);
        Assert.AreEqual(0, whoami.Code, whoami.Error);
    }

    [TestMethod]
    public async Task FlagsOverrideEnvironmentWhichOverridesSavedAuthentication()
    {
        var key = await CreateKeyAsync(); await LoginAsync(key);
        var environment = new Dictionary<string, string> { ["DMS_SERVER"] = Http.BaseAddress!.ToString(), ["DMS_API_KEY"] = "invalid-test-key" };
        var rejected = await RunAsync(["access", "whoami"], environment: environment);
        Assert.AreEqual(1, rejected.Code);
        var explicitKey = await RunAsync(["access", "whoami", "--api-key", key], environment: environment);
        Assert.AreEqual(0, explicitKey.Code, explicitKey.Error);
        environment["DMS_SERVER"] = "http://127.0.0.1:1"; environment["DMS_API_KEY"] = key;
        var explicitEndpoint = await RunAsync(["access", "whoami", "--dms-server-endpoint", Http.BaseAddress.ToString()], environment: environment);
        Assert.AreEqual(0, explicitEndpoint.Code, explicitEndpoint.Error);
    }

    [TestMethod]
    public async Task EndpointOverrideCannotReuseKeySavedForAnotherController()
    {
        await LoginAsync(await CreateKeyAsync());
        var result = await RunAsync(["access", "whoami", "--dms-server-endpoint", "http://127.0.0.1:1"]);
        Assert.AreEqual(1, result.Code);
        StringAssert.Contains(result.Error, "for this Controller");
    }

    [TestMethod]
    public async Task EndpointHelpUsesNewNameAndLegacyScriptsStillConnect()
    {
        foreach (var command in new[] { new[] { "login" }, new[] { "artifact", "ls" } })
        {
            var help = await RunAsync([.. command, "--help"]);
            Assert.AreEqual(0, help.Code, help.Error);
            StringAssert.Contains(help.Output, "--dms-server-endpoint");
            Assert.DoesNotContain("--server", help.Output);
        }

        var invalid = await RunAsync(["artifact", "ls", "--dms-server-endpoint", Guid.NewGuid().ToString()]);
        Assert.AreEqual(1, invalid.Code);
        StringAssert.Contains(invalid.Error, "--dms-server-endpoint");
        StringAssert.Contains(invalid.Error, "--machine-id");

        var key = await CreateKeyAsync();
        var endpoint = Http.BaseAddress!.ToString();
        var login = await RunAsync(["login", "--server", endpoint], $"{key}\n");
        Assert.AreEqual(0, login.Code, login.Error);
        var legacy = await RunAsync(["access", "whoami", "--server", endpoint]);
        Assert.AreEqual(0, legacy.Code, legacy.Error);
        var explicitEndpoint = await RunAsync(["access", "whoami", "--dms-server-endpoint", endpoint, "--server", "http://127.0.0.1:1"]);
        Assert.AreEqual(0, explicitEndpoint.Code, explicitEndpoint.Error);
    }

    [TestMethod]
    public async Task CorruptAuthenticationCanBeBypassedOrReplacedByLogin()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(AuthPath)!);
        await File.WriteAllTextAsync(AuthPath, "{invalid-test-secret}");
        var rejected = await RunAsync(["access", "whoami"]);
        Assert.AreEqual(1, rejected.Code);
        Assert.IsFalse(rejected.Error.Contains("invalid-test-secret", StringComparison.Ordinal));
        var key = await CreateKeyAsync();
        var explicitCredentials = await RunAsync(["access", "whoami", "--dms-server-endpoint", Http.BaseAddress!.ToString(), "--api-key", key]);
        Assert.AreEqual(0, explicitCredentials.Code, explicitCredentials.Error);
        await LoginAsync(key);
        Assert.AreEqual(0, (await RunAsync(["access", "whoami"])).Code);
    }

    [TestCleanup]
    public override async Task CleanServer()
    {
        try { await base.CleanServer(); }
        finally { Directory.Delete(_home, recursive: true); }
    }
}
