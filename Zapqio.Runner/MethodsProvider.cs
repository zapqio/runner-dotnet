using System.IO.Compression;
using System.Reflection;
using System.Security.Cryptography;
using Zapqio.Runner.Core;

namespace Zapqio.Runner
{
    public class MethodsProvider
    {
        private readonly IServiceProvider _localServiceProvider;
        private readonly ILogger<MethodsProvider> _logger;

        public MethodsProvider(ILogger<MethodsProvider> logger)
        {
            _logger = logger;
            var serviceCollectionModule = new ServiceCollection();
            AddModules(serviceCollectionModule);
            _localServiceProvider = serviceCollectionModule.BuildServiceProvider();
        }

        public IEnumerable<IRunnerMethod> GetMethods()
        {
            return _localServiceProvider.GetServices<IRunnerMethod>();
        }

        public IRunnerMethod? GetMethod(string name)
        {
            return _localServiceProvider.GetServices<IRunnerMethod>().FirstOrDefault(x => name == x.NameMethod());
        }

        #region LoadModules
        private static string HashFile = "##Hash";
        private static string DllFile = "##Dll";
        private static DirectoryInfo DirModules { get; } = new DirectoryInfo(Path.Combine(AppContext.BaseDirectory, "Modules"));
        private static DirectoryInfo DirModulesCache { get; } = new DirectoryInfo(Path.Combine(AppContext.BaseDirectory, ".modulesCache"));
        /// <summary>
        /// Kto czyta katalog modułów - przy usłudze to konto wirtualne <c>NT SERVICE\...</c>, nie
        /// użytkownik, który wgrał paczki. Wpis w logu oszczędza zgadywania, gdy zawodzą uprawnienia.
        /// </summary>
        private static string RunningAs => $@"{Environment.UserDomainName}\{Environment.UserName}";

        private List<DirectoryInfo> CheckCacheOrCreate()
        {
            var dirModules = new List<DirectoryInfo>();
            _logger.LogInformation("Katalog modułów: {Dir} (konto: {User})", DirModules.FullName, RunningAs);

            List<FileInfo> zips;
            try
            {
                if (!DirModules.Exists)
                {
                    DirModules.Create();
                }
                if (!DirModulesCache.Exists)
                {
                    DirModulesCache.Create();
                }
                zips = DirModules.EnumerateFiles("*.zip").ToList();
            }
            catch (Exception ex) when (ex is UnauthorizedAccessException or IOException)
            {
                // Bez dostępu do katalogów nie ma modułów, ale runner ma wystartować i połączyć się z
                // platformą - wtedy ten błąd trafi do logu, zamiast ubić usługę przed pierwszym wpisem.
                _logger.LogError(ex,
                    "Brak dostępu do katalogu modułów {Dir} albo cache {Cache} dla konta {User} - sprawdź uprawnienia (icacls). Runner startuje bez metod",
                    DirModules.FullName, DirModulesCache.FullName, RunningAs);
                return dirModules;
            }

            if (zips.Count == 0)
            {
                _logger.LogWarning("Brak modułów: w {Dir} nie ma żadnej paczki .zip", DirModules.FullName);
                return dirModules;
            }
            _logger.LogInformation("Znaleziono {Count} paczek modułów: {Zips}", zips.Count, string.Join(", ", zips.Select(z => z.Name)));

            foreach (var file in zips)
            {
                try
                {
                    using var stream = file.OpenRead();
                    var cacheDir = DirModulesCache.CreateSubdirectory(file.Name.Replace(file.Extension, string.Empty));
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
            // sprawdzenie czy istnieje plik #DLL okre�laj�cy list� bibliotek do za�adowania
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


        public IServiceCollection AddModules(IServiceCollection buildier)
        {
            var modules = 0;
            var methods = 0;
            var injections = 0;
            foreach (var dir in CheckCacheOrCreate())
            {
                modules++;
                var (listDll, readIsFile) = GetListDll(dir);
                var listLoaded = new List<string>();
                foreach (var dll in listDll)
                {
                    Assembly assembly = null;
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
                        // aplikacji (WDAC / Smart App Control) i brak prawa odczytu dla konta usługi.
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
                    foreach (var type in types) // �adowanie wstrzykni��
                    {
                        if (type.IsClass && !type.IsAbstract && type.IsPublic && type.IsAssignableTo(typeof(IRunnerInjection)))
                        {
                            _logger.LogInformation($"Add injection: {type.FullName} in module: {dir.Name}");
                            buildier.AddSingleton(type);
                            listLoaded.Add(dll.Name);
                            injections++;
                        }
                    }
                    foreach (var type in types) // �adownie metod
                    {
                        if (type.IsClass && !type.IsAbstract && type.IsPublic && type.IsAssignableTo(typeof(IRunnerMethod)))
                        {
                            _logger.LogInformation($"Add method: {type.FullName} in module: {dir.Name}");
                            buildier.AddSingleton(typeof(IRunnerMethod), type);
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
                    _logger.LogWarning(
                        "Moduł {Module} nie dostarczył żadnej metody ani wstrzyknięcia (sprawdzono {Count} DLL)",
                        dir.Name, listDll.Count);
                }
            }
            _logger.LogInformation(
                "Załadowano {Methods} metod i {Injections} wstrzyknięć z {Modules} modułów",
                methods, injections, modules);
            return buildier;
        }
        #endregion
    }
}
