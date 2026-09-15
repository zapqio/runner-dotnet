using System.Collections.Concurrent;

namespace Zapqio.Runner;

/// <summary>Żyje przez cały czas działania hosta, niezależnie od liczby połączeń WebSocket.</summary>
public sealed class RunnerProcessState
{
    public Guid InstanceId { get; } = Guid.NewGuid();
    private readonly ConcurrentDictionary<Guid, byte> _active = new();
    public void Received(Guid attemptId) => _active.TryAdd(attemptId, 0);
    public void Completed(Guid attemptId) => _active.TryRemove(attemptId, out _);

    public List<Guid> Snapshot(PendingJobReturns pending)
    {
        // Wykonanie najpierw odkłada wynik do pending, dopiero potem znika z active.
        // Ta kolejność odczytu nie zgubi próby przechodzącej między tymi zbiorami.
        var active = _active.Keys.ToArray();
        return active.Concat(pending.PeekAll().Select(r => r.AttemptId)).Distinct().ToList();
    }
}
