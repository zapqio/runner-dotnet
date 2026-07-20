using Zapqio.Protocol.Enums;

namespace Zapqio.Protocol
{
    public class Message
    {
        public MessageType Type { get; set; }
        public string Data { get; set; }
        public override string ToString()
        {
            var d = "";
            if (string.IsNullOrEmpty(Data))
            {
                d = $" - {Data}";
            }
            return $"{Type}{d}";
        }

    }
}
