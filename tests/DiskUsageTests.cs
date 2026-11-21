using Aiursoft.DMS.Contracts;
using System.Text.Json;
using Aiursoft.DMS.Workflows;

namespace Aiursoft.DMS.Tests;

[TestClass]
public class DiskUsageTests
{
    [TestMethod]
    public void FilesystemTotalsUseByteCountsAndDoNotInventReservedSpace()
    {
        var id = Guid.NewGuid();
        var result = MachineDiskUsageWorkflow.Parse("Taille Utilisé Disponible\r\n 100 20 70\r\n 9007199254740991 1 9007199254740980\n", id);
        Assert.AreEqual(id, result.WorkflowId); Assert.AreEqual(2, result.Filesystems);
        Assert.AreEqual(9007199254741091L, result.TotalBytes);
        Assert.AreEqual(21L, result.UsedBytes); Assert.AreEqual(9007199254741050L, result.AvailableBytes);
        Assert.AreNotEqual(result.TotalBytes - result.UsedBytes, result.AvailableBytes);
    }

    [TestMethod]
    public void NegativeAvailableSpaceIsPreservedRatherThanClamped()
    {
        var result = MachineDiskUsageWorkflow.Parse("Size Used Avail\n100 100 -10\n", Guid.NewGuid());
        Assert.AreEqual(-10L, result.AvailableBytes);
    }

    [TestMethod]
    [DataRow("")]
    [DataRow("Size Used Avail\n")]
    [DataRow("Size Used Avail\n100 bad 50\n")]
    [DataRow("Size Used Avail\n100 20 -\n")]
    [DataRow("Size Used Avail\n100 20 70 extra\n")]
    [DataRow("Size Used Avail\n-100 20 70\n")]
    [DataRow("Size Used Avail\n100 -20 70\n")]
    public void MissingOrInvalidFilesystemValuesDoNotBecomeSuccessfulZeroTotals(string output) =>
        Assert.Throws<InvalidDataException>(() => MachineDiskUsageWorkflow.Parse(output, Guid.NewGuid()));

    [TestMethod]
    public void EqualSizedDistinctFilesystemsAreNotDeduplicatedByTheirCounters()
    {
        var result = MachineDiskUsageWorkflow.Parse("Size Used Avail\n100 20 70\n100 20 70\n", Guid.NewGuid());
        Assert.AreEqual(200L, result.TotalBytes); Assert.AreEqual(2, result.Filesystems);
    }

    [TestMethod]
    public void FilesystemSumCannotSilentlyOverflow() =>
        Assert.Throws<OverflowException>(() => MachineDiskUsageWorkflow.Parse($"Size Used Avail\n{long.MaxValue} 0 0\n1 0 0\n", Guid.NewGuid()));

    [TestMethod]
    public void HistoricalControllerResultCanStillBeReadBySdkConsumers()
    {
        const string historicalResult = """
            {
              "TotalBytes": 100,
              "UsedBytes": 20,
              "AvailableBytes": 70,
              "MachineCount": 1,
              "Machines": [{
                "MachineId": "ca264013-6534-4d0f-9acb-e6a84492ab15",
                "Usage": { "WorkflowId": "2fd0fbc8-75b4-4fb1-a6d3-0686b88430df", "TotalBytes": 100,
                           "UsedBytes": 20, "AvailableBytes": 70, "Filesystems": 1 }
              }]
            }
            """;
        var result = JsonSerializer.Deserialize<ClusterDiskUsageResult>(historicalResult)!;
        Assert.AreEqual(100L, result.TotalBytes);
        Assert.AreEqual(20L, result.UsedBytes);
        Assert.AreEqual(70L, result.AvailableBytes);
        Assert.AreEqual(1, result.MachineCount);
        Assert.AreEqual(Guid.Parse("ca264013-6534-4d0f-9acb-e6a84492ab15"), result.Machines.Single().MachineId);
        Assert.AreEqual(1, result.Machines.Single().Usage.Filesystems);
    }

}
