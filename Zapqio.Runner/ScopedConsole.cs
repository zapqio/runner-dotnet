using System.Text;
using Zapqio.Runner.Protocol;
using Zapqio.Runner.Protocol.Enums;

namespace Zapqio.Runner
{
    /// <summary>
    /// Kanał logu dla modułów, które niczego nie wołają: <c>Console.WriteLine</c> metody idzie do
    /// platformy jako <see cref="MessageLogLevel.Info"/>, a <c>Console.Error</c> jako
    /// <see cref="MessageLogLevel.Error"/>. Moduł, który chce nazwać wagę wpisu wprost - także
    /// <c>Debug</c>, <c>Warning</c> i <c>Critical</c> - sięga po <see cref="Core.RunnerLog"/> albo
    /// wstrzykuje <c>ILogger</c>; jedno i drugie kończy w tym samym <see cref="JobLogWriter"/>.
    /// </summary>
    /// <remarks>
    /// Podmiana <c>Console.Out</c> jest globalna i jednorazowa, ale przekierowanie działa zakresowo:
    /// pisarz kieruje tekst do kolejki tylko wtedy, gdy w bieżącym przepływie trwa zadanie. Poza
    /// zadaniem - start runnera, jego własne logi - tekst leci na prawdziwą konsolę. Dlatego
    /// <c>Program</c> zapamiętuje oryginalne <c>Console.Out</c>, zanim powstanie ta klasa, i to do
    /// niego pisze Serilog: inaczej własne logi runnera wpadłyby do kolejki zadań.
    /// </remarks>
    public class ScopedConsole
    {
        private readonly ScopedTextWriter _out;
        private readonly ScopedTextWriter _error;

        public ScopedConsole(JobLogWriter writer)
        {
            if (writer is null) throw new ArgumentNullException(nameof(writer));

            _out = new ScopedTextWriter(Console.Out, (job, text) =>
                writer.Write(job.Id, job.AttemptId, job.Name, MessageLogLevel.Info, text));
            _error = new ScopedTextWriter(Console.Error, (job, text) =>
                writer.Write(job.Id, job.AttemptId, job.Name, MessageLogLevel.Error, text));

            Console.SetOut(_out);
            Console.SetError(_error);
        }

        public TextWriter Out => _out;
        public TextWriter Error => _error;

        /// <summary>
        /// Włącza przekierowanie konsoli na czas wykonania metody. Zakres żyje w
        /// <see cref="AsyncLocal{T}"/>, więc obejmuje też zadania i wątki, które metoda uruchomi.
        /// </summary>
        public ConsoleScope BeginScope(MessageJob message)
        {
            if (message is null) throw new ArgumentNullException(nameof(message));

            _out.SetScope(message);
            _error.SetScope(message);
            return new ConsoleScope(_out, _error);
        }

        public sealed class ConsoleScope : IDisposable
        {
            private readonly ScopedTextWriter _out;
            private readonly ScopedTextWriter _error;
            private bool _disposed;

            internal ConsoleScope(ScopedTextWriter outScoped, ScopedTextWriter errorScoped)
            {
                _out = outScoped;
                _error = errorScoped;
            }

            public void Dispose()
            {
                if (_disposed) return;
                _disposed = true;
                _out.SetScope(null);
                _error.SetScope(null);
            }
        }

        internal class ScopedTextWriter : TextWriter
        {
            private readonly TextWriter _default;
            private readonly AsyncLocal<MessageJob?> _local = new();
            private readonly Action<MessageJob, string> _onWrite;

            public ScopedTextWriter(TextWriter defaultWriter, Action<MessageJob, string> onWrite)
            {
                _default = defaultWriter;
                _onWrite = onWrite;
            }

            internal void SetScope(MessageJob? job) => _local.Value = job;

            private MessageJob? CurrentJob => _local.Value;

            public override Encoding Encoding => CurrentJob != null ? Encoding.UTF8 : _default.Encoding;

            public override void Write(char value)
            {
                if (CurrentJob is { } job)
                    _onWrite(job, value.ToString());
                else
                    _default.Write(value);
            }

            public override void Write(string? value)
            {
                if (CurrentJob is { } job)
                    _onWrite(job, value ?? "");
                else
                    _default.Write(value);
            }

            public override void WriteLine(string? value)
            {
                if (CurrentJob is { } job)
                    _onWrite(job, value ?? "");
                else
                    _default.WriteLine(value);
            }

            public override void WriteLine()
            {
                if (CurrentJob is { } job)
                    _onWrite(job, "");
                else
                    _default.WriteLine();
            }
        }
    }
}
