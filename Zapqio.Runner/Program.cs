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
                var envMaxConcurrency = builder.Configuration.GetValue<string>("ZAPQIO_MAX_CONCURRENCY");
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
                if (int.TryParse(envMaxConcurrency, out var maxConcurrency))
                {
                    s.MaxConcurrency = maxConcurrency;
                }
                s.Normalize();
                return s;
            });
            builder.Services.AddSerilog();

            // Kolejka wyjściowa i jej jedyny autor. Wszystko, co idzie do platformy, przechodzi tędy.
            builder.Services.AddSingleton(sp => new Outbox(sp.GetRequiredService<AppSettings>().MaxQueuedLogLines));
            builder.Services.AddSingleton<ScopedConsole>();
            builder.Services.AddSingleton<MethodsProvider>();
            builder.Services.AddSingleton<WSClient>();
            builder.Services.AddSingleton<RunnerProcessState>();
            builder.Services.AddSingleton<IOutboundTransport>(sp => sp.GetRequiredService<WSClient>());
            builder.Services.AddSingleton<OutboundSender>();
            builder.Services.AddSingleton<PendingJobReturns>();
            builder.Services.AddSingleton<ExecuteJob>();
            builder.Services.AddSingleton(sp =>
            {
                var settings = sp.GetRequiredService<AppSettings>();
                var client = sp.GetRequiredService<WSClient>();
                var execute = sp.GetRequiredService<ExecuteJob>();
                return new JobScheduler(
                    settings.MaxConcurrency,
                    execute.Exec,
                    job => client.SendJobAccepted(job.Id, job.AttemptId),
                    client.SendQueryOnJob,
                    sp.GetRequiredService<ILogger<JobScheduler>>(),
                    sp.GetRequiredService<RunnerProcessState>());
            });

            // Kolejność rejestracji to odwrotność kolejności zatrzymania: pętla połączenia staje
            // pierwsza i czeka na zadania w toku oraz wysyłkę ich wyników, planista drugi, a nadawca
            // gniazda żyje najdłużej - inaczej wyniki nie miałyby czym wyjść.
            builder.Services.AddHostedService(sp => sp.GetRequiredService<OutboundSender>());
            builder.Services.AddHostedService<JobSchedulerHost>();
            builder.Services.AddHostedService<RequestBindBackground>();

            // Host ma dać pętli połączenia czas na łagodne zatrzymanie (StopTimeoutSeconds) z zapasem
            // na uzgodnienie zamknięcia gniazda; domyślne 30 s hosta ucięłoby dłuższe oczekiwanie.
            builder.Services.AddOptions<HostOptions>().Configure<AppSettings>((options, settings) =>
                options.ShutdownTimeout = TimeSpan.FromSeconds(settings.StopTimeoutSeconds + 15));

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
