using Microsoft.Extensions.Logging.Abstractions;
using Zapqio.Runner.Protocol;
using Zapqio.Runner.Protocol.Enums;

namespace Zapqio.Runner.Tests;

public class RunnerProcessStateTests
{
    [Fact]
    public void IdentitySurvivesSnapshots_AndChangesForNewHost()
    {
        var process=new RunnerProcessState();var id=process.InstanceId;
        process.Snapshot(new PendingJobReturns());process.Snapshot(new PendingJobReturns());
        Assert.NotEqual(Guid.Empty,id);Assert.Equal(id,process.InstanceId);
        Assert.NotEqual(id,new RunnerProcessState().InstanceId);
    }

    [Fact]
    public void CompletedMethod_RemainsInSnapshotUntilItsResultIsConfirmed()
    {
        var process=new RunnerProcessState();var pending=new PendingJobReturns();var id=Guid.NewGuid();
        process.Received(id);
        var result=new MessageJobReturn { Id=Guid.NewGuid(),AttemptId=id,Status=MessageResponseStatus.OK,Data="{}" };
        pending.MarkSent(result,10);process.Completed(id);
        Assert.Equal(id,Assert.Single(process.Snapshot(pending)));
        pending.Confirm(11);Assert.Empty(process.Snapshot(pending));
    }

    [Fact]
    public async Task SnapshotIncludesQueuedAndRunningAttempts_WithoutRestartingThem()
    {
        var process=new RunnerProcessState();var pending=new PendingJobReturns();
        var started=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var finish=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var polled=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var stop=new CancellationTokenSource();
        var scheduler=new JobScheduler(1,async _=>{started.TrySetResult();await finish.Task;},_=>Task.CompletedTask,
            ()=>{polled.TrySetResult();return Task.CompletedTask;},NullLogger<JobScheduler>.Instance,process);
        MessageJob Job()=>new() { Id=Guid.NewGuid(),AttemptId=Guid.NewGuid(),Name="Wait",Data="{}" };
        var first=Job();var second=Job();var loop=scheduler.RunAsync(stop.Token);
        try {
            scheduler.Enqueue(first);await started.Task.WaitAsync(TimeSpan.FromSeconds(5));scheduler.Enqueue(second);
            Assert.Equal(new[]{first.AttemptId,second.AttemptId}.Order(),process.Snapshot(pending).Order());
            finish.TrySetResult();await polled.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.DoesNotContain(first.AttemptId,process.Snapshot(pending));
        } finally {finish.TrySetResult();stop.Cancel();await loop;}
    }
}
