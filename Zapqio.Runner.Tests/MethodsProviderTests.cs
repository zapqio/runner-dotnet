using System.Diagnostics;
using System.IO.Compression;
using Microsoft.Extensions.Logging;

namespace Zapqio.Runner.Tests;

/// <summary>
/// Loader modułów na prawdziwych zestawach. Trzy projekty z Zapqio.Runner.Tests.Modules są budowane do
/// TestModules\ obok binarki testów (nie są referencjonowane - host nie może ich widzieć przez własne
/// probowanie) i pakowane do zipów w katalogu tymczasowym per test.
///
/// Stan AssemblyLoadContext.Default jest globalny w procesie: pierwszy test, który dotknie zestawu,
/// uruchamia rozwiązywanie; każdy kolejny zastaje zestaw już w procesie i żadne zdarzenie nie pada.
/// Asercje są pisane tak, żeby przechodziły w obu przypadkach, także przy uruchomieniu każdego testu osobno.
/// </summary>
public class MethodsProviderTests
{
    private const string Consumer = "TestModule.Consumer.dll";
    private const string Shared = "TestModule.Shared.dll";
    private const string SharedExtra = "TestModule.Shared.Extra.dll";

    [Fact]
    public async Task SharedModule_ResolvesAssembliesForConsumer_RegardlessOfZipOrder()
    {
        var sandbox = new ModuleSandbox();
        // Konsument jest alfabetycznie pierwszy: jego skan potrzebuje zestawów z paczki, która przyjdzie później.
        sandbox.Zip("A-Consumer", new[] { Consumer }, scan: new[] { Consumer }, shared: false);
        sandbox.Zip("Z-Shared", new[] { Shared, SharedExtra }, scan: new[] { Shared }, shared: true);
        var log = new CollectingLogger();

        using var provider = new MethodsProvider(log, sandbox.Modules, sandbox.Cache);

        Assert.Equal(new[] { "Z-Shared" }, provider.SharedDirectories.Select(d => d.Name));
        Assert.True(log.Has(LogLevel.Information, "Moduł współdzielony: Z-Shared"), log.Dump());
        var names = provider.GetMethods().Select(m => m.NameMethod()).ToList();
        Assert.Contains("uses-client", names);
        // Typ pochodny wymaga TestModule.Shared.Extra już przy GetTypes(), a tej biblioteki
        // moduł współdzielony sam nie ładuje (nie ma jej w ##Dll).
        Assert.Contains("derived", names);
        Assert.Equal("hello from shared", await provider.GetMethod("uses-client")!.Run(""));
        Assert.Equal("extra", await provider.GetMethod("derived")!.Run(""));
    }

    [Fact]
    public void MissingSharedModule_SkipsMethodsThatNeedIt_WithoutThrowing()
    {
        var sandbox = new ModuleSandbox();
        sandbox.Zip("A-Consumer", new[] { Consumer }, scan: new[] { Consumer }, shared: false);
        var log = new CollectingLogger();

        using var provider = new MethodsProvider(log, sandbox.Modules, sandbox.Cache);
        var names = provider.GetMethods().Select(m => m.NameMethod()).ToList();

        Assert.Empty(provider.SharedDirectories);
        Assert.DoesNotContain("uses-client", names);
        Assert.Null(provider.GetMethod("uses-client"));
        Assert.True(log.Has(LogLevel.Error, "UsesClientMethod", "nie została utworzona"), log.Dump());
        // "derived" celowo bez asercji: jako pierwszy test w procesie typ nie załaduje się wcale
        // (brak TestModule.Shared.Extra), jako kolejny powstanie, bo zestaw jest już w procesie.
    }

    [Fact]
    public void FailingConstructor_SkipsOnlyThatMethod()
    {
        var sandbox = new ModuleSandbox();
        sandbox.Zip("Consumer", new[] { Consumer }, scan: new[] { Consumer }, shared: false);
        sandbox.Zip("Shared", new[] { Shared, SharedExtra }, scan: new[] { Shared }, shared: true);
        var log = new CollectingLogger();

        using var provider = new MethodsProvider(log, sandbox.Modules, sandbox.Cache);
        var names = provider.GetMethods().Select(m => m.NameMethod()).OrderBy(n => n).ToList();

        Assert.Equal(new[] { "derived", "uses-client" }, names);
        Assert.Null(provider.GetMethod("throwing"));
        Assert.Null(provider.GetMethod("needs-unregistered"));
        Assert.True(log.Has(LogLevel.Error, "ThrowingMethod", "constructor failed on purpose"), log.Dump());
        Assert.True(log.Has(LogLevel.Error, "NeedsUnregisteredMethod", "NotRegistered"), log.Dump());
        Assert.Equal(2, log.Count(LogLevel.Error));
    }

    /// <summary>
    /// Katalog Modules i cache w %TEMP%, zipy budowane z DLL skopiowanych do TestModules\.
    /// Załadowanych DLL nie da się skasować na Windows, więc własny katalog zostaje;
    /// stare katalogi (sprzed startu procesu) sprząta następne uruchomienie testów.
    /// </summary>
    private sealed class ModuleSandbox
    {
        private static readonly string Root = Path.Combine(Path.GetTempPath(), "zapqio-runner-tests", "modules");

        public DirectoryInfo Modules { get; }
        public DirectoryInfo Cache { get; }

        public ModuleSandbox()
        {
            CleanupOld();
            var dir = Path.Combine(Root, Guid.NewGuid().ToString("N"));
            Modules = Directory.CreateDirectory(Path.Combine(dir, "Modules"));
            Cache = Directory.CreateDirectory(Path.Combine(dir, ".modulesCache"));
        }

        /// <summary>Zip z podanymi DLL, plikiem ##Dll (lista do skanowania) i opcjonalnym ##Shared.</summary>
        public void Zip(string name, string[] dlls, string[] scan, bool shared)
        {
            using var zip = ZipFile.Open(Path.Combine(Modules.FullName, name + ".zip"), ZipArchiveMode.Create);
            foreach (var dll in dlls)
            {
                zip.CreateEntryFromFile(Path.Combine(AppContext.BaseDirectory, "TestModules", dll), dll);
            }
            using (var writer = new StreamWriter(zip.CreateEntry("##Dll").Open()))
            {
                foreach (var entry in scan)
                {
                    writer.WriteLine(entry);
                }
            }
            if (shared)
            {
                zip.CreateEntry("##Shared");
            }
        }

        private static void CleanupOld()
        {
            if (!Directory.Exists(Root))
            {
                return;
            }
            var processStart = Process.GetCurrentProcess().StartTime.ToUniversalTime();
            foreach (var old in Directory.EnumerateDirectories(Root))
            {
                if (Directory.GetCreationTimeUtc(old) >= processStart)
                {
                    continue;
                }
                try
                {
                    Directory.Delete(old, true);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    // Nadal zablokowany przez inny proces testów - następnym razem.
                }
            }
        }
    }

    private sealed class CollectingLogger : ILogger<MethodsProvider>
    {
        private readonly List<(LogLevel Level, string Message)> _entries = new();

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            lock (_entries)
            {
                _entries.Add((logLevel, formatter(state, exception)));
            }
        }

        /// <summary>Czy jest wpis danego poziomu zawierający wszystkie fragmenty naraz.</summary>
        public bool Has(LogLevel level, params string[] fragments)
        {
            lock (_entries)
            {
                return _entries.Any(e => e.Level == level && fragments.All(f => e.Message.Contains(f)));
            }
        }

        public int Count(LogLevel level)
        {
            lock (_entries)
            {
                return _entries.Count(e => e.Level == level);
            }
        }

        public string Dump()
        {
            lock (_entries)
            {
                return string.Join(Environment.NewLine, _entries.Select(e => $"[{e.Level}] {e.Message}"));
            }
        }
    }
}
