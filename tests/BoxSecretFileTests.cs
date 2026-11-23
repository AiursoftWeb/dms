using System.Text.Json;
using Aiursoft.DMS.Workflows;
using Aiursoft.DMS.Workflows.Operations;
using Aiursoft.DMS.Contracts;

namespace Aiursoft.DMS.Tests;

[TestClass]
public sealed class BoxSecretFileTests
{
    [TestMethod]
    public async Task AtomicUpdatePreservesOtherSettingsAndReturnsOnlyMetadata()
    {
        if (!OperatingSystem.IsLinux())
        {
            Assert.Inconclusive("Box is a Linux deployment.");
            return;
        }
        using var fixture = new Fixture();
        var original = "# comment\nCLOUDFLARE_API_TOKEN=synthetic-secret-agreed\nPG=other-value\nEMPTY=\n";
        File.WriteAllText(fixture.Env, original);
        var mode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
        File.SetUnixFileMode(fixture.Env, mode);
        var file = new BoxSecretFile(fixture.Directory.FullName, fixture.Transport);
        Assert.AreEqual(3, file.List().Keys.Count);
        Assert.IsFalse(file.Summary("EMPTY").Configured);
        Assert.IsFalse(file.Summary("MISSING").Configured);
        var summary = file.Summary("CLOUDFLARE_API_TOKEN");
        Assert.IsTrue(summary.Configured);
        Assert.IsFalse(JsonSerializer.Serialize(summary).Contains("synthetic-secret", StringComparison.Ordinal));
        var id = Guid.NewGuid();
        fixture.Transport.Value = "new-$value=& with spaces";
        var receipt = await file.SetAsync("CLOUDFLARE_API_TOKEN", id, CancellationToken.None);
        Assert.AreEqual("CLOUDFLARE_API_TOKEN", receipt.Name);
        Assert.AreEqual(mode, File.GetUnixFileMode(fixture.Env));
        Assert.AreEqual(original.Replace("synthetic-secret-agreed", fixture.Transport.Value, StringComparison.Ordinal),
            File.ReadAllText(fixture.Env));
        var revealed = await file.PublishAsync("CLOUDFLARE_API_TOKEN", id, CancellationToken.None);
        Assert.AreEqual(receipt, revealed);
        Assert.AreEqual(fixture.Transport.Value, fixture.Transport.Published);
    }

    [TestMethod]
    public async Task RejectsSymlinksAndDuplicateKeysBeforeConsumingTransfer()
    {
        if (!OperatingSystem.IsLinux())
        {
            Assert.Inconclusive("Box is a Linux deployment.");
            return;
        }
        using var fixture = new Fixture();
        var other = Path.Combine(fixture.Directory.FullName, "other");
        File.WriteAllText(other, "protected");
        File.CreateSymbolicLink(fixture.Env, other);
        var file = new BoxSecretFile(fixture.Directory.FullName, fixture.Transport);
        await Assert.ThrowsAsync<InvalidOperationException>(() => file.SetAsync("PG", Guid.NewGuid(), CancellationToken.None));
        Assert.AreEqual("protected", File.ReadAllText(other));
        Assert.AreEqual(0, fixture.Transport.Consumed);
        File.Delete(fixture.Env);
        File.WriteAllText(fixture.Env, "PG=one\nPG=two\n");
        File.SetUnixFileMode(fixture.Env, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        Assert.Throws<InvalidDataException>(() => file.List());
        Assert.AreEqual(0, fixture.Transport.Consumed);
    }

    [TestMethod]
    public async Task GenericFileReadRefusesPrivateBoxConfiguration()
    {
        var agent = new AgentEnvironment(Guid.Empty, "", 0, 0);
        var context = new MachineWorkflowContext(Guid.NewGuid(), null, _ => Task.CompletedTask);
        var input = new TextFileInput("/swarm-vol/BoxForMoog/.env.prod");
        var failure = await Assert.ThrowsAsync<IOException>(() => FilesystemOperations.CatAsync(context,
            input, agent, CancellationToken.None));
        StringAssert.Contains(failure.Message, "Box secret workflows");
        var elevatedFailure = await Assert.ThrowsAsync<IOException>(() => FilesystemOperations.CatAsRootAsync(context,
            input, agent, CancellationToken.None));
        StringAssert.Contains(elevatedFailure.Message, "Box secret workflows");
    }

    private sealed class Fixture : IDisposable
    {
        public DirectoryInfo Directory { get; } = System.IO.Directory.CreateTempSubdirectory("dms-box-secret-test-");
        public string Env => Path.Combine(Directory.FullName, ".env.prod");
        public Transfer Transport { get; } = new();
        public Fixture() => Directory.CreateSubdirectory(".git");
        public void Dispose() => Directory.Delete(recursive: true);
    }

    private sealed class Transfer : IBoxSecretTransport
    {
        public string Value { get; set; } = "fixture";
        public string? Published { get; private set; }
        public int Consumed { get; private set; }
        public string Fingerprint(string name, string value) => "0123456789abcdef";
        public Task<string> TakeSetValueAsync(Guid workflowId, CancellationToken token)
        {
            Consumed++;
            return Task.FromResult(Value);
        }

        public Task PublishRawValueAsync(Guid workflowId, string value, CancellationToken token)
        {
            Published = value;
            return Task.CompletedTask;
        }
    }
}
