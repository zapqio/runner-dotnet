using Zapqio.Runner.Protocol;

namespace Zapqio.Runner
{
    /// <summary>
    /// Ostatni <c>JobReturn</c>, o którym nie wiadomo, czy doszedł. Protokół nie ma potwierdzenia
    /// wyniku, a zerwane gniazdo nie zawsze zgłasza się przy wysyłce: po restarcie platformy pierwszy
    /// zapis do martwego połączenia potrafi „się udać", bo ląduje w buforze systemu. Dlatego wynik
    /// zostaje tu w dwóch sytuacjach - gdy wysyłka jawnie się nie powiodła i gdy się powiodła, ale
    /// od tej pory nie przyszło nic od platformy, co dowodziłoby, że połączenie żyło.
    ///
    /// Po każdym ponownym połączeniu host wysyła to, co tu leży, z tym samym <c>attemptId</c>
    /// (§5.5 protokołu). Platforma przyjmie wynik, jeśli wciąż trzyma zadanie jako „wynik nieznany",
    /// a odrzuci ze śladem w dzienniku, gdy zdążyła je rozstrzygnąć inaczej - w najgorszym razie
    /// duplikat kosztuje jedną linijkę w historii zadania, a zgubiony wynik kosztowałby człowieka.
    ///
    /// Jedno miejsce, bo runner wykonuje jedno zadanie naraz; restart procesu je gubi (wynik nie
    /// jest zapisywany na dysk - to decyzja z MU3, do zmiany, gdy zajdzie potrzeba).
    /// </summary>
    public sealed class PendingJobReturn
    {
        private readonly object _gate = new();
        private MessageJobReturn? _pending;

        /// <summary>Wysyłka nie powiodła się - wynik czeka na ponowne połączenie.</summary>
        public void MarkFailed(MessageJobReturn message)
        {
            lock (_gate) _pending = message;
        }

        /// <summary>
        /// Wysyłka się powiodła, ale bez potwierdzenia od platformy. Zostaje do
        /// <see cref="Confirm"/> - jeśli wcześniej połączenie padnie, pójdzie jeszcze raz.
        /// </summary>
        public void MarkSent(MessageJobReturn message)
        {
            lock (_gate) _pending = message;
        }

        /// <summary>
        /// Coś przyszło od platformy po wysyłce. Połączenie żyło, więc wynik do niej dotarł i nie ma
        /// czego ponawiać.
        /// </summary>
        public void Confirm()
        {
            lock (_gate) _pending = null;
        }

        /// <summary>Wynik do ponowienia albo <c>null</c>, gdy nic nie czeka.</summary>
        public MessageJobReturn? Peek()
        {
            lock (_gate) return _pending;
        }
    }
}
