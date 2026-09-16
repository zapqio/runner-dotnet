using System.IO.Compression;
using System.Reflection;
using System.Security.Cryptography;
using Zapqio.Runner.Core;

namespace Zapqio.Runner
{
    /// <summary>
    /// Ładuje moduły z paczek .zip i buduje z nich kontener metod. Jeden na proces (singleton hosta),
    /// bo rejestruje w domenie aplikacji handler rozwiązywania zestawów z paczek współdzielonych.
    /// </summary>
    public class MethodsProvider : IDisposable
    {
        private readonly IServiceProvider _localServiceProvider;
        private readonly ILogger<MethodsProvider> _logger;
        private readonly DirectoryInfo _dirModules;
        private readonly DirectoryInfo _dirModulesCache;
        private readonly DirectoryInfo? _dirConfig;
        private readonly List<DirectoryInfo> _sharedDirs = new();
        private readonly List<(Type Type, string Module)> _methodTypes = new();
        private readonly Lazy<IReadOnlyList<IRunnerMethod>> _methods;
        private bool _disposed;

        public MethodsProvider(ILogger<MethodsProvider> logger)
            : this(logger, DirModules, DirModulesCache, DirConfig)
        {
        }

        /// <summary>
        /// Wariant z jawnymi katalogami - dla testów. Host używa domyślnych, obok binarki.
        /// <paramref name="config"/> to katalog na konfigurację modułów (runner go tylko zakłada).
        /// </summary>
        public MethodsProvider(ILogger<MethodsProvider> logger, DirectoryInfo modules, DirectoryInfo cache, DirectoryInfo? config = null)
        {
            _logger = logger;
            _dirModules = modules;
            _dirModulesCache = cache;
            _dirConfig = config;
            // Przed pierwszym LoadFrom: skan konsumenta może potrzebować zestawu z paczki współdzielonej
            // już przy GetTypes(), nie dopiero przy wywołaniu metody.
            AppDomain.CurrentDomain.AssemblyResolve += ResolveFromSharedModules;
            var serviceCollectionModule = new ServiceCollection();
            AddModules(serviceCollectionModule);
            _localServiceProvider = serviceCollectionModule.BuildServiceProvider();
            _methods = new Lazy<IReadOnlyList<IRunnerMethod>>(CreateMethods, LazyThreadSafetyMode.ExecutionAndPublication);
        }

        /// <summary>
        /// Katalogi paczek oznaczonych plikiem <c>##Shared</c>, w kolejności nazw paczek.
        /// </summary>
        public IReadOnlyList<DirectoryInfo> SharedDirectories => _sharedDirs;

        public IEnumerable<IRunnerMethod> GetMethods()
        {
            return _methods.Value;
        }

        public IRunnerMethod? GetMethod(string name)
        {
            return _methods.Value.FirstOrDefault(x => name == x.NameMethod());
        }

        /// <summary>
        /// Metody powstają pojedynczo, przy pierwszym użyciu. Wcześniej jeden rzucający konstruktor
        /// (np. brak wstrzyknięcia, bo paczki współdzielonej nie ma w Modules) wywracał całe
        /// <c>GetServices&lt;IRunnerMethod&gt;()</c> w SendInfo i runner łączył się w kółko bez metod.
        /// </summary>
        private IReadOnlyList<IRunnerMethod> CreateMethods()
        {
            var list = new List<IRunnerMethod>(_methodTypes.Count);
            foreach (var (type, module) in _methodTypes)
            {
                try
                {
                    list.Add((IRunnerMethod)_localServiceProvider.GetRequiredService(type));
                }
                catch (Exception ex)
                {
                    // Metoda jest wyłączona do restartu; pozostałe działają normalnie.
                    _logger.LogError(ex,
                        "Metoda {Type} z modułu {Module} nie została utworzona i nie będzie ogłoszona: {Reason}",
                        type.FullName, module, ex.Message);
                }
            }
            return list;
        }

        public void Dispose()
        {
            if (_disposed)
            {
                return;
            }
            _disposed = true;
            AppDomain.CurrentDomain.AssemblyResolve -= ResolveFromSharedModules;
            // Kontener modułów nie jest zwalniany - jak dotąd, moduły żyją do końca procesu.
        }

        #region LoadModules
        private const string HashFile = "##Hash";
        private const string DllFile = "##Dll";
        private const string SharedFile = "##Shared";
        private static DirectoryInfo DirModules { get; } = new DirectoryInfo(Path.Combine(AppContext.BaseDirectory, "Modules"));
        private static DirectoryInfo DirModulesCache { get; } = new DirectoryInfo(Path.Combine(AppContext.BaseDirectory, ".modulesCache"));
        /// <summary>
        /// Konfiguracja modułów: każdy trzyma tam własny plik (np. Config\nexoModule.json), który przeżywa
        /// podmianę zipów. Runner nic z niego nie czyta, tylko zakłada katalog; install.ps1 nadaje mu ACL
        /// jak appsettings.json, bo moduły trzymają tam hasła.
        /// </summary>
        private static DirectoryInfo DirConfig { get; } = new DirectoryInfo(Path.Combine(AppContext.BaseDirectory, "Config"));
        /// <summary>
        /// Kto czyta katalog modułów - przy usłudze to konto wirtualne <c>NT SERVICE\...</c>, nie
        /// użytkownik, który wgrał paczki. Wpis w logu oszczędza zgadywania, gdy zawodzą uprawnienia.
        /// </summary>
        private static string RunningAs => $@"{Environment.UserDomainName}\{Environment.UserName}";

        private List<DirectoryInfo> CheckCacheOrCreate()
        {
            var dirModules = new List<DirectoryInfo>();
            _logger.LogInformation("Katalog modułów: {Dir} (konto: {User})", _dirModules.FullName, RunningAs);

            List<FileInfo> zips;
            try
            {
                if (!_dirModules.Exists)
                {
                    _dirModules.Create();
                }
                if (!_dirModulesCache.Exists)
                {
                    _dirModulesCache.Create();
                }
                if (_dirConfig is { Exists: false })
                {
                    _dirConfig.Create();
                }
                // Kolejność jawna, nie z systemu plików (NTFS zwraca alfabetycznie, ext4 nie): to zarazem
                // kolejność probowania katalogów paczek współdzielonych.
                zips = _dirModules.EnumerateFiles("*.zip").OrderBy(f => f.Name, StringComparer.OrdinalIgnoreCase).ToList();
            }
            catch (Exception ex) when (ex is UnauthorizedAccessException or IOException)
            {
                // Bez dostępu do katalogów nie ma modułów, ale runner ma wystartować i połączyć się z
                // platformą - wtedy ten błąd trafi do logu, zamiast ubić usługę przed pierwszym wpisem.
                _logger.LogError(ex,
                    "Brak dostępu do katalogu modułów {Dir} albo cache {Cache} dla konta {User} - sprawdź uprawnienia (icacls). Runner startuje bez metod",
                    _dirModules.FullName, _dirModulesCache.FullName, RunningAs);
                return dirModules;
            }

            if (zips.Count == 0)
            {
                _logger.LogWarning("Brak modułów: w {Dir} nie ma żadnej paczki .zip", _dirModules.FullName);
                return dirModules;
            }
            _logger.LogInformation("Znaleziono {Count} paczek modułów: {Zips}", zips.Count, string.Join(", ", zips.Select(z => z.Name)));

            foreach (var file in zips)
            {
                try
                {
                    using var stream = file.OpenRead();
                    var cacheDir = _dirModulesCache.CreateSubdirectory(file.Name.Replace(file.Extension, string.Empty));
                    var hashFile = new FileInfo(Path.Combine(cacheDir.FullName, HashFile));
                    stream.Position = 0;
                    var hashZip = SHA256.HashData(stream);
                    if (hashFile.Exists)
                    {
                        using var sr = hashFile.OpenRead();
                        var hashCache = new byte[sr.Length];
                        sr.ReadExactly(hashCache, 0, hashCache.Length);
                        if (Enumerable.SequenceEqual(hashCache, hashZip))
                        {
                            dirModules.Add(cacheDir);
                            continue;
                        }
                    }
                    cacheDir.Delete(true);
                    ZipFile.ExtractToDirectory(stream, cacheDir.FullName);
                    using var s = hashFile.Create();
                    s.Write(hashZip);

                    _logger.LogDebug($"Unppack module: {cacheDir.Name}");
                    dirModules.Add(cacheDir);
                }
                catch (Exception ex) when (ex is UnauthorizedAccessException or IOException or InvalidDataException)
                {
                    // Jedna zepsuta albo nieczytelna paczka nie ma blokować pozostałych.
                    _logger.LogError(ex,
                        "Nie udało się przygotować modułu {Zip} (konto: {User}) - pomijam",
                        file.FullName, RunningAs);
                }
            }
            return dirModules;
        }

        private (ICollection<FileInfo>, bool) GetListDll(DirectoryInfo directoryInfo)
        {
            // Plik ##Dll określa listę bibliotek do załadowania; bez niego skanowane są wszystkie DLL z paczki.
            var pluginsFile = directoryInfo.GetFiles(DllFile).FirstOrDefault();
            if (pluginsFile != null)
            {
                var listdll = new List<FileInfo>();
                using var sr = pluginsFile.OpenText();
                var text = sr.ReadToEnd();
                var listDllNames = text.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries).Select(x => x.Trim()).Distinct();
                foreach (var dllName in listDllNames)
                {
                    var dll = directoryInfo.GetFiles(dllName).FirstOrDefault();
                    if (dll == null)
                    {
                        _logger.LogWarning($"File {dllName} not found in module: {directoryInfo.Name}");
                        continue;
                    }
                    listdll.Add(dll);
                }
                return (listdll, true);
            }
            return (directoryInfo.EnumerateFiles("*.dll").ToList(), false);
        }

        /// <summary>
        /// Rozwiązywanie zestawów, których nie ma w katalogu modułu proszącego: najpierw jego własny
        /// katalog (ta sama semantyka co LoadFrom, więc kolejność handlerów nie ma znaczenia i własny
        /// katalog zawsze wygrywa), potem po kolei katalogi paczek <c>##Shared</c>. Zdarzenie
        /// AssemblyResolve, a nie AssemblyLoadContext.Resolving, bo tylko ono niesie zestaw proszący.
        /// </summary>
        private Assembly? ResolveFromSharedModules(object? sender, ResolveEventArgs args)
        {
            if (_sharedDirs.Count == 0)
            {
                return null;
            }
            var fileName = new AssemblyName(args.Name).Name + ".dll";
            var requestor = args.RequestingAssembly;
            // Location rzuca dla zestawów dynamicznych.
            if (requestor is { IsDynamic: false } && !string.IsNullOrEmpty(requestor.Location))
            {
                var own = Path.Combine(Path.GetDirectoryName(requestor.Location)!, fileName);
                if (File.Exists(own) && TryLoad(own) is { } fromOwnDir)
                {
                    return fromOwnDir;
                }
            }
            foreach (var dir in _sharedDirs)
            {
                var path = Path.Combine(dir.FullName, fileName);
                if (!File.Exists(path))
                {
                    continue;
                }
                if (TryLoad(path) is { } assembly)
                {
                    _logger.LogDebug("Zestaw {Name} dla {Requestor} rozwiązany z modułu współdzielonego {Module}",
                        fileName, requestor?.GetName().Name ?? "?", dir.Name);
                    return assembly;
                }
            }
            // Brak trafienia bez logu: zdarzenie pada też dla zasobów satelickich i zestawów opcjonalnych.
            return null;
        }

        private Assembly? TryLoad(string path)
        {
            try
            {
                // LoadFrom, nie LoadFromAssemblyPath: zestaw z katalogu współdzielonego ma sam rozwiązywać
                // swoje zależności z tego katalogu, także po odpięciu tego handlera.
                return Assembly.LoadFrom(path);
            }
            catch (Exception ex) when (ex is FileNotFoundException or FileLoadException or BadImageFormatException)
            {
                _logger.LogDebug("Nie udało się załadować {Path} przy rozwiązywaniu zestawu: {Reason}", path, ex.Message);
                return null;
            }
        }

        public IServiceCollection AddModules(IServiceCollection buildier)
        {
            var modules = 0;
            var methods = 0;
            var injections = 0;
            var registered = new HashSet<Type>();
            var dirs = CheckCacheOrCreate();

            // Przebieg 1: katalogi paczek współdzielonych, zanim jakikolwiek skan zacznie wiązać zestawy -
            // dzięki temu kolejność paczek nie ma znaczenia.
            foreach (var dir in dirs)
            {
                if (File.Exists(Path.Combine(dir.FullName, SharedFile)))
                {
                    _sharedDirs.Add(dir);
                    _logger.LogInformation(
                        "Moduł współdzielony: {Module} - jego katalog służy do rozwiązywania zestawów innych modułów",
                        dir.Name);
                }
            }

            // Przebieg 2: skan wszystkich paczek, współdzielonych też.
            foreach (var dir in dirs)
            {
                modules++;
                var (listDll, readIsFile) = GetListDll(dir);
                var listLoaded = new List<string>();
                foreach (var dll in listDll)
                {
                    Assembly assembly;
                    try
                    {
                        assembly = Assembly.LoadFrom(dll.FullName);
                    }
                    catch (BadImageFormatException)
                    {
                        // Natywna biblioteka albo DLL spoza .NET - w paczkach modułów to normalne.
                        _logger.LogDebug("Pomijam {Dll} z modułu {Module}: to nie jest zestaw .NET", dll.Name, dir.Name);
                        continue;
                    }
                    catch (Exception ex) when (ex is FileNotFoundException or FileLoadException or UnauthorizedAccessException)
                    {
                        // Wcześniej połykane bez śladu - a tu ląduje m.in. blokada przez politykę kontroli
                        // aplikacji (WDAC / Smart App Control), brak prawa odczytu dla konta usługi
                        // i inna binarka pod nazwą zestawu, który jest już załadowany z innej paczki.
                        _logger.LogWarning(ex,
                            "Nie udało się załadować {Dll} z modułu {Module} (konto: {User}): {Reason}",
                            dll.FullName, dir.Name, RunningAs, ex.Message);
                        continue;
                    }
                    Type[] types;
                    _logger.LogDebug($"Check dll: {dll.Name} in module: {dir.Name}");
                    try
                    {
                        types = assembly.GetTypes();
                    }
                    catch (ReflectionTypeLoadException ex)
                    {
                        types = ex.Types.Where(t => t != null).ToArray()!;
                        var reasons = ex.LoaderExceptions
                            .Where(e => e != null)
                            .Select(e => e!.Message)
                            .Distinct()
                            .Take(5);
                        // Typowy powód: moduł zbudowany pod inną wersję .NET albo brak zależności w paczce.
                        _logger.LogWarning(
                            "Część typów z {Dll} (moduł {Module}) nie dała się załadować: {Reasons}",
                            dll.Name, dir.Name, string.Join(" | ", reasons));
                    }
                    foreach (var type in types) // ładowanie wstrzyknięć
                    {
                        if (IsPublicConcrete(type) && type.IsAssignableTo(typeof(IRunnerInjection)) && registered.Add(type))
                        {
                            _logger.LogInformation($"Add injection: {type.FullName} in module: {dir.Name}");
                            buildier.AddSingleton(type);
                            listLoaded.Add(dll.Name);
                            injections++;
                        }
                    }
                    foreach (var type in types) // ładowanie metod
                    {
                        if (IsPublicConcrete(type) && type.IsAssignableTo(typeof(IRunnerMethod)) && _methodTypes.All(m => m.Type != type))
                        {
                            // Typ będący naraz wstrzyknięciem i metodą jest w kontenerze raz, jako on sam;
                            // lista metod trzyma typy, a nie rejestrację pod IRunnerMethod, żeby móc
                            // tworzyć je pojedynczo (CreateMethods).
                            if (registered.Add(type))
                            {
                                buildier.AddSingleton(type);
                            }
                            _logger.LogInformation($"Add method: {type.FullName} in module: {dir.Name}");
                            _methodTypes.Add((type, dir.Name));
                            listLoaded.Add(dll.Name);
                            methods++;
                        }
                    }
                }
                if (!readIsFile && listLoaded.Any())
                {
                    File.WriteAllLines(Path.Combine(dir.FullName, DllFile), listLoaded.Distinct());
                }
                else if (!listLoaded.Any())
                {
                    if (_sharedDirs.Contains(dir))
                    {
                        // Paczka z samymi bibliotekami (np. SDK) to poprawny przypadek modułu współdzielonego.
                        _logger.LogInformation(
                            "Moduł współdzielony {Module} nie dostarczył żadnej metody ani wstrzyknięcia - udostępnia tylko biblioteki",
                            dir.Name);
                    }
                    else
                    {
                        _logger.LogWarning(
                            "Moduł {Module} nie dostarczył żadnej metody ani wstrzyknięcia (sprawdzono {Count} DLL)",
                            dir.Name, listDll.Count);
                    }
                }
            }
            _logger.LogInformation(
                "Załadowano {Methods} metod i {Injections} wstrzyknięć z {Modules} modułów",
                methods, injections, modules);
            return buildier;
        }

        private static bool IsPublicConcrete(Type type) => type.IsClass && !type.IsAbstract && type.IsPublic;
        #endregion
    }
}
