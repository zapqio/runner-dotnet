using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using Zapqio.Runner.Protocol;
using Zapqio.Runner.Protocol.Enums;

namespace Zapqio.Runner.Background
{
    public class RequestBindBackground : BackgroundService
    {
        private readonly WSClient _client;
        private readonly ILogger<RequestBindBackground> _logger;
        private readonly IServiceProvider _serviceProvider;
        private bool _runMethodFirstConnected = false;
        private volatile bool _executingJob = false;

        public RequestBindBackground(WSClient client, ILogger<RequestBindBackground> logger, IServiceProvider serviceProvider)
        {
            _client = client;
            _logger = logger;
            _serviceProvider = serviceProvider;
        }
        protected override async Task ExecuteAsync(CancellationToken stoppingToken)
        {
            while (!stoppingToken.IsCancellationRequested)
            {
                try
                {
                    await _client.Connect();
                    if (!_client.Connected())
                    {
                        await Task.Delay(3000, stoppingToken).ContinueWith(x => { });
                        continue;
                    }
                    await FirstConnectedAsync();
                    WebSocketReceiveResult result;
                    using var ms = new MemoryStream();
                    var buff = new byte[1024];
                    do
                    {
                        result = await _client.ReceiveAsync(buff, stoppingToken);
                        ms.Write(buff, 0, result.Count);
                    } while (!result.EndOfMessage);
                    ms.Position = 0;
                    var json = Encoding.UTF8.GetString(ms.ToArray());
                    var message = JsonSerializer.Deserialize<Message>(json, JsonDefaults.Options);

                    if (message != null)
                    {
                        await Handle(message);
                    }

                }
                catch (Exception e)
                {
                    _logger.LogError(e, "Main loop");
                }
            }
        }
        private async Task FirstConnectedAsync()
        {
            if (_runMethodFirstConnected)
            {
                return;
            }
            _runMethodFirstConnected = await _client.SendInfo();
        }
        private async Task Handle(Message message)
        {
            switch (message.Type)
            {
                case MessageType.Job:
                    await HandleJob(message);
                    break;
                default:
                    break;
            }
        }
        private async Task HandleJob(Message message)
        {
            if (_executingJob)
            {
                return;
            }
            try
            {
                _executingJob = true;
                using var scope = _serviceProvider.CreateScope();
                var exec = scope.ServiceProvider.GetService<ExecuteJob>();
                var m = JsonSerializer.Deserialize<MessageJob>(message.Data, JsonDefaults.Options);
                await exec.Exec(m);
            }
            finally
            {
                await Task.Delay(1000);
                _executingJob = false;
                await _client.SendQueryOnJob();
            }
        }
    }
}
