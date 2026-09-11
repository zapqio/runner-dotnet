using Zapqio.Runner.Background;
using Zapqio.Runner.Protocol;
using Zapqio.Runner.Protocol.Enums;
using Microsoft.Extensions.Logging.Abstractions;

namespace Zapqio.Runner.Tests;

/// <summary>
/// Jeden autor gniazda. Reguły, które łatwo zepsuć przy równoległych zadaniach: kolejność FIFO
/// (w obrębie zadania potwierdzenie, log startowy, linie, wynik muszą wyjść w tej kolejności),
/// wynik oczekiwany przez wołającego przy martwym gnieździe kończy się od razu niepowodzeniem
/// (inaczej metoda ruszyłaby po ponownym połączeniu, gdy Web dawno zwrócił zadanie do kolejki),
/// a linie logu czekają na połączenie zamiast ginąć.
/// </summary>
public class OutboundSenderTests
{
    private static readonly TimeSpan Patience = TimeSpan.FromSeconds(5);

    private sealed class FakeTransport : IOutboundTransport
    {
        private readonly object _gate = new();
        private readonly List<Message> _written = new();

        public volatile bool Connected = true;
        public Func<Message, bool>? Accept { get; set; }

        public bool IsConnected => Connected;

        public IReadOnlyList<Message> Written
        {
            get { lock (_gate) return _written.ToList(); }
        }

        public Task<bool> WriteAsync(Message message)
        {
            var ok = Accept?.Invoke(message) ?? true;
            if (ok) lock (_gate) _written.Add(message);
            return Task.FromResult(ok);
        }
    }

    private static MessageLog Line(string text) => new()
    {
        Date = DateTimeOffset.UtcNow,
        JobId = Guid.NewGuid(),
        AttemptId = Guid.NewGuid(),
        Level = MessageLogLevel.Info,
        Message = text
    };

    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        var deadline = DateTime.UtcNow + Patience;
        while (!condition())
        {
            if (DateTime.UtcNow > deadline) throw new TimeoutException("Warunek nie został spełniony na czas.");
            await Task.Delay(10);
        }
    }

    private static (Outbox Outbox, FakeTransport Transport, OutboundSender Sender, CancellationTokenSource Stop) Start(
        int maxQueuedLogLines = 1000,
        bool connected = true)
    {
        var outbox = new Outbox(maxQueuedLogLines);
        var transport = new FakeTransport { Connected = connected };
        var sender = new OutboundSender(outbox, transport, NullLogger<OutboundSender>.Instance);
        var stop = new CancellationTokenSource();
        _ = sender.PumpAsync(stop.Token);
        return (outbox, transport, sender, stop);
    }

    [Fact]
    public async Task Delivers_InFifoOrder_AndNumbersAwaitedWrites()
    {
        var (outbox, transport, _, stop) = Start();
        using var _ = stop;

        var info = outbox.SendAndWaitAsync(Outbox.Frame(MessageType.Info, new MessageInfo { Name = "r", Methods = [] }));
        outbox.EnqueueLog(Line("first"));
        var result = outbox.SendAndWaitAsync(Outbox.Frame(MessageType.JobReturn, new MessageJobReturn { Status = MessageResponseStatus.OK }));
        outbox.Enqueue(Outbox.Frame(MessageType.Job, null));

        var infoSeq = await info;
        var resultSeq = await result;
        await WaitUntilAsync(() => transport.Written.Count == 4);

        Assert.Equal(
            new[] { MessageType.Info, MessageType.Log, MessageType.JobReturn, MessageType.Job },
            transport.Written.Select(m => m.Type).ToArray());
        Assert.NotNull(infoSeq);
        Assert.NotNull(resultSeq);
        Assert.True(resultSeq > infoSeq, "Numer sekwencji rośnie z każdym zapisem.");
        stop.Cancel();
    }

    /// <summary>Sedno rozstrzygnięcia G: log startowy przy martwym gnieździe ma zawieść od razu, nie po powrocie.</summary>
    [Fact]
    public async Task AwaitedItem_FailsFast_WhenDisconnected()
    {
        var (outbox, transport, _, stop) = Start(connected: false);
        using var _ = stop;

        var seq = await outbox.SendAndWaitAsync(Outbox.Frame(MessageType.Log, Line("Run Job")))
            .WaitAsync(Patience);

        Assert.Null(seq);
        Assert.Empty(transport.Written);
        stop.Cancel();
    }

    [Fact]
    public async Task LogLines_WaitForTheConnection_AndGoOutInOrder()
    {
        var (outbox, transport, sender, stop) = Start(connected: false);
        using var _ = stop;

        outbox.EnqueueLog(Line("a"));
        outbox.EnqueueLog(Line("b"));
        await Task.Delay(50);
        Assert.Empty(transport.Written);
        Assert.False(sender.IsDrained);

        transport.Connected = true;
        await WaitUntilAsync(() => transport.Written.Count == 2);

        Assert.Equal(new[] { "a", "b" }, transport.Written.Select(Text).ToArray());
        await WaitUntilAsync(() => sender.IsDrained);
        stop.Cancel();
    }

    /// <summary>Linie zaparkowane w czasie przerwy wychodzą przed tym, co zakolejkowano po powrocie.</summary>
    [Fact]
    public async Task ParkedLogs_GoBeforeNewerItems()
    {
        var (outbox, transport, _, stop) = Start(connected: false);
        using var _ = stop;

        outbox.EnqueueLog(Line("parked"));
        await Task.Delay(50);
        transport.Connected = true;
        var result = await outbox.SendAndWaitAsync(Outbox.Frame(MessageType.JobReturn, new MessageJobReturn()));

        Assert.NotNull(result);
        await WaitUntilAsync(() => transport.Written.Count == 2);
        Assert.Equal(new[] { MessageType.Log, MessageType.JobReturn }, transport.Written.Select(m => m.Type).ToArray());
        stop.Cancel();
    }

    [Fact]
    public async Task FailedWrite_OfAwaitedItem_ReturnsNull()
    {
        var (outbox, transport, _, stop) = Start();
        using var _ = stop;
        transport.Accept = _ => false;

        var seq = await outbox.SendAndWaitAsync(Outbox.Frame(MessageType.JobReturn, new MessageJobReturn())).WaitAsync(Patience);

        Assert.Null(seq);
        stop.Cancel();
    }

    /// <summary>
    /// Odcięte Web nie może rozdąć pamięci runnera bez końca: ponad limit nowe linie są odrzucane,
    /// a jedna linia ostrzegawcza mówi, że tak się stało.
    /// </summary>
    [Fact]
    public async Task LogLimit_DropsFurtherLines_AndWritesOneMarker()
    {
        var (outbox, transport, _, stop) = Start(maxQueuedLogLines: 2, connected: false);
        using var _ = stop;

        for (var i = 0; i < 5; i++)
            outbox.EnqueueLog(Line($"line-{i}"));

        transport.Connected = true;
        await WaitUntilAsync(() => transport.Written.Count == 3);
        await Task.Delay(50);

        var texts = transport.Written.Select(Text).ToArray();
        Assert.Equal(3, texts.Length);
        Assert.Equal("line-0", texts[0]);
        Assert.Equal("line-1", texts[1]);
        Assert.Contains("pełna", texts[2]);
        Assert.Equal(MessageLogLevel.Error, Log(transport.Written[2]).Level);
        stop.Cancel();
    }

    [Fact]
    public async Task LogLimit_RearmsTheMarker_OnceTheQueueDrains()
    {
        var (outbox, transport, _, stop) = Start(maxQueuedLogLines: 1, connected: false);
        using var _ = stop;

        outbox.EnqueueLog(Line("first"));
        outbox.EnqueueLog(Line("dropped-1"));
        transport.Connected = true;
        await WaitUntilAsync(() => transport.Written.Count == 2);

        // Kolejka pusta, więc kolejna seria odrzuceń dostaje własne ostrzeżenie.
        transport.Connected = false;
        outbox.EnqueueLog(Line("second"));
        outbox.EnqueueLog(Line("dropped-2"));
        transport.Connected = true;
        await WaitUntilAsync(() => transport.Written.Count == 4);

        var texts = transport.Written.Select(Text).ToArray();
        Assert.Equal("first", texts[0]);
        Assert.Contains("pełna", texts[1]);
        Assert.Equal("second", texts[2]);
        Assert.Contains("pełna", texts[3]);
        stop.Cancel();
    }

    private static MessageLog Log(Message message) =>
        System.Text.Json.JsonSerializer.Deserialize<MessageLog>(message.Data, JsonDefaults.Options)!;

    private static string Text(Message message) => Log(message).Message;
}
