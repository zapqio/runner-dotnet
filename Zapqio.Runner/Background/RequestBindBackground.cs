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

        /// <summary>Ustawiane w <see cref="StopAsync"/>, zanim host anuluje pętlę - żeby po zamknięciu gniazda nie próbowała się łączyć na nowo.</summary>
        private volatile bool _stopping = false;

        /// <summary>Zwłoka po pierwszej nieudanej próbie; kolejne podwajają ją aż do <see cref="MaxReconnectDelay"/>.</summary>
        private static readonly TimeSpan BaseReconnectDelay = TimeSpan.FromSeconds(3);

        /// <summary>Sufit wycofywania się - wyżej runner przestałby zauważać, że Web wrócił.</summary>
        private static readonly TimeSpan MaxReconnectDelay = TimeSpan.FromSeconds(60);

        private int _failedConnects;

        /// <summary>Zwłoka po pierwszym błędzie pętli; kolejne z rzędu podwajają ją do <see cref="MaxLoopErrorDelay"/>.</summary>
        private static readonly TimeSpan BaseLoopErrorDelay = TimeSpan.FromSeconds(1);

        private static readonly TimeSpan MaxLoopErrorDelay = TimeSpan.FromSeconds(30);

        private int _loopErrors;

        private readonly PendingJobReturn _pending;

        public RequestBindBackground(
            WSClient client,
            ILogger<RequestBindBackground> logger,
            IServiceProvider serviceProvider,
            PendingJobReturn pending)
        {
            _client = client;
            _logger = logger;
            _serviceProvider = serviceProvider;
            _pending = pending;
        }
        /// <summary>
        /// Uzgodnienie zamknięcia idzie przed anulowaniem pętli. Anulowanie trwającego ReceiveAsync
        /// zrywa gniazdo bez ramki Close (stan Aborted), przez co DisposeAsync klienta nie ma już
        /// czego zamykać, a platforma dowiaduje się o odejściu runnera dopiero, gdy wykryje martwe
        /// TCP - za proxy potrafi to trwać minuty. Tutaj gniazdo jest jeszcze otwarte, a odpowiedź
        /// serwera odbierze trwający odczyt pętli.
        /// </summary>
        public override async Task StopAsync(CancellationToken cancellationToken)
        {
            _stopping = true;
            await _client.CloseAsync(cancellationToken);
            await base.StopAsync(cancellationToken);
        }

        protected override async Task ExecuteAsync(CancellationToken stoppingToken)
        {
            while (!stoppingToken.IsCancellationRequested && !_stopping)
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
                    // Tylko po faktycznym powrocie: Connect() w zwykłym obrocie pętli zastaje gniazdo
                    // otwarte i nic nie nawiązuje, a wynik wysłany chwilę temu nie jest do ponowienia.
                    if (connect.Established)
                    {
                        await ResendPendingResultAsync();
                    }
                    WebSocketReceiveResult result;
                    using var ms = new MemoryStream();
                    var buff = new byte[1024];
                    do
                    {
                        result = await _client.ReceiveAsync(buff, stoppingToken);
                        ms.Write(buff, 0, result.Count);
                    } while (!result.EndOfMessage);

                    if (result.MessageType == WebSocketMessageType.Close)
                    {
                        // Ramka Close - albo odpowiedź na nasze zamknięcie (wtedy CloseAsync klienta
                        // już nie ma nic do zrobienia), albo serwer zamyka pierwszy: dopowiadamy
                        // uzgodnienie, a kolejny obrót pętli łączy się na nowo.
                        if (_stopping)
                            _logger.LogDebug("Platforma potwierdziła zamknięcie ({Status})", result.CloseStatus);
                        else
                            _logger.LogInformation(
                                "Platforma zamknęła połączenie ({Status}: {Description})",
                                result.CloseStatus, result.CloseStatusDescription);
                        await _client.CloseAsync(stoppingToken);
                        continue;
                    }

                    ms.Position = 0;
                    var json = Encoding.UTF8.GetString(ms.ToArray());
                    var message = JsonSerializer.Deserialize<Message>(json, JsonDefaults.Options);

                    // Cokolwiek przyszło od platformy dowodzi, że połączenie żyło po ostatniej
                    // wysyłce wyniku - nie ma czego ponawiać (patrz PendingJobReturn).
                    _pending.Confirm();

                    if (message != null)
                    {
                        await Handle(message);
                    }

                    _loopErrors = 0;
                }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
                {
                    return;
                }
                catch (Exception e) when (_stopping)
                {
                    // Gniazdo zamknięte przez StopAsync w trakcie odczytu - to nie jest błąd pętli.
                    _logger.LogDebug(e, "Main loop interrupted by shutdown");
                    return;
                }
                catch (Exception e)
                {
                    _logger.LogError(e, "Main loop");
                    await DelayAfterLoopErrorAsync(stoppingToken);
                }
            }
        }
        /// <summary>
        /// Zwłoka po błędzie pętli. Bez niej błąd powtarzający się natychmiast - gniazdo formalnie
        /// otwarte, ale każdy odczyt rzuca - kręci pętlą na pełnym CPU i zalewa log. Narastanie
        /// ogranicza to drugie, gdy przyczyna nie mija.
        /// </summary>
        private async Task DelayAfterLoopErrorAsync(CancellationToken stoppingToken)
        {
            _loopErrors++;

            var delay = Math.Min(
                BaseLoopErrorDelay.TotalMilliseconds * Math.Pow(2, _loopErrors - 1),
                MaxLoopErrorDelay.TotalMilliseconds);

            await Task.Delay(TimeSpan.FromMilliseconds(delay), stoppingToken).ContinueWith(x => { });
        }

        /// <summary>
        /// Odczekuje przed kolejną próbą uzgodnienia. Stała zwłoka wystarczała, dopóki jedynym
        /// powodem odmowy było niedostępne Web; odkąd serwer ogranicza tempo (§3 protokołu),
        /// ponawianie w tym samym rytmie odnawia limit własnymi próbami, zamiast dać mu wygasnąć.
        /// </summary>
        private async Task DelayBeforeReconnectAsync(WSClient.ConnectResult result, CancellationToken stoppingToken)
        {
            _failedConnects++;

            TimeSpan delay;
            if (result.RetryAfter is { } retryAfter)
            {
                // Losowa sekunda ponad termin, żeby runnery z tym samym Retry-After nie wróciły razem.
                delay = retryAfter + TimeSpan.FromMilliseconds(Random.Shared.Next(1000));
            }
            else
            {
                var backoff = Math.Min(
                    BaseReconnectDelay.TotalMilliseconds * Math.Pow(2, _failedConnects - 1),
                    MaxReconnectDelay.TotalMilliseconds);

                // Rozrzut, bo limit jest liczony na adres: bez niego runnery zza jednego NAT-u
                // wracałyby zgraną falą i przekraczały go razem, rundę po rundzie.
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

        /// <summary>
        /// Wynik, który nie doszedł (albo nie wiadomo, czy doszedł), idzie zaraz po nawiązaniu
        /// połączenia, przed odczytem czegokolwiek - z tym samym <c>attemptId</c>, żeby platforma
        /// rozpoznała próbę (§5.5 protokołu). Po udanej wysyłce runner zgłasza gotowość, bo w tej
        /// chwili nic nie wykonuje. Nieudana wysyłka zostawia wynik na następny obrót pętli.
        /// </summary>
        private async Task ResendPendingResultAsync()
        {
            var pending = _pending.Peek();
            if (pending is null)
            {
                return;
            }

            _logger.LogInformation(
                "Ponawiam JobReturn ({Status}) zadania {JobId}, próba {AttemptId}, po ponownym połączeniu",
                pending.Status, pending.Id, pending.AttemptId);

            if (!await _client.SendJobReturn(pending.Id, pending.AttemptId, pending.Status, pending.Data))
            {
                _logger.LogWarning("Ponowienie JobReturn zadania {JobId} nie powiodło się - zostaje na kolejne połączenie", pending.Id);
                return;
            }

            _pending.MarkSent(pending);
            await _client.SendQueryOnJob();
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

                // Potwierdzenie idzie przed czymkolwiek innym (§5.3). Platforma liczy termin od
                // wysłania przydziału, więc każda praca wykonana wcześniej - choćby log startowy -
                // zjada budżet, po którym zadanie wróci do kolejki i zostanie wysłane drugi raz.
                await _client.SendJobAccepted(m.Id, m.AttemptId);

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
