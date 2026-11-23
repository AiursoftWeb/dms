using System.Text;
using Aiursoft.DMS.Contracts;
using Aiursoft.DMS.Workflows;
using Aiursoft.DMS.Workflows.Operations;

namespace Aiursoft.DMS.Tests;

[TestClass]
public class FrpsDeployTests
{
    private const string ExistingConfig = "bindPort = 8432\nkcpBindPort = 8432\n[auth]\nmethod = 'token'\ntoken = 'original-secret' # preserve\n";

    [TestMethod]
    public async Task CliExposesDeployAndTheOptionalToken()
    {
        var result = await Client.DmsCommandApp.Create().TestRunAsync(["machine", "frps", "deploy", "--help"]);
        Assert.AreEqual(0, result.ProgramReturn);
        StringAssert.Contains(result.StdOut, "--frps-token");
        Assert.IsTrue(MachineWorkflows.FrpsDeploy.SingleMachineOnly);
        Assert.IsTrue(new MachineWorkflowRegistry().Contains(MachineWorkflows.FrpsDeploy.Name));
    }

    [TestMethod]
    public async Task EmptyTokenOnFreshMachineFailsBeforeDownloadOrWrites()
    {
        if (!OperatingSystem.IsLinux())
        {
            Assert.Inconclusive("FRPS deployment requires Linux.");
            return;
        }
        using var machine = new Fixture();
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => machine.DeployAsync(null));
        StringAssert.Contains(error.Message, "--frps-token");
        Assert.AreEqual(0, machine.Downloads);
        Assert.IsFalse(File.Exists(machine.Config));
        Assert.IsFalse(File.Exists(machine.Binary));
    }

    [TestMethod]
    public async Task FreshInstallBuildsPrivateConfigAndEnablesService()
    {
        if (!OperatingSystem.IsLinux())
        {
            Assert.Inconclusive("FRPS deployment requires Linux.");
            return;
        }
        using var machine = new Fixture();
        var result = await machine.DeployAsync("test-private-token");
        Assert.IsTrue(result.Installed);
        Assert.IsTrue(result.TokenChanged);
        Assert.AreEqual("0.72.0", result.Version);
        StringAssert.Contains(File.ReadAllText(machine.Config), "auth.token = \"test-private-token\"");
        Assert.AreEqual(UnixFileMode.UserRead | UnixFileMode.UserWrite, File.GetUnixFileMode(machine.Config));
        Assert.IsTrue(machine.Commands.Contains("systemctl enable --now frps.service"));
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task UpgradeKeepsExistingUnitAndOnlyChangesTokenWhenSupplied(bool replaceToken)
    {
        if (!OperatingSystem.IsLinux())
        {
            Assert.Inconclusive("FRPS deployment requires Linux.");
            return;
        }
        using var machine = new Fixture(ExistingConfig);
        var result = await machine.DeployAsync(replaceToken ? "rotated-token" : null);
        Assert.AreEqual("0.66.0", result.PreviousVersion);
        Assert.IsFalse(result.Installed);
        Assert.AreEqual(replaceToken, result.TokenChanged);
        Assert.AreEqual(replaceToken ? ExistingConfig.Replace("'original-secret'", "\"rotated-token\"") : ExistingConfig,
            File.ReadAllText(machine.Config));
        Assert.AreEqual("0.72.0", File.ReadAllText(machine.Binary));
        CollectionAssert.AreEqual(new[] { "systemctl restart frps.service" },
            machine.Commands.Where(line => line.Contains("restart", StringComparison.Ordinal)).ToArray());
        Assert.IsFalse(File.Exists(machine.NewUnit));
    }

    [TestMethod]
    public async Task FailedRestartRestoresPreviousBinaryAndConfig()
    {
        if (!OperatingSystem.IsLinux())
        {
            Assert.Inconclusive("FRPS deployment requires Linux.");
            return;
        }
        using var machine = new Fixture(ExistingConfig) { FailRestart = true };
        await Assert.ThrowsAsync<InvalidOperationException>(() => machine.DeployAsync("rotated-token"));
        Assert.AreEqual("0.66.0", File.ReadAllText(machine.Binary));
        Assert.AreEqual(ExistingConfig, File.ReadAllText(machine.Config));
        Assert.HasCount(2, machine.Commands.Where(line => line.Contains("restart", StringComparison.Ordinal)));
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task UpgradeRecognizedEncryptedWrapperInPlace(bool rotate)
    {
        if (!OperatingSystem.IsLinux())
        {
            Assert.Inconclusive("FRPS deployment requires Linux.");
            return;
        }

        using var machine = new Fixture(ExistingConfig, wrapped: true);
        var result = await machine.DeployAsync(rotate ? "rotated-token" : null);
        Assert.IsFalse(result.Installed);
        Assert.AreEqual(rotate, result.TokenChanged);
        Assert.AreEqual("0.72.0", File.ReadAllText(machine.Binary));
        Assert.AreEqual(rotate ? "replacement-ciphertext" : "original-ciphertext", File.ReadAllText(machine.Config));
        Assert.IsTrue(machine.Commands.Contains("systemctl restart turn.service"));
        Assert.IsFalse(File.Exists(machine.NewUnit));
        Assert.IsFalse(machine.Commands.Any(c => c.Contains("rotated-token", StringComparison.Ordinal)));
    }

    private sealed class Fixture : IDisposable
    {
        private readonly string _root = Directory.CreateTempSubdirectory("dms-frps-test-").FullName;
        public string Binary => _root + (_wrapped ? "/usr/lib/turn/turn" : "/usr/local/bin/frps");
        public string Config => _root + (_wrapped ? "/etc/turn/turn.toml" : "/etc/frp/frps.toml");
        public string NewUnit => _root + "/etc/systemd/system/frps.service";
        private readonly bool _installed;
        private readonly bool _wrapped;
        public int Downloads { get; private set; }
        public bool FailRestart { get; init; }
        public List<string> Commands { get; } = [];

        public Fixture(string? configuration = null, bool wrapped = false)
        {
            Directory.CreateDirectory(_root + "/run");
            _installed = configuration != null;
            _wrapped = wrapped;
            if (configuration == null) return;
            Directory.CreateDirectory(Path.GetDirectoryName(Binary)!);
            Directory.CreateDirectory(Path.GetDirectoryName(Config)!);
            File.WriteAllText(Binary, "0.66.0");
            File.WriteAllText(Config, wrapped ? "original-ciphertext" : configuration);
            if (!wrapped) return;
            Directory.CreateDirectory(_root + "/usr/bin");
            File.WriteAllText(_root + "/usr/bin/turn", "fixture-wrapper");
            Directory.CreateDirectory(_root + "/proc/100/fd");
            File.WriteAllText(_root + "/proc/100/fd/3", configuration);
        }

        public Task<FrpsDeployResult> DeployAsync(string? token)
        {
            var input = new FrpsDeployInput(ProtectedToken: token == null ? null : "encrypted");
            var agent = new AgentEnvironment(Guid.Empty, "", 0, 0, Operations: new OperationHost(_root, RunAsync),
                GetFrpsDeployToken: (_, _) => Task.FromResult(token!));
            var workflow = new FrpsDeployWorkflow(input, agent, _ =>
            {
                Downloads++;
                return Task.FromResult(Encoding.UTF8.GetBytes("0.72.0"));
            });
            return workflow.ExecuteAsync(new MachineWorkflowContext(Guid.NewGuid(), null, _ => Task.CompletedTask), input,
                CancellationToken.None);
        }

        private Task<NativeResult> RunAsync(NativeCommand command, CancellationToken _)
        {
            Commands.Add(command.Executable + " " + string.Join(" ", command.Arguments));
            if (command.Executable == "readlink")
                return Task.FromResult(new NativeResult(0, command.Arguments[0].EndsWith("/exe", StringComparison.Ordinal)
                    ? "/usr/lib/turn/turn" : "/memfd:turn.toml (deleted)", ""));
            if (command.Arguments[0] == "--encrypt")
            {
                StringAssert.Contains(File.ReadAllText(command.Arguments[1]), "\"rotated-token\"");
                File.WriteAllText(command.Arguments[2], "replacement-ciphertext");
            }
            var output = command.Arguments[0] switch
            {
                "show" when command.Arguments[^1] == "turn.service" && _wrapped =>
                    "ExecStart={ path=/usr/bin/turn ; argv[]=/usr/bin/turn -c /etc/turn/turn.toml ; ignore_errors=no }\nLoadState=loaded\nMainPID=100\n",
                "show" when !_installed || _wrapped => "LoadState=not-found\n",
                "show" => "ExecStart={ path=/usr/local/bin/frps ; argv[]=/usr/local/bin/frps -c /etc/frp/frps.toml ; ignore_errors=no }\nLoadState=loaded\n",
                "--version" => File.ReadAllText(command.Executable),
                _ => ""
            };
            var fail = FailRestart && command.Arguments[0] == "restart" && File.ReadAllText(Binary) == "0.72.0";
            return Task.FromResult(new NativeResult(fail ? 1 : 0, output, ""));
        }

        public void Dispose() => Directory.Delete(_root, true);
    }
}
