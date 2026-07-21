using Zapqio.Runner.Protocol.Enums;

namespace Zapqio.Runner.Protocol
{
    public class MessageJobReturn
    {
        public Guid Id { get; set; }
        public MessageResponseStatus Status { get; set; }
        public string Data { get; set; }
    }
}
