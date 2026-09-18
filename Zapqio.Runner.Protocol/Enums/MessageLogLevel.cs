namespace Zapqio.Runner.Protocol.Enums
{
    /// <summary>
    /// Waga wpisu logu na łączu (§5.4 protokołu). Na łącze idzie <b>nazwa</b>, nie liczba (§6), ale
    /// wartości liczbowe są częścią kontraktu: <c>Info = 1</c> i <c>Error = 3</c> obowiązują od v1,
    /// a Web trzyma je w tej postaci w historii uruchomień. Nowe poziomy wchodzą więc w wolne miejsca
    /// wokół nich - dzięki temu wpisy zapisane wcześniej znaczą dokładnie to, co znaczyły, i nie ma
    /// czego migrować. Kolejność wartości jest rosnąca według wagi, bo po niej działa próg wysyłki
    /// (<see cref="Zapqio.Runner.AppSettings.MinRemoteLogLevel"/>).
    /// </summary>
    public enum MessageLogLevel
    {
        /// <summary>Szczegóły przydatne przy diagnozie. Domyślnie nie idą na łącze - patrz próg wysyłki. Dodane w v3.</summary>
        Debug = 0,

        /// <summary>Zwykły przebieg. Tu trafia <c>stdout</c> metody i wiersz startowy zadania.</summary>
        Info = 1,

        /// <summary>Coś poszło inaczej, niż powinno, ale zadanie idzie dalej. Dodane w v3.</summary>
        Warning = 2,

        /// <summary>Błąd. Tu trafia <c>stderr</c> metody i złapany wyjątek.</summary>
        Error = 3,

        /// <summary>Błąd, po którym nie ma sensu ciągnąć dalej. Dodane w v3.</summary>
        Critical = 4,
    }
}
