using Serilog;
using Serilog.Events;
using Zapqio.Runner.Background;

namespace Zapqio.Runner
{
    public class Program
    {
        public static void Main(string[] args)
        {
            var builder = Host.CreateApplicationBuilder(args);
            builder.Services.AddWindowsService();
            builder.Services.AddSystemd();
            builder.Services.AddSingleton(x =>
            {
                var s = new AppSettings();
                builder.Configuration.Bind(s);
                var envKey = builder.Configuration.GetValue<string>("ZAPQIO_TOKEN");
                var envId = builder.Configuration.GetValue<string>("ZAPQIO_NAME");
                var envUrl = builder.Configuration.GetValue<string>("ZAPQIO_URL");
                if (!string.IsNullOrEmpty(envKey))
                {
                    s.Token = envKey;
                }
                if (!string.IsNullOrEmpty(envId))
                {
                    s.Name = envId;
                }
                if (string.IsNullOrEmpty(s.Name))
                {
                    var filePath = Path.Combine(builder.Environment.ContentRootPath, "##Name");
                    if (File.Exists(filePath))
                    {
                        s.Name = File.ReadAllText(filePath);
                    }
                    if (string.IsNullOrEmpty(s.Name))
                    {
                        s.Name = Guid.NewGuid().ToString();
                        File.WriteAllText(filePath, s.Name);
                    }
                }
                if (!string.IsNullOrEmpty(envUrl))
                {
                    s.Url = envUrl;
                }
                return s;
            });
            builder.Services.AddSerilog();
            builder.Services.AddSingleton<ScopedConsole>();
            builder.Services.AddSingleton<MethodsProvider>();
            builder.Services.AddSingleton<WSClient>();
            builder.Services.AddSingleton<LogQueue>();
            builder.Services.AddSingleton<ExecuteJob>();
            builder.Services.AddHostedService<RequestBindBackground>();
            builder.Services.AddHostedService<SendLogsBackground>();

            var host = builder.Build();
            PreRun(host).GetAwaiter().GetResult();
            host.Run();

            static async Task PreRun(IHost host)
            {
                var settings = host.Services.GetRequiredService<AppSettings>();

                if (!Enum.TryParse<LogEventLevel>(settings.Logger.LogLevel, out var logLevel))
                {
                    logLevel = LogEventLevel.Information;
                }
                var template = "[{Timestamp:yyyy-MM-dd HH:mm:ss.fff zzz} {Level:u3} {SourceContext}] {Message:lj}{NewLine}{Exception}";
                var originalConsoleOut = Console.Out; // Zapisz PRZED utworzeniem ScopedConsole
                var log = new LoggerConfiguration()
                .WriteTo.TextWriter(originalConsoleOut, outputTemplate: template)
                .MinimumLevel.Is(logLevel)
                .MinimumLevel.Override("Microsoft.AspNetCore", LogEventLevel.Warning);
                if (!string.IsNullOrEmpty(settings.Logger.PathDirectory))
                {
                    string fullPath = Path.IsPathRooted(settings.Logger.PathDirectory) ? settings.Logger.PathDirectory : Path.Combine(AppContext.BaseDirectory, settings.Logger.PathDirectory);
                    log.WriteTo.File(Path.Combine(fullPath, ".log"), outputTemplate: template, rollOnFileSizeLimit: true, fileSizeLimitBytes: 200 * 1048576, rollingInterval: RollingInterval.Day);
                }
                Log.Logger = log.CreateLogger();
            }
        }
    }
}
