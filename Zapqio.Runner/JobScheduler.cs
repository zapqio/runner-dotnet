using System.Collections.Concurrent;
using System.Threading.Channels;
using Zapqio.Runner.Protocol;

namespace Zapqio.Runner
{
    /// <summary>
    /// Planista zadań. Pętla odbioru tylko tu odkłada przydziały i wraca do gniazda; wykonanie
    /// ogranicza semafor o pojemności <see cref="MaxConcurrency"/>.
    ///
    /// Potwierdzenie odbioru (<c>JobAccepted</c>) idzie od razu po zdjęciu zadania z kolejki, także
    /// gdy wszystkie sloty są zajęte i zadanie poczeka: potwierdzenie mówi „mam i wykonam", a nie
    /// „ruszyłem" - o starcie mówi log startowy. Odwlekanie potwierdzenia do wolnego slotu
    /// skończyłoby się po terminie zwrotem zadania do kolejki i drugą wysyłką, a runner wykonałby
    /// jeszcze starą próbę. Taka lokalna kolejka zapełnia się wyłącznie, gdy limit w panelu Web jest
    /// wyższy niż <c>MaxConcurrency</c> tego runnera.
    ///
    /// Po każdym zakończonym zadaniu, gdy jest wolny slot, idzie jedno odpytanie o kolejne. Wykonanie,
    /// potwierdzenie i odpytanie są podstawiane delegatami, żeby dało się to sprawdzić bez gniazda.
    /// </summary>
    public sealed class JobScheduler
    {
        // Dwie kolejki, bo potwierdzenie i slot to dwie różne rzeczy: przydział ma być potwierdzony
        // natychmiast po odebraniu, a na slot może czekać dowolnie długo. Jedna pętla, która czeka na
        // semafor, nie czytałaby w tym czasie kolejnych przydziałów - te leżałyby niepotwierdzone i po
        // terminie platforma zwracałaby je do kolejki, wysyłała ponownie i tak w kółko.
        // Bez SingleReader: wariant pod jednego czytelnika nie obsługuje Count, a Queued go potrzebuje.
        private readonly Channel<MessageJob> _inbound = Channel.CreateUnbounded<MessageJob>(
            new UnboundedChannelOptions { SingleReader = false, SingleWriter = false });

        private readonly Channel<MessageJob> _accepted = Channel.CreateUnbounded<MessageJob>(
            new UnboundedChannelOptions { SingleReader = false, SingleWriter = false });

        private readonly SemaphoreSlim _slots;
        private readonly ConcurrentDictionary<Guid, Task> _running = new();
        private readonly Func<MessageJob, Task> _execute;
        private readonly Func<MessageJob, Task> _accept;
        private readonly Func<Task> _poll;
        private readonly ILogger<JobScheduler> _logger;
        private volatile bool _closed;

        public JobScheduler(
            int maxConcurrency,
            Func<MessageJob, Task> execute,
            Func<MessageJob, Task> accept,
            Func<Task> poll,
            ILogger<JobScheduler> logger)
        {
            MaxConcurrency = Math.Max(1, maxConcurrency);
            _slots = new SemaphoreSlim(MaxConcurrency, MaxConcurrency);
            _execute = execute;
            _accept = accept;
            _poll = poll;
            _logger = logger;
        }

        public int MaxConcurrency { get; }

        /// <summary>Wolne sloty w tej chwili.</summary>
        public int FreeSlots => _slots.CurrentCount;

        /// <summary>Zadania w trakcie wykonania.</summary>
        public int Running => _running.Count;

        /// <summary>Przydziały odebrane, a jeszcze nierozpoczęte: niepotwierdzone w drodze, potwierdzone w kolejce i to jedno zdjęte z niej, które stoi na semaforze.</summary>
        public int Queued => _inbound.Reader.Count + _accepted.Reader.Count + Volatile.Read(ref _awaitingSlot);

        private int _awaitingSlot;

        /// <summary>Fałsz po <see cref="CompleteAdding"/>: runner się zatrzymuje i nie przyjmuje nowych przydziałów.</summary>
        public bool Enqueue(MessageJob job) => !_closed && _inbound.Writer.TryWrite(job);

        /// <summary>
        /// Zatrzymanie: nic nowego nie rusza. Zadania jeszcze niezdjęte z kolejki przepadają - są w
        /// <c>Dispatched</c>, więc po zamknięciu gniazda platforma zwróci je do kolejki, a to, co trwa,
        /// dokończy się i odeśle wynik (patrz <see cref="WaitForRunningAsync"/>).
        /// </summary>
        public void CompleteAdding()
        {
            _closed = true;
            _inbound.Writer.TryComplete();
            _accepted.Writer.TryComplete();
        }

        /// <summary>Obie pętle naraz; kończy się, gdy obie skończą (po anulowaniu albo zamknięciu kolejek).</summary>
        public Task RunAsync(CancellationToken cancellationToken) =>
            Task.WhenAll(AcceptLoopAsync(cancellationToken), StartLoopAsync(cancellationToken));

        /// <summary>Potwierdza każdy przydział od razu po odebraniu i odkłada go do kolejki oczekujących na slot.</summary>
        private async Task AcceptLoopAsync(CancellationToken cancellationToken)
        {
            try
            {
                while (await _inbound.Reader.WaitToReadAsync(cancellationToken))
                {
                    while (_inbound.Reader.TryRead(out var job))
                    {
                        if (_closed) return;

                        await _accept(job);
                        _accepted.Writer.TryWrite(job);
                    }
                }
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                // Zatrzymanie hosta.
            }
        }

        /// <summary>Bierze potwierdzone przydziały po kolei i startuje każdy, gdy tylko zwolni się slot.</summary>
        private async Task StartLoopAsync(CancellationToken cancellationToken)
        {
            try
            {
                while (await _accepted.Reader.WaitToReadAsync(cancellationToken))
                {
                    while (_accepted.Reader.TryRead(out var job))
                    {
                        if (_closed) return;

                        Volatile.Write(ref _awaitingSlot, 1);
                        try
                        {
                            await _slots.WaitAsync(cancellationToken);
                        }
                        finally
                        {
                            Volatile.Write(ref _awaitingSlot, 0);
                        }

                        if (_closed)
                        {
                            _slots.Release();
                            return;
                        }

                        Start(job);
                    }
                }
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                // Zatrzymanie hosta.
            }
        }

        private void Start(MessageJob job)
        {
            var done = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            _running[job.AttemptId] = done.Task;

            _ = Task.Run(async () =>
            {
                try
                {
                    await _execute(job);
                }
                catch (Exception ex)
                {
                    // Wyjątek z metody łapie ExecuteJob i zamienia na wynik ERROR; tu ląduje tylko
                    // awaria samego hosta wokół metody.
                    _logger.LogError(ex, "Zadanie {JobId} (próba {AttemptId}) przerwane poza metodą", job.Id, job.AttemptId);
                }
                finally
                {
                    _slots.Release();
                    _running.TryRemove(job.AttemptId, out _);
                    done.TrySetResult();
                    await PollIfFreeAsync();
                }
            });
        }

        private async Task PollIfFreeAsync()
        {
            if (_closed || _slots.CurrentCount <= 0) return;

            try
            {
                await _poll();
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Odpytanie o kolejne zadanie nie powiodło się");
            }
        }

        /// <summary>Prawda, gdy wszystkie trwające zadania skończyły się przed upływem <paramref name="timeout"/>.</summary>
        public async Task<bool> WaitForRunningAsync(TimeSpan timeout, CancellationToken cancellationToken)
        {
            var running = _running.Values.ToArray();
            if (running.Length == 0) return true;

            var all = Task.WhenAll(running);
            var finished = await Task.WhenAny(all, Task.Delay(timeout, cancellationToken));
            return finished == all;
        }
    }
}
