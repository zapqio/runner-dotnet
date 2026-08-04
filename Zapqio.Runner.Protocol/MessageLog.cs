using Zapqio.Runner.Protocol.Enums;

namespace Zapqio.Runner.Protocol
{
    public class MessageLog
    {
        public DateTimeOffset Date { get; set; }
        public Guid JobId { get; set; }

        /// <summary>Próba, do której należy ten wpis - <c>attemptId</c> z przydziału (§5.2).</summary>
        public Guid AttemptId { get; set; }

        public MessageLogLevel Level { get; set; }
        public string Message { get; set; }

    }
}
