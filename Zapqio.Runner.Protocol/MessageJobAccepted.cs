namespace Zapqio.Runner.Protocol
{
    /// <summary>
    /// Potwierdzenie odbioru przydziału (§5.3). Wysyłane natychmiast po odebraniu <see cref="MessageJob"/>,
    /// przed logiem startowym i przed wywołaniem metody. Mówi wyłącznie „mam to zadanie i je wykonam";
    /// o starcie metody mówi dopiero pierwszy <see cref="MessageLog"/>.
    ///
    /// Bez potwierdzenia w terminie platforma uzna przydział za niedostarczony i zwróci zadanie do
    /// kolejki - także wtedy, gdy gniazdo pozostaje otwarte.
    /// </summary>
    public class MessageJobAccepted
    {
        public Guid Id { get; set; }
        public Guid AttemptId { get; set; }
    }
}
