namespace Zapqio.Runner
{
    public class AppSettings
    {
        public LoggerClass Logger { get; set; } = new();
        public string Token { get; set; }
        public string Name { get; set; }
        public string Url { get; set; }

        /// <summary>
        /// Ile zadań runner wykonuje naraz. Domyślnie 1, czyli dotychczasowe zachowanie: jedno po
        /// drugim. Wyższa wartość ma sens wyłącznie dla modułów gotowych na równoległe wywołania
        /// <c>Run</c> (także tej samej metody na tej samej instancji) - runner nie dodaje żadnej
        /// synchronizacji, o tym decyduje twórca modułu. To jedyne miejsce, w którym pojemność się
        /// ustawia: runner ogłasza ją platformie w <c>Info</c> (§5.1 protokołu), a platforma wysyła
        /// najwyżej tyle zadań naraz (i może przyciąć od góry, dziś do 32).
        /// Zmienna środowiskowa: <c>ZAPQIO_MAX_CONCURRENCY</c>.
        /// </summary>
        public int MaxConcurrency { get; set; } = 1;

        /// <summary>
        /// Ile sekund przy zatrzymaniu usługi runner czeka na zadania w toku i wysyłkę ich wyników,
        /// zanim zamknie gniazdo. Po tym czasie zadania są porzucane i platforma zamknie je jako
        /// „wynik nieznany". Zero = bez czekania.
        /// </summary>
        public int StopTimeoutSeconds { get; set; } = 30;

        /// <summary>
        /// Ile linii logu zadań może czekać na wysyłkę, gdy platforma jest niedostępna. Ponad limit
        /// kolejne linie są pomijane (z jedną linią ostrzegawczą w logu zadania), żeby odcięte Web nie
        /// rozdęło pamięci runnera bez końca.
        /// </summary>
        public int MaxQueuedLogLines { get; set; } = 10_000;

        /// <summary>Sprowadza wartości bez sensu do dopuszczalnych; wołane raz, po zbudowaniu ustawień.</summary>
        public void Normalize()
        {
            if (MaxConcurrency < 1) MaxConcurrency = 1;
            if (StopTimeoutSeconds < 0) StopTimeoutSeconds = 0;
            if (MaxQueuedLogLines < 100) MaxQueuedLogLines = 100;
        }

        public class LoggerClass
        {
            public string LogLevel { get; set; }
            public string PathDirectory { get; set; }
        }
    }

}
