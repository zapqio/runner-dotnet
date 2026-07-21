using Zapqio.Runner.Protocol.Enums;

namespace Zapqio.Runner.Protocol
{
    public class MessageLog
    {
        public DateTimeOffset Date { get; set; }
        public Guid JobId { get; set; }
        public MessageLogLevel Level { get; set; }
        public string Message { get; set; }

    }
}
