namespace Zapqio.Runner.Core
{
    /// <summary>
    /// Odbiornik wpisów z <see cref="RunnerLog"/>. Implementuje go host runnera i podstawia przez
    /// <see cref="RunnerLog.UseSink"/> - moduły nie mają powodu ani tego implementować, ani wołać.
    /// </summary>
    /// <remarks>
    /// Implementacja MUSI być bezpieczna dla wielu wątków: przy <c>MaxConcurrency</c> powyżej 1 piszą
    /// tu równolegle różne zadania, a do tego wątki, które metoda uruchomiła sama. Do którego zadania
    /// należy wpis, odbiornik ustala z <see cref="JobContext.Current"/> - dlatego wpis z wątku bez
    /// kontekstu (pula wątków, timer uruchomiony poza metodą) nie ma do czego się przypiąć.
    /// </remarks>
    public interface IRunnerLogSink
    {
        /// <summary>
        /// Czy wpis o tej wadze w ogóle ma sens - fałsz, gdy próg wysyłki runnera go odrzuci.
        /// Pozwala modułowi pominąć kosztowne budowanie treści wpisu, którego nikt nie zobaczy.
        /// </summary>
        bool IsEnabled(RunnerLogLevel level);

        /// <summary>Przyjmuje wpis. Nie rzuca - log modułu nie może wywrócić metody.</summary>
        void Write(RunnerLogLevel level, string message);
    }
}
