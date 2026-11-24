using Aiursoft.DMS.Contracts;
using Aiursoft.DMS.Workflows;
using Aiursoft.DMS.Workflows.Operations;

namespace Aiursoft.DMS.Tests;

[TestClass]
public class FrpsStatusTests
{
    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task ReportsOnlyTheFrpsProcessListenersAndSummarizesTheJournal(bool wrapped)
    {
        if (!OperatingSystem.IsLinux()) throw new PlatformNotSupportedException();
        using var machine = new Fixture(wrapped);
        var result = await machine.InspectAsync();
        var service = result.Services.Single();

        Assert.AreEqual(wrapped ? "turn.service" : "frps.service", service.Unit);
        Assert.AreEqual("active", service.ActiveState);
        Assert.AreEqual(203, service.MainPid);
        Assert.AreEqual(2, service.AutomaticRestarts);
        Assert.AreEqual(new DateTimeOffset(2026, 11, 23, 13, 8, 52, TimeSpan.Zero), service.StartedAtUtc);
        Assert.AreEqual("0.72.0", service.InstalledVersion);
        Assert.AreEqual(service.InstalledVersion, service.RunningVersion);
        Assert.AreEqual(true, service.ConfigurationPresent);
        Assert.AreEqual(wrapped ? true : (bool?)null, service.InMemoryConfiguration);
        CollectionAssert.AreEqual(new[] { new FrpsListener("tcp", "*:443"), new FrpsListener("udp", "*:7000") },
            service.Listeners!.ToArray());
        Assert.AreEqual(3, service.RecentJournal!.Entries);
        Assert.AreEqual(1, service.RecentJournal.Errors);
        Assert.AreEqual(1, service.RecentJournal.Warnings);
        Assert.AreEqual(1, service.RecentJournal.WorkPoolFull);
        Assert.AreEqual(1, service.RecentJournal.ProxyStarts);
        Assert.IsEmpty(service.Diagnostics);
        Assert.DoesNotContain("never-return-this-token", System.Text.Json.JsonSerializer.Serialize(result));
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task RejectsUntrustedPathsAndDoesNotReportTheNewVersionForAReplacedProcess(bool writable)
    {
        if (!OperatingSystem.IsLinux()) throw new PlatformNotSupportedException();
        using var machine = new Fixture(false) { WritableDirectory = writable, Replaced = true };
        var service = (await machine.InspectAsync()).Services.Single();
        Assert.AreEqual(writable ? null : "0.72.0", service.InstalledVersion);
        Assert.IsNull(service.RunningVersion);
        Assert.AreEqual(!writable, machine.VersionCalled);
        Assert.Contains("The running executable could not be matched to the installed version.", service.Diagnostics);
    }

    [TestMethod]
    public async Task ReportsAStoppedServiceWithoutTreatingItsListenersAsAnEmptySample()
    {
        if (!OperatingSystem.IsLinux()) throw new PlatformNotSupportedException();
        using var machine = new Fixture(false) { Stopped = true };
        var service = (await machine.InspectAsync()).Services.Single();
        Assert.AreEqual("inactive", service.ActiveState);
        Assert.AreEqual("0.72.0", service.InstalledVersion);
        Assert.IsNull(service.RunningVersion);
        Assert.IsNull(service.Listeners);
        Assert.IsFalse(machine.SocketQueryCalled);
    }

    [TestMethod]
    public async Task NoConfiguredServiceProducesAnEmptyInventory()
    {
        if (!OperatingSystem.IsLinux()) throw new PlatformNotSupportedException();
        using var machine = new Fixture(false) { Missing = true };
        Assert.IsEmpty((await machine.InspectAsync()).Services);
        Assert.AreEqual(RiskLevel.Low, MachineWorkflows.FrpsStatus.Risk);
        Assert.IsTrue(new MachineWorkflowRegistry().Contains(MachineWorkflows.FrpsStatus.Name));
    }

    private sealed class Fixture : IDisposable
    {
        private readonly DirectoryInfo _root = Directory.CreateTempSubdirectory("dms-frps-status-");
        private readonly string _service;
        private readonly string _command;
        private readonly string _binary;
        private readonly string _config;
        public bool WritableDirectory { get; init; }
        public bool Replaced { get; init; }
        public bool Stopped { get; init; }
        public bool Missing { get; init; }
        public bool VersionCalled { get; private set; }
        public bool SocketQueryCalled { get; private set; }

        public Fixture(bool wrapped)
        {
            _service = wrapped ? "turn.service" : "frps.service";
            _command = wrapped ? "/usr/bin/turn" : "/usr/local/bin/frps";
            _binary = wrapped ? "/usr/lib/turn/turn" : _command;
            _config = wrapped ? "/etc/turn/turn.toml" : "/etc/frp/frps.toml";
            var config = Path.Combine(_root.FullName, _config.TrimStart('/'));
            Directory.CreateDirectory(Path.GetDirectoryName(config)!);
            File.WriteAllText(config, "never-return-this-token");
        }

        public Task<FrpsStatusResult> InspectAsync()
        {
            var agent = new AgentEnvironment(Guid.Empty, "1.0", 0, 0,
                Operations: new OperationHost(_root.FullName, (command, _) => Task.FromResult(Run(command))));
            var input = new EmptyInput();
            return new FrpsStatusWorkflow(input, agent).ExecuteAsync(
                new MachineWorkflowContext(Guid.NewGuid(), null, _ => Task.CompletedTask, agent), input, CancellationToken.None);
        }

        private NativeResult Run(NativeCommand command)
        {
            string output;
            switch (command.Executable)
            {
                case "systemctl":
                    output = Missing || command.Arguments[^1] != _service ? "LoadState=not-found\n" :
                        $"LoadState=loaded\nActiveState={(Stopped ? "inactive" : "active")}\nSubState=running\nUnitFileState=enabled\n" +
                        $"MainPID={(Stopped ? 0 : 203)}\nExecMainStartTimestamp=Mon 2026-11-23 13:08:52 UTC\nNRestarts=2\n" +
                        $"ExecStart={{ path={_command} ; argv[]={_command} -c {_config} ; ignore_errors=no }}\n";
                    break;
                case "realpath":
                    output = _binary + "\n";
                    break;
                case "stat":
                    output = "0:700:regular file\n" + string.Concat(Enumerable.Repeat(
                        WritableDirectory ? "0:777:directory\n" : "0:755:directory\n", command.Arguments.Count - 4));
                    break;
                case "readlink":
                    output = command.Arguments[0].EndsWith("/fd/3", StringComparison.Ordinal)
                        ? "/memfd:turn.toml (deleted)\n" : _binary + (Replaced ? " (deleted)" : "") + "\n";
                    break;
                case "ss":
                    SocketQueryCalled = true;
                    output = """
                        tcp LISTEN 0 4096 *:443 *:* users:(("frps",pid=203,fd=9))
                        udp UNCONN 0 0 *:7000 *:* users:(("frps",pid=203,fd=10))
                        tcp LISTEN 0 4096 *:22 *:* users:(("sshd",pid=2030,fd=4))
                        """;
                    break;
                case "journalctl":
                    output = """
                        {"PRIORITY":"6","MESSAGE":"new proxy [https] type [tcp] success"}
                        {"PRIORITY":"6","MESSAGE":"2026-11-23 13:09:00.000 [E] work connection pool is full; never-return-this-token"}
                        {"PRIORITY":"4","MESSAGE":"failed to get work connection"}
                        """;
                    break;
                default:
                    Assert.AreEqual(_binary, command.Executable);
                    CollectionAssert.AreEqual(new[] { "--version" }, command.Arguments.ToArray());
                    VersionCalled = true;
                    output = "0.72.0\n";
                    break;
            }
            return new NativeResult(0, output, "");
        }

        public void Dispose() => _root.Delete(true);
    }
}
