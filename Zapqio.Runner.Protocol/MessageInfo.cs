namespace Zapqio.Runner.Protocol
{
    public class MessageInfo
    {
        public List<MessageMethod> Methods { get; set; }
        public string Name { get; set; }

        /// <summary>
        /// Pojemność runnera: ile zadań wykonuje naraz, a więc ile nierozstrzygniętych zadań Web może
        /// u niego trzymać (§5.1, §5.2). Pole opcjonalne na łączu - runner, który go nie wysyła,
        /// wykonuje jedno zadanie naraz. Zawsze co najmniej 1; Web może przyciąć od góry.
        /// </summary>
        public int MaxConcurrency { get; set; } = 1;
    }
}
