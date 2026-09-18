using Zapqio.Runner.Core;
using Zapqio.Runner.Protocol.Enums;

namespace Zapqio.Runner
{
    /// <summary>
    /// Odbiornik, który host podstawia pod statyczny <see cref="RunnerLog"/>. Tłumaczy poziom z
    /// kontraktu modułów na poziom protokołu i oddaje wpis <see cref="JobLogWriter"/>.
    /// </summary>
    public sealed class ModuleLogSink : IRunnerLogSink
    {
        private readonly JobLogWriter _writer;

        public ModuleLogSink(JobLogWriter writer) => _writer = writer ?? throw new ArgumentNullException(nameof(writer));

        public bool IsEnabled(RunnerLogLevel level) => _writer.IsEnabled(ModuleLogLevels.ToProtocol(level));

        public void Write(RunnerLogLevel level, string message) =>
            _writer.Write(ModuleLogLevels.ToProtocol(level), message);
    }

    /// <summary>
    /// Most dla modułów, które wolą wstrzyknąć <c>ILogger&lt;T&gt;</c>, niż wołać statyczny
    /// <see cref="RunnerLog"/>. Rejestrowany wyłącznie w kontenerze modułów
    /// (<see cref="MethodsProvider"/>) - w kontenerze hosta zapętliłby własne logi runnera z powrotem
    /// do kolejki wyjściowej.
    /// </summary>
    public sealed class ModuleLoggerProvider : ILoggerProvider
    {
        private readonly JobLogWriter _writer;

        public ModuleLoggerProvider(JobLogWriter writer) => _writer = writer ?? throw new ArgumentNullException(nameof(writer));

        public ILogger CreateLogger(string categoryName) => new ModuleLogger(_writer);

        public void Dispose()
        {
        }

        private sealed class ModuleLogger : ILogger
        {
            private readonly JobLogWriter _writer;

            public ModuleLogger(JobLogWriter writer) => _writer = writer;

            public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

            public bool IsEnabled(LogLevel logLevel) =>
                logLevel != LogLevel.None && _writer.IsEnabled(ModuleLogLevels.ToProtocol(logLevel));

            public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
                Func<TState, Exception?, string> formatter)
            {
                if (!IsEnabled(logLevel)) return;

                var text = formatter(state, exception);
                if (exception is not null)
                    text = string.IsNullOrEmpty(text) ? exception.ToString() : $"{text}{Environment.NewLine}{exception}";

                _writer.Write(ModuleLogLevels.ToProtocol(logLevel), text);
            }
        }
    }

    /// <summary>Tłumaczenia poziomów między kontraktem modułów, <c>ILogger</c> i protokołem.</summary>
    internal static class ModuleLogLevels
    {
        public static MessageLogLevel ToProtocol(RunnerLogLevel level) => level switch
        {
            RunnerLogLevel.Debug => MessageLogLevel.Debug,
            RunnerLogLevel.Info => MessageLogLevel.Info,
            RunnerLogLevel.Warning => MessageLogLevel.Warning,
            RunnerLogLevel.Error => MessageLogLevel.Error,
            RunnerLogLevel.Critical => MessageLogLevel.Critical,
            // Moduł zbudowany pod nowszy kontrakt niż ten runner: wpisu nie gubimy, tylko spłaszczamy
            // do najbliższego znanego poziomu.
            _ => level > RunnerLogLevel.Critical ? MessageLogLevel.Critical : MessageLogLevel.Info,
        };

        /// <summary>
        /// <c>Trace</c> nie ma odpowiednika w protokole i schodzi do <see cref="MessageLogLevel.Debug"/> -
        /// po stronie platformy i tak byłby tym samym, a osobnego poziomu na łączu nie warto kupować
        /// kolejną wersją główną.
        /// </summary>
        public static MessageLogLevel ToProtocol(LogLevel level) => level switch
        {
            LogLevel.Trace => MessageLogLevel.Debug,
            LogLevel.Debug => MessageLogLevel.Debug,
            LogLevel.Information => MessageLogLevel.Info,
            LogLevel.Warning => MessageLogLevel.Warning,
            LogLevel.Error => MessageLogLevel.Error,
            LogLevel.Critical => MessageLogLevel.Critical,
            _ => MessageLogLevel.Info,
        };
    }
}
