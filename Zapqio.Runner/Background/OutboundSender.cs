using Zapqio.Runner.Protocol;

namespace Zapqio.Runner.Background
{
    /// <summary>Gniazdo widziane od strony nadawcy - do podmiany w testach.</summary>
    public interface IOutboundTransport
    {
        bool IsConnected { get; }

        /// <summary>Zapis jednej ramki. Fałsz, gdy gniazdo nie jest otwarte albo zapis się nie powiódł.</summary>
        Task<bool> WriteAsync(Message message);
    }

    /// <summary>
    /// Jedyny wywołujący <see cref="IOutboundTransport.WriteAsync"/>. Drenuje <see cref="Outbox"/> w
    /// kolejności FIFO:
    /// <list type="bullet">
    ///   <item>element, na który ktoś czeka, przy zamkniętym gnieździe kończy się od razu
    ///         niepowodzeniem - metoda nie może ruszyć na podstawie logu startowego, który wyjdzie
    ///         dopiero po ponownym połączeniu, gdy platforma dawno zwróciła zadanie do kolejki;</item>
    ///   <item>linia logu czeka na połączenie (zaparkowana, w kolejności) i wychodzi przed wszystkim,
    ///         co zakolejkowano później - tak jak dotąd kolejka logów czekała na powrót gniazda;</item>
    ///   <item>element „wyślij i zapomnij" (potwierdzenie, odpytanie) przy zamkniętym gnieździe
    ///         przepada - platforma i tak ponowi przydział albo sama zapyta.</item>
    /// </list>
    /// </summary>
    public sealed class OutboundSender : BackgroundService
    {
        /// <summary>Co ile nadawca sprawdza, czy gniazdo wróciło, gdy trzyma zaparkowane linie.</summary>
        private static readonly TimeSpan ReconnectPoll = TimeSpan.FromMilliseconds(250);

        private readonly Outbox _outbox;
        private readonly IOutboundTransport _transport;
        private readonly ILogger<OutboundSender> _logger;

        /// <summary>Linie logu czekające na połączenie. Dotyka ich wyłącznie pętla nadawcy.</summary>
        private readonly List<OutboxItem> _parked = new();

        private volatile bool _busy;

        public OutboundSender(Outbox outbox, IOutboundTransport transport, ILogger<OutboundSender> logger)
        {
            _outbox = outbox;
            _transport = transport;
            _logger = logger;
        }

        /// <summary>Nic w kolejce, nic zaparkowane, nic w trakcie zapisu - wszystko, co zakolejkowano, wyszło.</summary>
        public bool IsDrained => !_busy && _parked.Count == 0 && _outbox.Count == 0;

        protected override Task ExecuteAsync(CancellationToken stoppingToken) => PumpAsync(stoppingToken);

        /// <summary>Pętla nadawcy; publiczna, żeby test mógł ją uruchomić bez hosta.</summary>
        public async Task PumpAsync(CancellationToken cancellationToken)
        {
            try
            {
                while (!cancellationToken.IsCancellationRequested)
                {
                    if (_parked.Count > 0)
                    {
                        // Zaparkowane linie mają pierwszeństwo. Bez gniazda (albo gdy zapis w nie
                        // zawodzi) czekamy krótko i patrzymy znowu, ale w międzyczasie obsługujemy to,
                        // co przychodzi: element z oczekującym ma dostać odmowę teraz, nie po powrocie.
                        var flushed = _transport.IsConnected && await FlushParkedAsync();
                        if (!flushed && !_outbox.Reader.TryPeek(out _))
                            await Task.Delay(ReconnectPoll, cancellationToken);

                        if (_outbox.Reader.TryRead(out var next))
                            await DeliverAsync(next);

                        continue;
                    }

                    if (!await _outbox.Reader.WaitToReadAsync(cancellationToken))
                        return;

                    while (_outbox.Reader.TryRead(out var item))
                        await DeliverAsync(item);
                }
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                // Zatrzymanie hosta.
            }
        }

        private async Task DeliverAsync(OutboxItem item)
        {
            _busy = true;
            try
            {
                if (!_transport.IsConnected)
                {
                    if (item.Completion is { } waiting)
                    {
                        waiting.TrySetResult(null);
                        return;
                    }

                    if (item.IsLogLine)
                    {
                        _parked.Add(item);
                        return;
                    }

                    _logger.LogDebug("Pominięto {Type}: gniazdo nie jest otwarte", item.Message.Type);
                    return;
                }

                await FlushParkedAsync();

                if (_parked.Count > 0)
                {
                    // Gniazdo padło w trakcie opróżniania: kolejność zostaje, nowy element czeka za zaparkowanymi.
                    if (item.Completion is { } waiting)
                        waiting.TrySetResult(null);
                    else if (item.IsLogLine)
                        _parked.Add(item);
                    return;
                }

                var written = await _transport.WriteAsync(item.Message);

                if (item.Completion is { } completion)
                {
                    completion.TrySetResult(written ? _outbox.NextSeq() : null);
                    return;
                }

                if (item.IsLogLine)
                {
                    if (written)
                        Release(item);
                    else
                        _parked.Add(item);
                }
            }
            finally
            {
                _busy = false;
            }
        }

        /// <summary>Prawda, gdy wszystkie zaparkowane linie wyszły; fałsz, gdy zapis zawiódł i coś zostało.</summary>
        private async Task<bool> FlushParkedAsync()
        {
            while (_parked.Count > 0 && _transport.IsConnected)
            {
                var head = _parked[0];
                if (!await _transport.WriteAsync(head.Message))
                    return false;

                _parked.RemoveAt(0);
                Release(head);
            }

            return _parked.Count == 0;
        }

        private void Release(OutboxItem logLine)
        {
            if (logLine.CountsAsLogLine)
                _outbox.OnLogLineLeft();
        }
    }
}
