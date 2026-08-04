namespace Zapqio.Runner.Protocol
{
    public class MessageJob
    {
        public Guid Id { get; set; }

        /// <summary>
        /// Identyfikator tej wysyłki (§5.2). To samo <see cref="Id"/> może przyjść kilka razy, a ta
        /// wartość jest za każdym razem inna. Odeślij ją niezmienioną w potwierdzeniu, każdym logu
        /// i wyniku - po niej platforma poznaje, że wiadomość należy do próby trwającej.
        /// </summary>
        public Guid AttemptId { get; set; }

        public string Name { get; set; }
        public string Data { get; set; }
    }
}
