using Zapqio.Runner.Core;

namespace TestModule.Shared
{
    /// <summary>
    /// Odpowiednik NexoClient: singleton z paczki współdzielonej, wstrzykiwany do metod z innych paczek.
    /// </summary>
    public class SharedClient : IRunnerInjection
    {
        public string Hello() => "hello from shared";
    }
}
