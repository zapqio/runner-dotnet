using Zapqio.Protocol;

namespace Zapqio.Runner.Background
{
    public class SendLogsBackground : BackgroundService
    {
        private readonly ILogger<SendLogsBackground> _logger;
        private readonly WSClient _client;
        private readonly LogQueue _queue;

        public SendLogsBackground(ILogger<SendLogsBackground> logger, WSClient client, LogQueue queue)
        {
            _logger = logger;
            _client = client;
            _queue = queue;
        }
        protected override async Task ExecuteAsync(CancellationToken stoppingToken)
        {
            try
            {
                await Task.Delay(2000, stoppingToken);

                while (!stoppingToken.IsCancellationRequested)
                {
                    try
                    {
                        if (!_client.Connected())
                        {
                            _logger.LogDebug("WebSocket not connected, attempting to reconnect");

                            await Task.Delay(5000, stoppingToken);
                        }
                        else
                        {
                            while (_queue.TryDequeue(out var m))
                            {
                                if (!await _client.SendLogs(m))
                                {
                                    _queue.AddLog(m); //dodanie z powrotem do kolejki jeśli nie wysłano
                                    continue;
                                }
                            }
                        }

                        await Task.Delay(2000, stoppingToken);
                    }
                    catch (TaskCanceledException)
                    {
                        _logger.LogDebug("Task was cancelled, service is stopping");
                        break;
                    }
                    catch (OperationCanceledException)
                    {
                        _logger.LogDebug("Operation was cancelled, service is stopping");
                        break;
                    }
                    catch (Exception ex)
                    {
                        _logger.LogError(ex, "Error occurred during job checking loop");
                        await Task.Delay(5000, stoppingToken);
                    }
                }
            }
            finally
            {
                _logger.LogInformation("Send logs service has stopped");
            }
        }
    }
}
