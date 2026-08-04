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

        /// <summary>Zwłoka po pierwszej nieudanej próbie; kolejne podwajają ją aż do <see cref="MaxReconnectDelay"/>.</summary>
        private static readonly TimeSpan BaseReconnectDelay = TimeSpan.FromSeconds(3);

        /// <summary>Sufit wycofywania się - wyżej runner przestałby zauważać, że Web wrócił.</summary>
        private static readonly TimeSpan MaxReconnectDelay = TimeSpan.FromSeconds(60);

        private int _failedConnects;

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
                    var connect = await _client.Connect();
                    if (!connect.Connected)
                    {
                        await DelayBeforeReconnectAsync(connect, stoppingToken);
                        continue;
                    }
                    _failedConnects = 0;
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
        /// <summary>
        /// Odczekuje przed kolejną próbą uzgodnienia. Stała zwłoka wystarczała, dopóki jedynym
        /// powodem odmowy było niedostępne Web. Odkąd serwer ogranicza tempo uzgodnień (§3
        /// protokołu), ponawianie w niezmienionym rytmie utrzymuje limit w stanie zadziałania -
        /// runner odnawia go własnymi próbami zamiast pozwolić mu wygasnąć.
        /// </summary>
        private async Task DelayBeforeReconnectAsync(WSClient.ConnectResult result, CancellationToken stoppingToken)
        {
            _failedConnects++;

            TimeSpan delay;
            if (result.RetryAfter is { } retryAfter)
            {
                // Serwer podał termin, więc go dotrzymujemy. Losowa sekunda ponad to jest po to, żeby
                // runnery odprawione tą samą wartością nie wróciły co do chwili razem.
                delay = retryAfter + TimeSpan.FromMilliseconds(Random.Shared.Next(1000));
            }
            else
            {
                var backoff = Math.Min(
                    BaseReconnectDelay.TotalMilliseconds * Math.Pow(2, _failedConnects - 1),
                    MaxReconnectDelay.TotalMilliseconds);

                // Rozrzut, a nie czysty backoff: limit jest liczony na adres, więc runnery zza jednego
                // NAT-u wracałyby zgraną falą i przekraczały go razem, rundę po rundzie.
                delay = TimeSpan.FromMilliseconds(backoff * (0.5 + Random.Shared.NextDouble() * 0.5));
            }

            _logger.LogInformation(
                "Kolejna próba połączenia za {Delay:0.#}s (nieudanych z rzędu: {Failed})",
                delay.TotalSeconds, _failedConnects);

            await Task.Delay(delay, stoppingToken).ContinueWith(x => { });
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
