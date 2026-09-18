namespace Zapqio.Runner.Core
{
    /// <summary>
    /// Waga wpisu, który moduł wysyła przez <see cref="RunnerLog"/>. Runner tłumaczy ją na poziom
    /// protokołu i wysyła do platformy; w panelu wpis pokazuje się z tą wagą przy zadaniu.
    /// </summary>
    /// <remarks>
    /// Wartości liczbowe są celowo takie same jak w protokole - <c>Info = 1</c> i <c>Error = 3</c>
    /// obowiązują tam od pierwszej wersji, a <see cref="Debug"/>, <see cref="Warning"/> i
    /// <see cref="Critical"/> wchodzą w wolne miejsca wokół nich. Kolejność jest rosnąca według wagi,
    /// bo po niej działa próg wysyłki ustawiony na runnerze: poniżej progu wpis nie idzie na łącze.
    /// <see cref="Debug"/> domyślnie jest poniżej - to poziom, który włącza się na czas diagnozy.
    /// </remarks>
    public enum RunnerLogLevel
    {
        /// <summary>Szczegóły przydatne przy diagnozie. Domyślnie NIE idzie na łącze.</summary>
        Debug = 0,

        /// <summary>Zwykły przebieg. To samo, co zwykłe <c>Console.WriteLine</c>.</summary>
        Info = 1,

        /// <summary>Coś poszło inaczej, niż powinno, ale metoda idzie dalej.</summary>
        Warning = 2,

        /// <summary>Błąd. To samo, co zapis na <c>Console.Error</c>.</summary>
        Error = 3,

        /// <summary>Błąd, po którym nie ma sensu ciągnąć dalej.</summary>
        Critical = 4,
    }
}
