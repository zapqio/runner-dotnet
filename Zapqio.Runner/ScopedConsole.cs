using System.Text;
using Zapqio.Protocol;

namespace Zapqio.Runner
{
    public class ScopedConsole
    {
        private readonly ScopedTextWriter _out;
        private readonly ScopedTextWriter _error;
        private readonly LogQueue _logQueue;
        private readonly ILoggerFactory _loggerFactory;

        public ScopedConsole(LogQueue logQueue, ILoggerFactory loggerFactory)
        {
            _logQueue = logQueue;
            _loggerFactory = loggerFactory;
            _out = new ScopedTextWriter(Console.Out, Send);
            _error = new ScopedTextWriter(Console.Error, SendErr);
            Console.SetOut(_out);
            Console.SetError(_error);
        }
        private void SendErr(MessageJob? message, string text, ILogger logger)
        {
            if (message == null)
            {
                return;
            }
            _logQueue.AddLog(new MessageLog
            {
                Date = DateTimeOffset.Now,
                JobId = message.Id,
                Level = Zapqio.Protocol.Enums.MessageLogLevel.Error,
                Message = text,
            });
            logger?.LogError(text);
        }
        private void Send(MessageJob? message, string text, ILogger logger)
        {
            if (message == null)
            {
                return;
            }
            _logQueue.AddLog(new MessageLog
            {
                Date = DateTimeOffset.Now,
                JobId = message.Id,
                Level = Zapqio.Protocol.Enums.MessageLogLevel.Info,
                Message = text,
            });
            logger?.LogInformation(text);
        }
        public TextWriter Out => _out;
        public TextWriter Error => _error;

        public ConsoleScope BeginScope(MessageJob message)
        {
            var outWriter = new StringWriter();
            var errorWriter = new StringWriter();
            var outState = new ScopeState(outWriter, message, _loggerFactory.CreateLogger($"Method({message.Name})-{message.Id}"));
            var errorState = new ScopeState(errorWriter, message, _loggerFactory.CreateLogger($"Method({message.Name})-{message.Id}"));

            _out.SetScope(outState);
            _error.SetScope(errorState);

            var scope = new ConsoleScope(outWriter, errorWriter, _out, _error);
            return scope;
        }

        public class ConsoleScope : IDisposable
        {
            private readonly StringWriter _outWriter;
            private readonly StringWriter _errorWriter;
            private readonly ScopedTextWriter _out;
            private readonly ScopedTextWriter _error;

            internal ConsoleScope(StringWriter outWriter, StringWriter errorWriter, ScopedTextWriter outScoped, ScopedTextWriter errorScoped)
            {
                _outWriter = outWriter;
                _errorWriter = errorWriter;
                _out = outScoped;
                _error = errorScoped;
            }

            public void Dispose()
            {
                _out.SetScope(null);
                _error.SetScope(null);
                _outWriter.Dispose();
                _errorWriter.Dispose();
            }
        }
        internal class ScopeState
        {
            public readonly ILogger Log;
            public StringWriter Writer { get; }
            public MessageJob Message { get; }

            public ScopeState(StringWriter writer, MessageJob message, ILogger log)
            {
                Writer = writer;
                Message = message;
                Log = log;
            }
        }
        internal class ScopedTextWriter : TextWriter
        {
            private readonly TextWriter _default;
            private readonly AsyncLocal<ScopeState?> _local = new();
            private readonly Action<MessageJob?, string, ILogger>? _onWrite;

            public ScopedTextWriter(TextWriter defaultWriter, Action<MessageJob?, string, ILogger>? onWrite = null)
            {
                _default = defaultWriter;
                _onWrite = onWrite;
            }

            internal void SetScope(ScopeState? state) => _local.Value = state;

            private MessageJob? CurrentJob => _local.Value?.Message;

            public override Encoding Encoding => CurrentJob != null ? Encoding.UTF8 : _default.Encoding;

            public override void Write(char value)
            {
                if (CurrentJob != null)
                    _onWrite?.Invoke(CurrentJob, value.ToString(), _local.Value?.Log);
                else
                    _default.Write(value);
            }

            public override void Write(string? value)
            {
                if (CurrentJob != null)
                    _onWrite?.Invoke(CurrentJob, value ?? "", _local.Value?.Log);
                else
                    _default.Write(value);
            }

            public override void WriteLine(string? value)
            {
                if (CurrentJob != null)
                    _onWrite?.Invoke(CurrentJob, value ?? "", _local.Value?.Log);
                else
                    _default.WriteLine(value);
            }

            public override void WriteLine()
            {
                if (CurrentJob != null)
                    _onWrite?.Invoke(CurrentJob, "", _local.Value?.Log);
                else
                    _default.WriteLine();
            }
        }
    }
}
