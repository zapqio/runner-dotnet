using Zapqio.Runner.Core;
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
        private readonly PendingJobReturn _pending;

        public ExecuteJob(
            WSClient client,
            LogQueue logQueue,
            ScopedConsole scopedConsole,
            MethodsProvider methodsProvider,
            PendingJobReturn pending)
        {
            _client = client;
            _logQueue = logQueue;
            _scopedConsole = scopedConsole;
            _methodsProvider = methodsProvider;
            _pending = pending;
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

                    // Kontekst zadania dla modułu (JobContext.Current): id operacji, stałe między
                    // wysyłkami, po którym metoda z nieidempotentnym skutkiem rozpoznaje powtórkę.
                    // AsyncLocal przepływa do Task.Run, więc metoda widzi go także w swoich wątkach;
                    // po zakończeniu znika, żeby kolejne zadanie nie odziedziczyło cudzego kontekstu.
                    using (JobContext.Begin(new JobContext(message.Id, message.AttemptId, message.Name)))
                    {
                        outData = await Task.Run(() =>
                        {
                            using var consoleScope = _scopedConsole.BeginScope(message);
                            return method.Run(message.Data);
                        });
                    }
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

            var result = new MessageJobReturn
            {
                Id = message.Id,
                AttemptId = message.AttemptId,
                Status = status ? MessageResponseStatus.OK : MessageResponseStatus.ERROR,
                Data = status ? outData : null
            };

            var returnSent = await _client.SendJobReturn(result.Id, result.AttemptId, result.Status, result.Data);
            if (returnSent)
            {
                // Poszło, ale bez potwierdzenia: zostaje do ponowienia, gdyby połączenie okazało się
                // martwe, zanim platforma da znak życia (patrz PendingJobReturn).
                _pending.MarkSent(result);
                return;
            }

            // Wynik nie przepadł: host wyśle go po ponownym połączeniu z tym samym attemptId, a
            // platforma przyjmie, jeśli wciąż trzyma zadanie jako „wynik nieznany" (§5.5 protokołu).
            _pending.MarkFailed(result);
            _logQueue.AddLog(new MessageLog
            {
                Date = DateTimeOffset.Now,
                Level = MessageLogLevel.Error,
                JobId = message.Id,
                AttemptId = message.AttemptId,
                Message = $"Failed to send JobReturn ({result.Status}) - the result is kept and will be resent after reconnecting"
            });
        }
    }
}
