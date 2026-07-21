using System.Collections.Concurrent;
using Zapqio.Runner.Protocol;

namespace Zapqio.Runner
{
    public class LogQueue
    {
        private readonly ConcurrentQueue<MessageLog> _queue = new();

        public void AddLog(MessageLog log) => _queue.Enqueue(log);

        public void AddLogs(IEnumerable<MessageLog> logs)
        {
            foreach (var log in logs)
                _queue.Enqueue(log);
        }

        public bool TryDequeue(out MessageLog log) => _queue.TryDequeue(out log);
    }
}
