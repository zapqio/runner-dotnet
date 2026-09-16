using Zapqio.Runner.Core;

namespace TestModule.Shared.Extra
{
    /// <summary>
    /// Klasa bazowa metody z paczki współdzielonej. Konsument dziedziczący po niej potrzebuje tego
    /// zestawu już przy GetTypes(), czyli w trakcie skanu, nie dopiero przy Run.
    /// </summary>
    public abstract class SharedBaseMethod : IRunnerMethod
    {
        public abstract string NameMethod();

        public Type InData() => null;

        public Type OutData() => null;

        public abstract Task<string> Run(string data);
    }

    public static class ExtraHelper
    {
        public static string Value() => "extra";
    }
}
