using System.Collections.Concurrent;
using Microsoft.Extensions.Logging.Abstractions;
using Zapqio.Runner.Protocol;

namespace Zapqio.Runner.Tests;

/// <summary>
/// Planista: ile zadań rusza naraz, w jakiej kolejności, kiedy idzie potwierdzenie i odpytanie.
/// Wykonanie jest podstawione bramką per zadanie, więc test decyduje, kiedy które się kończy.
/// </summary>
public class JobSchedulerTests
{
    private static readonly TimeSpan Patience = TimeSpan.FromSeconds(5);

    private sealed class Harness
    {
        private readonly ConcurrentDictionary<Guid, TaskCompletionSource> _gates = new();
        public readonly ConcurrentQueue<Guid> Started = new();
        public readonly ConcurrentQueue<Guid> Accepted = new();
        public int Polls;
        public Func<MessageJob, Task>? OnExecute;
        public JobScheduler Scheduler { get; }
        public CancellationTokenSource Stop { get; } = new();

        public Harness(int capacity)
        {
            Scheduler = new JobScheduler(
                capacity,
                async job =>
                {
                    Started.Enqueue(job.AttemptId);
                    if (OnExecute is { } custom)
                    {
                        await custom(job);
                        return;
                    }
                    await _gates.GetOrAdd(job.AttemptId, _ => new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously)).Task;
                },
                job =>
                {
                    Accepted.Enqueue(job.AttemptId);
                    return Task.CompletedTask;
                },
                () =>
                {
                    Interlocked.Increment(ref Polls);
                    return Task.CompletedTask;
                },
                NullLogger<JobScheduler>.Instance);
            _ = Scheduler.RunAsync(Stop.Token);
        }

        public MessageJob Enqueue()
        {
            var job = new MessageJob { Id = Guid.NewGuid(), AttemptId = Guid.NewGuid(), Name = "Wait", Data = "{}" };
            Assert.True(Scheduler.Enqueue(job));
            return job;
        }

        public void Finish(MessageJob job) =>
            _gates.GetOrAdd(job.AttemptId, _ => new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously)).TrySetResult();
    }

    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        var deadline = DateTime.UtcNow + Patience;
        while (!condition())
        {
            if (DateTime.UtcNow > deadline) throw new TimeoutException("Warunek nie został spełniony na czas.");
            await Task.Delay(10);
        }
    }

    /// <summary>
    /// Potwierdzenie idzie od razu dla KAŻDEGO przydziału, także trzeciego i dalszych, gdy slot jest
    /// zajęty (rozstrzygnięcie B). Wersja z dwoma zadaniami tego nie łapała: pętla potwierdzała
    /// drugie, a dopiero potem stawała na semaforze - trzecie leżało niepotwierdzone do zwolnienia
    /// slotu, po terminie platforma zwracała je do kolejki i wysyłała ponownie, w kółko.
    /// </summary>
    [Fact]
    public async Task CapacityOne_RunsJobsOneAfterAnother_ButAcceptsAllAtOnce()
    {
        var h = new Harness(1);
        using var _ = h.Stop;
        var first = h.Enqueue();
        var second = h.Enqueue();
        var third = h.Enqueue();

        await WaitUntilAsync(() => h.Accepted.Count == 3);
        await Task.Delay(50);
        Assert.Equal(new[] { first.AttemptId }, h.Started.ToArray());
        Assert.Equal(0, h.Scheduler.FreeSlots);
        Assert.Equal(2, h.Scheduler.Queued);

        h.Finish(first);
        await WaitUntilAsync(() => h.Started.Count == 2);
        Assert.Equal(new[] { first.AttemptId, second.AttemptId }, h.Started.ToArray());

        h.Finish(second);
        await WaitUntilAsync(() => h.Started.Count == 3);
        Assert.Equal(third.AttemptId, h.Started.Last());
        Assert.Equal(0, h.Scheduler.Queued);
        h.Stop.Cancel();
    }

    [Fact]
    public async Task CapacityTwo_RunsTwoAtOnce_ThirdWaitsForAFreeSlot()
    {
        var h = new Harness(2);
        using var _ = h.Stop;
        var a = h.Enqueue();
        var b = h.Enqueue();
        var c = h.Enqueue();

        await WaitUntilAsync(() => h.Started.Count == 2);
        await Task.Delay(50);
        Assert.Equal(2, h.Scheduler.Running);
        Assert.DoesNotContain(c.AttemptId, h.Started);

        h.Finish(a);
        await WaitUntilAsync(() => h.Started.Contains(c.AttemptId));
        Assert.Equal(2, h.Scheduler.Running);
        h.Stop.Cancel();
    }

    /// <summary>Rozstrzygnięcie H: jedno odpytanie po każdym zakończonym zadaniu, gdy jest wolny slot.</summary>
    [Fact]
    public async Task PollsOnceAfterEachFinishedJob_WhenASlotIsFree()
    {
        var h = new Harness(2);
        using var _ = h.Stop;
        var a = h.Enqueue();
        var b = h.Enqueue();
        await WaitUntilAsync(() => h.Started.Count == 2);
        Assert.Equal(0, h.Polls);

        h.Finish(a);
        await WaitUntilAsync(() => h.Polls == 1);
        h.Finish(b);
        await WaitUntilAsync(() => h.Polls == 2);
        Assert.Equal(2, h.Scheduler.FreeSlots);
        h.Stop.Cancel();
    }

    [Fact]
    public async Task ExceptionOutsideTheMethod_FreesTheSlot()
    {
        var h = new Harness(1) { OnExecute = _ => throw new InvalidOperationException("boom") };
        using var _ = h.Stop;
        h.Enqueue();

        // Odpytanie idzie na końcu sprzątania po zadaniu, więc to na nie czekamy - slot jest wtedy już wolny.
        await WaitUntilAsync(() => h.Polls == 1);
        Assert.Equal(1, h.Scheduler.FreeSlots);
        Assert.Equal(0, h.Scheduler.Running);
        h.Stop.Cancel();
    }

    /// <summary>Zatrzymanie: to, co trwa, ma się skończyć; to, co czeka, nie rusza.</summary>
    [Fact]
    public async Task CompleteAdding_LeavesQueuedJobsUnstarted_AndWaitsForTheRunningOne()
    {
        var h = new Harness(1);
        using var _ = h.Stop;
        var running = h.Enqueue();
        var queued = h.Enqueue();
        await WaitUntilAsync(() => h.Started.Count == 1);

        h.Scheduler.CompleteAdding();
        Assert.False(h.Scheduler.Enqueue(new MessageJob { Id = Guid.NewGuid(), AttemptId = Guid.NewGuid(), Name = "x", Data = "{}" }));

        var waiting = h.Scheduler.WaitForRunningAsync(Patience, CancellationToken.None);
        await Task.Delay(50);
        Assert.False(waiting.IsCompleted);

        h.Finish(running);
        Assert.True(await waiting);
        await Task.Delay(50);
        Assert.DoesNotContain(queued.AttemptId, h.Started);
        Assert.Equal(0, h.Scheduler.Running);
    }

    [Fact]
    public async Task WaitForRunning_ReturnsFalse_WhenAJobOutlivesTheTimeout()
    {
        var h = new Harness(1);
        using var _ = h.Stop;
        h.Enqueue();
        await WaitUntilAsync(() => h.Scheduler.Running == 1);

        Assert.False(await h.Scheduler.WaitForRunningAsync(TimeSpan.FromMilliseconds(50), CancellationToken.None));
        h.Stop.Cancel();
    }

    [Fact]
    public async Task WaitForRunning_IsImmediate_WhenNothingRuns()
    {
        var h = new Harness(1);
        using var _ = h.Stop;

        Assert.True(await h.Scheduler.WaitForRunningAsync(Patience, CancellationToken.None));
        h.Stop.Cancel();
    }
}
