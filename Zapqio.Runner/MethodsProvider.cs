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
        private List<DirectoryInfo> CheckCacheOrCreate()
        {
            var dirModules = new List<DirectoryInfo>();
            if (!DirModules.Exists)
            {
                DirModules.Create();
            }
            if (!DirModulesCache.Exists)
            {
                DirModulesCache.Create();
            }
            foreach (var file in DirModules.EnumerateFiles("*.zip"))
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
            return dirModules;
        }

        private (ICollection<FileInfo>, bool) GetListDll(DirectoryInfo directoryInfo)
        {
            // sprawdzenie czy istnieje plik #DLL okreœlaj¹cy listê bibliotek do za³adowania
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
            foreach (var dir in CheckCacheOrCreate())
            {
                var (listDll, readIsFile) = GetListDll(dir);
                var listLoaded = new List<string>();
                foreach (var dll in listDll)
                {
                    Assembly assembly = null;
                    try
                    {
                        assembly = Assembly.LoadFrom(dll.FullName);
                    }
                    catch (Exception ex) when (ex is FileNotFoundException or BadImageFormatException or FileLoadException)
                    {
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
                    }
                    foreach (var type in types) // £adowanie wstrzykniêæ
                    {
                        if (type.IsClass && !type.IsAbstract && type.IsPublic && type.IsAssignableTo(typeof(IRunnerInjection)))
                        {
                            _logger.LogInformation($"Add injection: {type.FullName} in module: {dir.Name}");
                            buildier.AddSingleton(type);
                            listLoaded.Add(dll.Name);
                        }
                    }
                    foreach (var type in types) // £adownie metod
                    {
                        if (type.IsClass && !type.IsAbstract && type.IsPublic && type.IsAssignableTo(typeof(IRunnerMethod)))
                        {
                            _logger.LogInformation($"Add method: {type.FullName} in module: {dir.Name}");
                            buildier.AddSingleton(typeof(IRunnerMethod), type);
                            listLoaded.Add(dll.Name);
                        }
                    }
                }
                if (!readIsFile && listLoaded.Any())
                {
                    File.WriteAllLines(Path.Combine(dir.FullName, DllFile), listLoaded.Distinct());
                }
            }
            return buildier;
        }
        #endregion
    }
}
