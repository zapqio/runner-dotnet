namespace Zapqio.Runner.Core
{
    /// <summary>
    /// Log zadania widziany od strony modułu. To jedyny sposób, żeby nazwać wagę wpisu wprost -
    /// <c>Console.WriteLine</c> daje tylko <see cref="RunnerLogLevel.Info"/>, a <c>Console.Error</c>
    /// tylko <see cref="RunnerLogLevel.Error"/>, i tak zostaje dla modułów, które niczego nie zmieniają.
    /// </summary>
    /// <remarks>
    /// Wpisy trafiają do zadania, w którego kontekście powstały (<see cref="JobContext.Current"/>),
    /// więc klasa jest statyczna celowo: działa z metod pomocniczych, klas statycznych i wątków, które
    /// metoda uruchomiła w trakcie pracy, bez przekazywania loggera przez pół modułu. Kto woli
    /// wstrzykiwanie, może wziąć w konstruktorze <c>ILogger&lt;T&gt;</c> - runner rejestruje go w
    /// kontenerze modułów i kieruje w to samo miejsce.
    ///
    /// Wpis powstały poza wykonaniem metody nie ma zadania, do którego mógłby należeć - nie idzie do
    /// platformy, ale nie przepada: host zapisuje go we własnym logu. Tak samo wpis z wątku, który
    /// metoda zostawiła działający po swoim zakończeniu, o ile wątek zdążył przejąć kontekst.
    ///
    /// Bez podstawionego odbiornika (moduł uruchomiony poza runnerem, np. w swoich testach) wpisy idą
    /// na konsolę procesu z przedrostkiem poziomu, żeby nie ginęły po cichu.
    /// </remarks>
    public static class RunnerLog
    {
        private static IRunnerLogSink? _sink;

        /// <summary>
        /// Podstawia odbiornik wpisów; woła to host runnera raz, przy starcie. Zwolnienie zwróconego
        /// obiektu przywraca poprzedni odbiornik - z tego korzystają testy, nie moduły.
        /// </summary>
        public static IDisposable UseSink(IRunnerLogSink sink)
        {
            if (sink is null) throw new ArgumentNullException(nameof(sink));

            var previous = Interlocked.Exchange(ref _sink, sink);
            return new SinkScope(previous);
        }

        /// <summary>
        /// Czy wpis o tej wadze zostanie gdziekolwiek zapisany. Warto sprawdzić przed zbudowaniem
        /// kosztownej treści: <c>if (RunnerLog.IsEnabled(RunnerLogLevel.Debug)) RunnerLog.Debug(Dump())</c>.
        /// </summary>
        public static bool IsEnabled(RunnerLogLevel level)
        {
            var sink = Volatile.Read(ref _sink);
            return sink is null || sink.IsEnabled(level);
        }

        /// <summary>Wpis o podanej wadze. Nigdy nie rzuca - log nie może wywrócić metody.</summary>
        public static void Write(RunnerLogLevel level, string? message)
        {
            var sink = Volatile.Read(ref _sink);
            if (sink is null)
            {
                WriteToConsole(level, message ?? "");
                return;
            }

            try
            {
                if (sink.IsEnabled(level))
                    sink.Write(level, message ?? "");
            }
            catch
            {
                // Odbiornik hosta ma własną obsługę błędów; gdyby zawiódł, metoda i tak ma pracować dalej.
            }
        }

        /// <summary>Szczegół diagnostyczny. Domyślnie NIE idzie do platformy - trzeba obniżyć próg na runnerze.</summary>
        public static void Debug(string? message) => Write(RunnerLogLevel.Debug, message);

        /// <summary>Zwykły przebieg - to samo, co <c>Console.WriteLine</c>.</summary>
        public static void Info(string? message) => Write(RunnerLogLevel.Info, message);

        /// <summary>Coś poszło inaczej, niż powinno, ale metoda idzie dalej.</summary>
        public static void Warning(string? message) => Write(RunnerLogLevel.Warning, message);

        /// <summary>Błąd - to samo, co <c>Console.Error.WriteLine</c>.</summary>
        public static void Error(string? message) => Write(RunnerLogLevel.Error, message);

        /// <summary>Błąd wraz z wyjątkiem; treść wyjątku dopisuje się pod wiadomością.</summary>
        public static void Error(Exception exception, string? message = null) =>
            Write(RunnerLogLevel.Error, Describe(exception, message));

        /// <summary>Błąd, po którym nie ma sensu ciągnąć dalej.</summary>
        public static void Critical(string? message) => Write(RunnerLogLevel.Critical, message);

        /// <inheritdoc cref="Critical(string)"/>
        public static void Critical(Exception exception, string? message = null) =>
            Write(RunnerLogLevel.Critical, Describe(exception, message));

        private static string Describe(Exception exception, string? message)
        {
            if (exception is null) return message ?? "";
            return string.IsNullOrEmpty(message) ? exception.ToString() : $"{message}{Environment.NewLine}{exception}";
        }

        private static void WriteToConsole(RunnerLogLevel level, string message)
        {
            var writer = level >= RunnerLogLevel.Warning ? Console.Error : Console.Out;
            writer.WriteLine($"[{level}] {message}");
        }

        private sealed class SinkScope : IDisposable
        {
            private readonly IRunnerLogSink? _previous;
            private bool _disposed;

            public SinkScope(IRunnerLogSink? previous) => _previous = previous;

            public void Dispose()
            {
                if (_disposed) return;
                _disposed = true;
                Volatile.Write(ref _sink, _previous);
            }
        }
    }
}
