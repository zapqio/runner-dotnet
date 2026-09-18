using System.Collections.Concurrent;
using Zapqio.Runner.Core;
using Zapqio.Runner.Protocol;
using Zapqio.Runner.Protocol.Enums;

namespace Zapqio.Runner
{
    /// <summary>
    /// Jedyne miejsce, w którym wpis od modułu staje się ramką protokołu. Schodzą się tu wszystkie
    /// trzy kanały - <c>stdout</c>/<c>stderr</c> przez <see cref="ScopedConsole"/>, statyczny
    /// <see cref="RunnerLog"/> i <c>ILogger</c> wstrzyknięty do modułu - żeby próg wysyłki, odbicie
    /// w logu lokalnym i przypisanie wpisu do zadania działały dla nich tak samo.
    /// </summary>
    /// <remarks>
    /// <b>Czego tu nie ma i być nie może.</b> Wpisy, które host generuje sam - wiersz startowy
    /// zadania, złapany wyjątek, informacja o nieudanej wysyłce wyniku, ostrzeżenie o pełnej kolejce -
    /// omijają tę klasę i idą wprost do <see cref="Outbox"/>. To nie przeoczenie, tylko warunek
    /// poprawności: wiersz startowy jest poziomu <see cref="MessageLogLevel.Info"/>, a to on przestawia
    /// zadanie po stronie platformy na <i>Executing</i> (§5.4 protokołu). Gdyby podlegał progowi,
    /// ustawienie <see cref="AppSettings.MinRemoteLogLevel"/> na <c>Warning</c> zatrzymałoby wysyłkę
    /// tego wiersza, <see cref="ExecuteJob"/> odwołałby wykonanie i runner przestałby robić cokolwiek.
    /// Próg dotyczy więc wyłącznie tego, co pisze moduł.
    /// </remarks>
    public sealed class JobLogWriter
    {
        private readonly Outbox _outbox;
        private readonly ILoggerFactory _loggerFactory;
        private readonly MessageLogLevel _minimum;

        /// <summary>
        /// Logger lokalny per metoda, nie per zadanie: identyfikatory zadań są nieograniczone, więc
        /// pamięć podręczna po nich rosłaby przez całe życie procesu. Zadanie rozpoznaje się po
        /// przedrostku w treści wpisu.
        /// </summary>
        private readonly ConcurrentDictionary<string, ILogger> _mirrors = new(StringComparer.Ordinal);

        public JobLogWriter(Outbox outbox, AppSettings settings, ILoggerFactory loggerFactory)
        {
            _outbox = outbox ?? throw new ArgumentNullException(nameof(outbox));
            _loggerFactory = loggerFactory ?? throw new ArgumentNullException(nameof(loggerFactory));
            _minimum = (settings ?? throw new ArgumentNullException(nameof(settings))).RemoteLogThreshold;
        }

        /// <summary>Próg, poniżej którego wpisy modułu nie idą do platformy.</summary>
        public MessageLogLevel Minimum => _minimum;

        /// <summary>Czy wpis o tej wadze w ogóle opuści runnera.</summary>
        public bool IsEnabled(MessageLogLevel level) => level >= _minimum;

        /// <summary>
        /// Wpis bez jawnie wskazanego zadania - przypisywany do tego, w którego kontekście powstał.
        /// Poza wykonaniem metody (konstruktor modułu, wątek spoza zadania) nie ma zadania, do którego
        /// mógłby należeć: nie idzie do platformy, ale ląduje w logu lokalnym, żeby nie zniknął bez śladu.
        /// </summary>
        public void Write(MessageLogLevel level, string message)
        {
            var context = JobContext.Current;
            if (context is null)
            {
                if (IsEnabled(level))
                    Mirror("Module", Guid.Empty, level, message);
                return;
            }

            Write(context.JobId, context.AttemptId, context.MethodName, level, message);
        }

        /// <summary>Wpis przypisany wprost do zadania - tak woła <see cref="ScopedConsole"/>, który zadanie zna.</summary>
        public void Write(Guid jobId, Guid attemptId, string methodName, MessageLogLevel level, string message)
        {
            if (!IsEnabled(level)) return;

            _outbox.EnqueueLog(new MessageLog
            {
                Date = DateTimeOffset.Now,
                JobId = jobId,
                AttemptId = attemptId,
                Level = level,
                Message = message ?? "",
            });

            Mirror(methodName, jobId, level, message ?? "");
        }

        /// <summary>To samo idzie do logu lokalnego runnera - po awarii łącza to jedyny ślad po pracy metody.</summary>
        private void Mirror(string methodName, Guid jobId, MessageLogLevel level, string message)
        {
            var logger = _mirrors.GetOrAdd(
                string.IsNullOrEmpty(methodName) ? "Module" : methodName,
                name => _loggerFactory.CreateLogger($"Method({name})"));

            // Treść wpisu jest argumentem, nie szablonem - nawiasy klamrowe w wyjściu metody nie mogą
            // trafić do parsera szablonu Seriloga.
            logger.Log(ToLocal(level), "[{JobId}] {LogMessage}", jobId, message);
        }

        private static LogLevel ToLocal(MessageLogLevel level) => level switch
        {
            MessageLogLevel.Debug => LogLevel.Debug,
            MessageLogLevel.Info => LogLevel.Information,
            MessageLogLevel.Warning => LogLevel.Warning,
            MessageLogLevel.Error => LogLevel.Error,
            MessageLogLevel.Critical => LogLevel.Critical,
            _ => LogLevel.Information,
        };
    }
}
