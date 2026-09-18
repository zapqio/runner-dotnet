using System.Text.Json;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Zapqio.Runner.Core;
using Zapqio.Runner.Protocol;
using Zapqio.Runner.Protocol.Enums;

namespace Zapqio.Runner.Tests;

/// <summary>
/// Trzy kanały logu modułu - <c>stdout</c>/<c>stderr</c>, statyczny <c>RunnerLog</c> i wstrzyknięty
/// <c>ILogger</c> - mają kończyć w jednym miejscu, z tym samym progiem i tym samym przypisaniem wpisu
/// do zadania. Osobno pilnowany jest warunek, o który najłatwiej się potknąć: próg NIE MOŻE dotyczyć
/// wiersza startowego zadania, bo to on przestawia zadanie na <i>Executing</i> (§5.4 protokołu).
/// </summary>
public class JobLogWriterTests
{
    private static (Outbox Outbox, JobLogWriter Writer) Build(string threshold = "Info")
    {
        var settings = new AppSettings { MinRemoteLogLevel = threshold };
        settings.Normalize();
        var outbox = new Outbox(1000);
        return (outbox, new JobLogWriter(outbox, settings, NullLoggerFactory.Instance));
    }

    /// <summary>Wpisy, które faktycznie trafiły do kolejki wyjściowej, w kolejności.</summary>
    private static List<MessageLog> Drain(Outbox outbox)
    {
        var lines = new List<MessageLog>();
        while (outbox.Reader.TryRead(out var item))
        {
            Assert.Equal(MessageType.Log, item.Message.Type);
            lines.Add(JsonSerializer.Deserialize<MessageLog>(item.Message.Data, JsonDefaults.Options)!);
        }
        return lines;
    }

    private static MessageJob Job(string name = "resize-image") => new()
    {
        Id = Guid.NewGuid(),
        AttemptId = Guid.NewGuid(),
        Name = name,
        Data = "{}"
    };

    [Theory]
    [InlineData(MessageLogLevel.Debug)]
    [InlineData(MessageLogLevel.Info)]
    [InlineData(MessageLogLevel.Warning)]
    [InlineData(MessageLogLevel.Error)]
    [InlineData(MessageLogLevel.Critical)]
    public void Every_level_reaches_the_outbox_when_the_threshold_allows_it(MessageLogLevel level)
    {
        var (outbox, writer) = Build(threshold: nameof(MessageLogLevel.Debug));
        var job = Job();

        writer.Write(job.Id, job.AttemptId, job.Name, level, "tekst");

        var line = Assert.Single(Drain(outbox));
        Assert.Equal(level, line.Level);
        Assert.Equal(job.Id, line.JobId);
        Assert.Equal(job.AttemptId, line.AttemptId);
        Assert.Equal("tekst", line.Message);
    }

    [Fact]
    public void Debug_stays_home_by_default()
    {
        var (outbox, writer) = Build();
        var job = Job();

        writer.Write(job.Id, job.AttemptId, job.Name, MessageLogLevel.Debug, "szczegół");
        writer.Write(job.Id, job.AttemptId, job.Name, MessageLogLevel.Info, "przebieg");

        var line = Assert.Single(Drain(outbox));
        Assert.Equal(MessageLogLevel.Info, line.Level);
    }

    [Fact]
    public void A_raised_threshold_also_silences_stdout()
    {
        var (outbox, writer) = Build(threshold: nameof(MessageLogLevel.Warning));
        var job = Job();

        writer.Write(job.Id, job.AttemptId, job.Name, MessageLogLevel.Info, "z Console.WriteLine");
        writer.Write(job.Id, job.AttemptId, job.Name, MessageLogLevel.Warning, "ostrzeżenie");

        var line = Assert.Single(Drain(outbox));
        Assert.Equal(MessageLogLevel.Warning, line.Level);
    }

    [Fact]
    public void A_nonsense_threshold_falls_back_to_Info_instead_of_silencing_the_runner()
    {
        var settings = new AppSettings { MinRemoteLogLevel = "Verbose" };
        settings.Normalize();

        Assert.Equal(MessageLogLevel.Info, settings.RemoteLogThreshold);
        Assert.Equal(nameof(MessageLogLevel.Info), settings.MinRemoteLogLevel);
    }

    /// <summary>
    /// Wiersz startowy idzie przez <see cref="Outbox.SendAndWaitAsync"/>, a nie przez
    /// <see cref="JobLogWriter"/>, więc nawet najwyższy próg go nie zatrzyma. Gdyby zatrzymał,
    /// <see cref="ExecuteJob"/> odwołałby wykonanie i runner przestałby robić cokolwiek.
    /// </summary>
    [Fact]
    public void The_start_line_ignores_the_threshold()
    {
        var (outbox, writer) = Build(threshold: nameof(MessageLogLevel.Critical));
        var job = Job();

        writer.Write(job.Id, job.AttemptId, job.Name, MessageLogLevel.Info, "wpis modułu");
        Assert.Empty(Drain(outbox));

        var startLine = outbox.SendAndWaitAsync(Outbox.Frame(MessageType.Log, new MessageLog
        {
            Date = DateTimeOffset.Now,
            JobId = job.Id,
            AttemptId = job.AttemptId,
            Level = MessageLogLevel.Info,
            Message = "Run Job"
        }));

        var queued = Assert.Single(Drain(outbox));
        Assert.Equal(MessageLogLevel.Info, queued.Level);
        Assert.Equal("Run Job", queued.Message);
        Assert.False(startLine.IsCompleted, "wiersz startowy czeka na potwierdzenie zapisu, a nie na próg");
    }

    [Fact]
    public void Without_a_job_context_nothing_goes_on_the_wire()
    {
        var (outbox, writer) = Build();

        // Moduł logujący z konstruktora albo z wątku spoza zadania: nie ma zadania, do którego wpis
        // mógłby należeć. Nie wysyłamy go donikąd, ale i nie wywracamy się na tym.
        writer.Write(MessageLogLevel.Error, "z konstruktora modułu");

        Assert.Empty(Drain(outbox));
    }

    [Fact]
    public void The_ambient_context_decides_which_job_owns_the_entry()
    {
        var (outbox, writer) = Build();
        var jobId = Guid.NewGuid();
        var attemptId = Guid.NewGuid();

        using (JobContext.Begin(new JobContext(jobId, attemptId, "resize-image")))
        {
            writer.Write(MessageLogLevel.Warning, "z metody");
        }

        var line = Assert.Single(Drain(outbox));
        Assert.Equal(jobId, line.JobId);
        Assert.Equal(attemptId, line.AttemptId);
        Assert.Equal(MessageLogLevel.Warning, line.Level);
    }

    [Theory]
    [InlineData(RunnerLogLevel.Debug, MessageLogLevel.Debug)]
    [InlineData(RunnerLogLevel.Info, MessageLogLevel.Info)]
    [InlineData(RunnerLogLevel.Warning, MessageLogLevel.Warning)]
    [InlineData(RunnerLogLevel.Error, MessageLogLevel.Error)]
    [InlineData(RunnerLogLevel.Critical, MessageLogLevel.Critical)]
    public void RunnerLog_carries_the_level_the_module_named(RunnerLogLevel named, MessageLogLevel onTheWire)
    {
        var (outbox, writer) = Build(threshold: nameof(MessageLogLevel.Debug));
        using var sink = RunnerLog.UseSink(new ModuleLogSink(writer));
        using var scope = JobContext.Begin(new JobContext(Guid.NewGuid(), Guid.NewGuid(), "resize-image"));

        RunnerLog.Write(named, "wpis");

        Assert.Equal(onTheWire, Assert.Single(Drain(outbox)).Level);
    }

    [Fact]
    public void RunnerLog_reports_what_the_threshold_will_swallow()
    {
        var (_, writer) = Build(threshold: nameof(MessageLogLevel.Warning));
        using var sink = RunnerLog.UseSink(new ModuleLogSink(writer));

        Assert.False(RunnerLog.IsEnabled(RunnerLogLevel.Info));
        Assert.True(RunnerLog.IsEnabled(RunnerLogLevel.Warning));
    }

    [Theory]
    [InlineData(LogLevel.Trace, MessageLogLevel.Debug)]
    [InlineData(LogLevel.Debug, MessageLogLevel.Debug)]
    [InlineData(LogLevel.Information, MessageLogLevel.Info)]
    [InlineData(LogLevel.Warning, MessageLogLevel.Warning)]
    [InlineData(LogLevel.Error, MessageLogLevel.Error)]
    [InlineData(LogLevel.Critical, MessageLogLevel.Critical)]
    public void An_injected_ILogger_maps_onto_the_same_levels(LogLevel logged, MessageLogLevel onTheWire)
    {
        var (outbox, writer) = Build(threshold: nameof(MessageLogLevel.Debug));
        using var provider = new ModuleLoggerProvider(writer);
        var logger = provider.CreateLogger("Module.Resize");
        using var scope = JobContext.Begin(new JobContext(Guid.NewGuid(), Guid.NewGuid(), "resize-image"));

        logger.Log(logged, "wpis");

        Assert.Equal(onTheWire, Assert.Single(Drain(outbox)).Level);
    }

    [Fact]
    public void An_injected_ILogger_appends_the_exception_to_the_entry()
    {
        var (outbox, writer) = Build();
        using var provider = new ModuleLoggerProvider(writer);
        var logger = provider.CreateLogger("Module.Resize");
        using var scope = JobContext.Begin(new JobContext(Guid.NewGuid(), Guid.NewGuid(), "resize-image"));

        logger.LogError(new InvalidOperationException("boom"), "nie udało się");

        var line = Assert.Single(Drain(outbox));
        Assert.Equal(MessageLogLevel.Error, line.Level);
        Assert.Contains("nie udało się", line.Message);
        Assert.Contains("boom", line.Message);
    }

    /// <summary>
    /// <see cref="ScopedConsole"/> podmienia <c>Console.Out</c> całego procesu, więc test musi
    /// przywrócić poprzednie pisarze - inaczej zabiera konsolę testom, które pójdą po nim.
    /// </summary>
    private static IDisposable Hijack(JobLogWriter writer, out ScopedConsole console)
    {
        var previousOut = Console.Out;
        var previousError = Console.Error;
        console = new ScopedConsole(writer);
        return new Restore(previousOut, previousError);
    }

    private sealed class Restore : IDisposable
    {
        private readonly TextWriter _out;
        private readonly TextWriter _error;

        public Restore(TextWriter previousOut, TextWriter previousError)
        {
            _out = previousOut;
            _error = previousError;
        }

        public void Dispose()
        {
            Console.SetOut(_out);
            Console.SetError(_error);
        }
    }

    [Fact]
    public void Console_output_keeps_its_old_meaning()
    {
        var (outbox, writer) = Build();
        var job = Job();
        using var restore = Hijack(writer, out var console);

        using (console.BeginScope(job))
        {
            console.Out.WriteLine("zwykły przebieg");
            console.Error.WriteLine("coś nie tak");
        }

        var lines = Drain(outbox);
        Assert.Equal(2, lines.Count);
        Assert.Equal(MessageLogLevel.Info, lines[0].Level);
        Assert.Equal("zwykły przebieg", lines[0].Message);
        Assert.Equal(MessageLogLevel.Error, lines[1].Level);
        Assert.Equal("coś nie tak", lines[1].Message);
        Assert.All(lines, line => Assert.Equal(job.AttemptId, line.AttemptId));
    }

    [Fact]
    public void Console_output_outside_a_job_does_not_reach_the_platform()
    {
        var (outbox, writer) = Build();
        var job = Job();
        using var restore = Hijack(writer, out var console);

        using (console.BeginScope(job))
        {
        }
        console.Out.WriteLine("log samego runnera");

        Assert.Empty(Drain(outbox));
    }
}
