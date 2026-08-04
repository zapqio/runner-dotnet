using Zapqio.Runner.Protocol;
using Zapqio.Runner.Protocol.Enums;

namespace Zapqio.Runner
{
    public class ExecuteJob
    {
        private readonly WSClient _client;
        private readonly LogQueue _logQueue;
        private readonly ScopedConsole _scopedConsole;
        private readonly MethodsProvider _methodsProvider;

        public ExecuteJob(WSClient client, LogQueue logQueue, ScopedConsole scopedConsole, MethodsProvider methodsProvider)
        {
            _client = client;
            _logQueue = logQueue;
            _scopedConsole = scopedConsole;
            _methodsProvider = methodsProvider;
        }

        public async Task Exec(MessageJob message)
        {
            var method = _methodsProvider.GetMethod(message.Name);
            bool status = true;
            string outData = null;
            if (method == null)
            {
                _logQueue.AddLog(new MessageLog
                {
                    Date = DateTimeOffset.Now,
                    Level = MessageLogLevel.Error,
                    JobId = message.Id,
                    AttemptId = message.AttemptId,
                    Message = $"Not found method: {message.Name}"
                });
                status = false;
            }
            else
            {
                try
                {
                    var logStatus = await _client.SendLogs(new MessageLog
                    {
                        Date = DateTimeOffset.Now,
                        Level = MessageLogLevel.Info,
                        JobId = message.Id,
                        AttemptId = message.AttemptId,
                        Message = $"Run Job: {DateTimeOffset.Now:s}"
                    });
                    if (!logStatus)
                    {
                        return;
                    }
                    outData = await Task.Run(() =>
                    {

                        using var consoleScope = _scopedConsole.BeginScope(message);
                        return method.Run(message.Data);
                    });
                }
                catch (Exception ex)
                {
                    _logQueue.AddLog(new MessageLog
                    {
                        Date = DateTimeOffset.Now,
                        Level = MessageLogLevel.Error,
                        JobId = message.Id,
                        AttemptId = message.AttemptId,
                        Message = $"Main exception: {ex}"
                    });
                    status = false;
                }
            }
            var responseStatus = status ? MessageResponseStatus.OK : MessageResponseStatus.ERROR;
            var returnSent = await _client.SendJobReturn(
                message.Id, message.AttemptId, responseStatus, status ? outData : null);
            if (!returnSent)
            {
                //wynik przepadł - platforma sama zamknie osierocone zadanie, zostaje tylko ślad w logach zadania
                _logQueue.AddLog(new MessageLog
                {
                    Date = DateTimeOffset.Now,
                    Level = MessageLogLevel.Error,
                    JobId = message.Id,
                    AttemptId = message.AttemptId,
                    Message = $"Failed to send JobReturn ({responseStatus}) - the result of this job was lost"
                });
            }

        }
    }
}
