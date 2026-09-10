namespace Zapqio.Runner.Core
{
    /// <summary>
    /// Kontekst zadania, w ramach którego runner woła <see cref="IRunnerMethod.Run"/>. Metoda czyta
    /// go przez <see cref="Current"/> - sygnatura <see cref="IRunnerMethod"/> się nie zmienia, więc
    /// moduły zbudowane pod 1.0 działają bez przebudowy, a te, które chcą, sięgają po kontekst.
    /// </summary>
    /// <remarks>
    /// Po co to jest: platforma może wysłać to samo zadanie drugi raz. Dzieje się tak, gdy straciła
    /// połączenie z runnerem po starcie metody i nie wie, jak się skończyła, albo gdy operator
    /// świadomie ponowił krok o nieznanym wyniku. <see cref="JobId"/> jest wtedy ten sam, a
    /// <see cref="AttemptId"/> inny. Metoda z nieidempotentnym skutkiem (faktura, mail, przelew)
    /// POWINNA zapisać <see cref="JobId"/> razem ze skutkiem i przed wykonaniem sprawdzić, czy skutek
    /// z tym identyfikatorem już istnieje - wtedy oddaje go zamiast tworzyć drugi.
    ///
    /// Wartość żyje w <see cref="System.Threading.AsyncLocal{T}"/>, więc jest widoczna także w
    /// zadaniach i wątkach, które metoda uruchomi w trakcie pracy, a znika po jej zakończeniu. Poza
    /// wykonaniem metody (konstruktor, <see cref="IRunnerMethod.NameMethod"/>) jest pusta.
    /// </remarks>
    public sealed class JobContext
    {
        private static readonly AsyncLocal<JobContext?> Ambient = new();

        /// <summary>
        /// Identyfikator OPERACJI: pole <c>id</c> przydziału <c>Job</c> w protokole. Stały między
        /// wysyłkami tego samego zadania i po ponowieniu przez operatora. To po nim moduł rozpoznaje,
        /// że wykonuje coś drugi raz.
        /// </summary>
        public Guid JobId { get; }

        /// <summary>Identyfikator tej konkretnej wysyłki (<c>attemptId</c>). Inny przy każdym ponowieniu.</summary>
        public Guid AttemptId { get; }

        /// <summary>Nazwa metody z przydziału - ta, którą metoda ogłosiła w <see cref="IRunnerMethod.NameMethod"/>.</summary>
        public string MethodName { get; }

        public JobContext(Guid jobId, Guid attemptId, string methodName)
        {
            JobId = jobId;
            AttemptId = attemptId;
            MethodName = methodName ?? throw new ArgumentNullException(nameof(methodName));
        }

        /// <summary>Kontekst bieżącego zadania albo <c>null</c> poza wykonaniem metody.</summary>
        public static JobContext? Current => Ambient.Value;

        /// <summary>
        /// Ustawia kontekst na czas wykonania metody. Woła to host runnera, moduły nie mają powodu.
        /// Zwolnienie zwróconego obiektu przywraca poprzedni kontekst.
        /// </summary>
        public static IDisposable Begin(JobContext context)
        {
            if (context is null) throw new ArgumentNullException(nameof(context));

            var previous = Ambient.Value;
            Ambient.Value = context;
            return new Scope(previous);
        }

        private sealed class Scope : IDisposable
        {
            private readonly JobContext? _previous;
            private bool _disposed;

            public Scope(JobContext? previous) => _previous = previous;

            public void Dispose()
            {
                if (_disposed) return;
                _disposed = true;
                Ambient.Value = _previous;
            }
        }
    }
}
