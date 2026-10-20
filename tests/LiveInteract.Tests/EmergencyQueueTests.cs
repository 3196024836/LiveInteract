using LiveInteract.Contracts;
using LiveInteract.Core;
using Xunit;
namespace LiveInteract.Tests;

/// <summary>明确停止应取消未发出的单位，晚到回执只能结算在途单位，不能把取消的礼物重新排队。</summary>
public sealed class EmergencyQueueTests
{
    private static InteractionJournal Create() => new(":memory:");
    private static InteractionJob Queue(InteractionJournal journal)
    {
        var input = new LiveEvent { Id = "stop-gift", Type = LiveEventType.Gift, Count = 3 };
        journal.AdmitEvents([input], true);
        journal.CommitDecision(input, [new RuleMatch(new() { Root = new() { ActionId = "health.add" } }, 3)], "");
        return journal.AcquireNext()!;
    }
    private static ActionRequest Intent(InteractionJob job) => new() { Id = "in-flight", JobId = job.Id, UnitIndex = 0, ActionId = "health.add" };

    [Fact]
    public void PreparedButCancelledUnitCannotBeSent()
    {
        using var journal = Create(); var job = Queue(journal); journal.CancelPending("主播停止");
        Assert.Throws<OperationCanceledException>(() => journal.RecordIntent(Intent(job)));
        journal.CompleteUnit(job.Id, new() { ErrorCode = "UNKNOWN" });
        job = Assert.Single(journal.QueryJobs()); Assert.Equal(3, job.Cancelled); Assert.Equal(0, job.Unknown); Assert.Null(journal.AcquireNext());
    }
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void InFlightReceiptDoesNotReviveCancelledRemainder(bool success)
    {
        using var journal = Create(); var job = Queue(journal); journal.RecordIntent(Intent(job)); journal.CancelPending("主播停止");
        journal.CompleteUnit(job.Id, new() { Success = success, Retryable = !success, ErrorCode = success ? "" : "NOT_APPLIED" });
        job = Assert.Single(journal.QueryJobs()); Assert.Equal(success ? 1 : 0, job.Succeeded); Assert.Equal(success ? 2 : 3, job.Cancelled); Assert.Equal(0, job.Remaining); Assert.Equal("Cancelled", job.State); Assert.Null(journal.AcquireNext());
    }
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void UnknownReceiptMayBeReconciledButNeverResumesStoppedRemainder(bool success)
    {
        using var journal = Create(); var job = Queue(journal); journal.RecordIntent(Intent(job));
        journal.CompleteUnit(job.Id, new() { ErrorCode = "UNKNOWN" }); journal.CancelPending("主播停止");
        journal.ResolveUnknown(job.Id, new() { Success = success, Retryable = !success, ErrorCode = success ? "" : "NOT_APPLIED" });
        job = Assert.Single(journal.QueryJobs()); Assert.Equal(success ? 1 : 0, job.Succeeded); Assert.Equal(success ? 2 : 3, job.Cancelled); Assert.Equal(0, job.Remaining); Assert.Null(journal.AcquireNext());
    }
    [Fact]
    public void StaleDecoderSnapshotCannotEnqueueAfterStop()
    {
        using var journal = Create(); journal.AppendFrame([1], "room", true); var frame = journal.ReadFrame()!;
        journal.CancelPending("主播停止");
        journal.CommitFrame(frame, [new() { Id = "late-frame", Type = LiveEventType.Gift, Count = 3 }], "");
        journal.ProcessNext(new RuleEngine(reliable: true), new() { Rules = [new() { Enabled = true, Root = new() { ActionId = "health.add" }, Trigger = new() { Type = LiveEventType.Gift } }] });
        Assert.Empty(journal.QueryJobs()); Assert.Equal(1, journal.ReceivedCount);
    }
}
