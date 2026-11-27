using System.Text.Json;
using Aiursoft.DMS.Agent;
using Aiursoft.DMS.Contracts;

namespace Aiursoft.DMS.Tests;

[TestClass]
public class AgentJournalHistoryTests
{
    private static JournalTask WriteCheckpoint(DirectoryInfo directory, int minute)
    {
        const string definition = "test.scheduled-history";
        var at = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc).AddMinutes(minute);
        var task = new JournalTask { Id = Guid.NewGuid(), Definition = definition, Version = "1", State = RunState.Succeeded,
            Schedule = new ScheduledInvocation(definition, "* * * * *", at, WorkflowTrigger.Cron), Sequence = 1 };
        WriteLoose(directory, task);
        return task;
    }

    private static string LooseFile(DirectoryInfo directory, Guid id) => Path.Combine(directory.FullName, $"{id:N}.json");

    private static void WriteLoose(DirectoryInfo directory, JournalTask task) =>
        File.WriteAllBytes(LooseFile(directory, task.Id), JsonSerializer.SerializeToUtf8Bytes(task));

    private static void AssertArchivedOfferCannotRestart(TaskJournal journal, AgentMessage offer)
    {
        journal.Accept(offer);
        Assert.IsNull(journal.Start(offer.TaskId));
        Assert.IsFalse(journal.RejectBeforeStart(offer, "replay"));
        Assert.Throws<InvalidDataException>(() => journal.Accept(new AgentMessage { TaskId = offer.TaskId, Definition = "changed" }));
    }

    [TestMethod]
    public async Task PreviousCheckpointsPackWithoutLosingOffersOccurrencesOrLateReports()
    {
        var directory = Directory.CreateTempSubdirectory();
        try
        {
            var eligible = Enumerable.Range(0, TaskJournal.HistoryBatchSize).Select(i => WriteCheckpoint(directory, i)).ToArray();
            var uncertain = WriteCheckpoint(directory, -3);
            uncertain.State = RunState.NeedsVerification;
            WriteLoose(directory, uncertain);
            var manual = WriteCheckpoint(directory, -2);
            manual.Schedule = null;
            WriteLoose(directory, manual);
            var waiting = WriteCheckpoint(directory, -1);
            waiting.Pending.Enqueue(new WorkflowEvent { WorkflowId = waiting.Id, Sequence = 1, State = RunState.Succeeded });
            WriteLoose(directory, waiting);
            var first = eligible[0];
            var offer = new AgentMessage { TaskId = first.Id, Definition = first.Definition, Version = first.Version, Input = first.Input };
            using (var journal = new TaskJournal(directory.FullName))
            {
                Assert.HasCount(1, directory.GetFiles("*.json.gz"));
                Assert.IsTrue(eligible.All(t => !File.Exists(LooseFile(directory, t.Id))));
                Assert.IsTrue(new[] { manual, uncertain, waiting }.All(t => File.Exists(LooseFile(directory, t.Id))));
                Assert.AreEqual((1, 1), journal.Counts());
                Assert.HasCount(eligible.Length + 3, journal.Snapshot());
                AssertArchivedOfferCannotRestart(journal, offer);
                Assert.IsNull(journal.AcceptScheduled(first.Definition, "1", eligible[^1].Schedule!, reconcileAfterInterruption: true));
                await journal.ReportAsync(first.Id, new WorkflowEvent { WorkflowId = first.Id, IsTrace = true, Message = "after archival" });
            }
            using var recovered = new TaskJournal(directory.FullName);
            var late = recovered.Snapshot(true).Single(t => t.Id == first.Id);
            Assert.AreEqual(2L, late.Pending.Single().Sequence);
            Assert.AreEqual("after archival", late.Pending.Single().Message);
            recovered.Acknowledge(first.Id, 2);
            Assert.AreEqual((1, 1), recovered.Counts());
            Assert.HasCount(eligible.Length + 3, recovered.Snapshot());
        }
        finally { directory.Delete(true); }
    }

    [TestMethod]
    public void InterruptedRemovalIsIdempotentAndCorruptBatchPreservesLooseEvidence()
    {
        var directory = Directory.CreateTempSubdirectory();
        try
        {
            var tasks = Enumerable.Range(0, TaskJournal.HistoryBatchSize).Select(i => WriteCheckpoint(directory, i)).ToArray();
            using (var first = new TaskJournal(directory.FullName)) Assert.IsEmpty(first.Snapshot(true));
            var batch = directory.GetFiles("*.json.gz").Single();
            foreach (var task in tasks.Take(11)) WriteLoose(directory, task);
            File.WriteAllText(Path.Combine(directory.FullName, "scheduled-history.tmp"), "incomplete write");
            using (var recovered = new TaskJournal(directory.FullName))
            {
                CollectionAssert.AreEquivalent(tasks.Select(t => t.Id).ToArray(), recovered.Snapshot().Select(t => t.Id).ToArray());
                Assert.IsTrue(tasks.All(t => !File.Exists(LooseFile(directory, t.Id))));
            }
            tasks[0].Pending.Enqueue(new WorkflowEvent { WorkflowId = tasks[0].Id, Sequence = 1, State = RunState.Succeeded });
            WriteLoose(directory, tasks[0]);
            Assert.Throws<InvalidDataException>(() => { using var journal = new TaskJournal(directory.FullName); });
            Assert.IsTrue(File.Exists(LooseFile(directory, tasks[0].Id)));
            tasks[0].Pending.Clear();
            WriteLoose(directory, tasks[0]);
            using (var stream = batch.Open(FileMode.Append)) stream.WriteByte(0);
            Assert.Throws<InvalidDataException>(() => { using var journal = new TaskJournal(directory.FullName); });
            Assert.IsTrue(File.Exists(LooseFile(directory, tasks[0].Id)));
        }
        finally { directory.Delete(true); }
    }

    [TestMethod]
    public async Task FailedPublicationLeavesEveryOriginalAndRetryArchivesTheNewestAcknowledgement()
    {
        var directory = Directory.CreateTempSubdirectory();
        try
        {
            var tasks = Enumerable.Range(0, TaskJournal.HistoryBatchSize).Select(i => WriteCheckpoint(directory, i)).ToArray();
            var obstruction = directory.CreateSubdirectory("scheduled-history.tmp");
            Assert.Throws<UnauthorizedAccessException>(() => { using var journal = new TaskJournal(directory.FullName); });
            Assert.IsTrue(tasks.All(t => File.Exists(LooseFile(directory, t.Id))));
            obstruction.Delete();
            using (var first = new TaskJournal(directory.FullName)) Assert.HasCount(tasks.Length, first.Snapshot());
            for (var i = 1; i < TaskJournal.HistoryBatchSize; i++) WriteCheckpoint(directory, TaskJournal.HistoryBatchSize + i);
            using (var journal = new TaskJournal(directory.FullName))
            {
                await journal.ReportAsync(tasks[0].Id, new WorkflowEvent { WorkflowId = tasks[0].Id, IsTrace = true });
                journal.Acknowledge(tasks[0].Id, 2);
            }
            using var reopened = new TaskJournal(directory.FullName);
            Assert.HasCount(2, directory.GetFiles("*.json.gz"));
            Assert.IsFalse(File.Exists(LooseFile(directory, tasks[0].Id)));
            Assert.AreEqual(2L, reopened.Find(tasks[0].Id)!.Sequence);
            Assert.HasCount(2 * TaskJournal.HistoryBatchSize - 1, reopened.Snapshot());
            Assert.IsEmpty(reopened.Snapshot(true));
        }
        finally { directory.Delete(true); }
    }

    [TestMethod]
    public async Task NewerBatchesReleaseAnEntireSupersededBatchAndSurviveItsReappearance()
    {
        var directory = Directory.CreateTempSubdirectory();
        try
        {
            var tasks = Enumerable.Range(0, TaskJournal.HistoryBatchSize * 2).Select(i => WriteCheckpoint(directory, i)).ToArray();
            string oldPath;
            byte[] before;
            using (var journal = new TaskJournal(directory.FullName))
            {
                oldPath = directory.GetFiles("*.json.gz").Single().FullName;
                before = File.ReadAllBytes(oldPath);
                var firstBatch = tasks.Where(task => !File.Exists(LooseFile(directory, task.Id))).ToArray();
                var agent = new AgentClient(new Uri("http://127.0.0.1"), "fixture", journal);
                agent.TickSchedules(new DateTime(2026, 1, 1, 12, 1, 0, DateTimeKind.Utc));
                Assert.HasCount(2, directory.GetFiles("*.json.gz"));
                foreach (var task in firstBatch)
                {
                    await journal.ReportAsync(task.Id, new WorkflowEvent { WorkflowId = task.Id, IsTrace = true });
                    journal.Acknowledge(task.Id, 2);
                }
                Assert.IsFalse(File.Exists(oldPath));
                Assert.HasCount(2, directory.GetFiles("*.json.gz"));
            }
            File.WriteAllBytes(oldPath, before); // Directory removal had not reached disk before restart.
            using var recovered = new TaskJournal(directory.FullName);
            var all = recovered.Snapshot();
            Assert.HasCount(tasks.Length, all);
            Assert.HasCount(TaskJournal.HistoryBatchSize, all.Where(t => t.Sequence == 2));
            Assert.IsEmpty(recovered.Snapshot(true));
            Assert.IsFalse(File.Exists(oldPath));
        }
        finally { directory.Delete(true); }
    }
}
