using System.Net;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using System.Threading.Channels;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Zapqio.Runner.Background;
using Zapqio.Runner.Protocol;
using Zapqio.Runner.Protocol.Enums;

namespace Zapqio.Runner.Tests;

public class RunnerReconnectTests
{
    [Fact]
    public async Task BrokenSocket_ResendsInfoWithSameProcessAndRunningAttempt_ThenDeliversResult()
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        var infos = Channel.CreateUnbounded<(MessageInfo Info, string Header, WebSocket Socket)>();
        var results = Channel.CreateUnbounded<MessageJobReturn>();
        var webBuilder = WebApplication.CreateBuilder();
        webBuilder.Logging.ClearProviders();
        webBuilder.WebHost.ConfigureKestrel(kestrel => kestrel.Listen(IPAddress.Loopback, 0));
        await using var web = webBuilder.Build();
        web.UseWebSockets();
        web.Map("/ws-runner", async context =>
        {
            using var socket = await context.WebSockets.AcceptWebSocketAsync();
            try
            {
                var buffer = new byte[8192];
                while (!deadline.IsCancellationRequested && socket.State == WebSocketState.Open)
                {
                    using var data = new MemoryStream();
                    WebSocketReceiveResult frame;
                    do
                    {
                        frame = await socket.ReceiveAsync(new ArraySegment<byte>(buffer), deadline.Token);
                        if (frame.MessageType == WebSocketMessageType.Close)
                        {
                            await socket.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, "done", deadline.Token);
                            return;
                        }
                        data.Write(buffer, 0, frame.Count);
                    } while (!frame.EndOfMessage);
                    var message = JsonSerializer.Deserialize<Message>(data.ToArray(), JsonDefaults.Options)!;
                    if (message.Type == MessageType.Info)
                        await infos.Writer.WriteAsync((JsonSerializer.Deserialize<MessageInfo>(message.Data, JsonDefaults.Options)!,
                            context.Request.Headers[MessageInfo.ProcessInstanceHeader].ToString(), socket), deadline.Token);
                    if (message.Type == MessageType.JobReturn)
                        await results.Writer.WriteAsync(JsonSerializer.Deserialize<MessageJobReturn>(message.Data, JsonDefaults.Options)!, deadline.Token);
                }
            }
            catch (Exception ex) when (ex is WebSocketException or OperationCanceledException or ObjectDisposedException) { }
        });
        await web.StartAsync(deadline.Token);
        var settings = new AppSettings { Name = "reconnect-test", Token = "test", Url = web.Urls.Single().Replace("http://", "ws://"),
            MaxConcurrency = 2, StopTimeoutSeconds = 0 };
        var process = new RunnerProcessState();
        var pending = new PendingJobReturns();
        var outbox = new Outbox(1000);
        await using var client = new WSClient(settings, NullLogger<WSClient>.Instance,
            new MethodsProvider(NullLogger<MethodsProvider>.Instance,
                new JobLogWriter(outbox, settings, NullLoggerFactory.Instance)), outbox, process, pending);
        using var sender = new OutboundSender(outbox, client, NullLogger<OutboundSender>.Instance);
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var finish = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var scheduler = new JobScheduler(2, async job =>
        {
            started.TrySetResult();
            await finish.Task.WaitAsync(deadline.Token);
            var result = new MessageJobReturn { Id = job.Id, AttemptId = job.AttemptId, Status = MessageResponseStatus.OK, Data = "kept-result" };
            var sequence = await client.SendJobReturn(result.Id, result.AttemptId, result.Status, result.Data);
            if (sequence is { } sent) pending.MarkSent(result, sent); else pending.MarkFailed(result);
        }, job => client.SendJobAccepted(job.Id, job.AttemptId), client.SendQueryOnJob,
            NullLogger<JobScheduler>.Instance, process);
        using var binder = new RequestBindBackground(client, NullLogger<RequestBindBackground>.Instance,
            pending, scheduler, outbox, sender, settings);
        using var schedulerStop = new CancellationTokenSource();
        var scheduling = scheduler.RunAsync(schedulerStop.Token);
        await sender.StartAsync(deadline.Token);
        await binder.StartAsync(deadline.Token);
        try
        {
            var first = await infos.Reader.ReadAsync(deadline.Token);
            Assert.Equal(process.InstanceId.ToString(), first.Header);
            Assert.Equal(process.InstanceId, first.Info.ProcessInstanceId);
            Assert.Empty(first.Info.ActiveAttemptIds!);
            var job = new MessageJob { Id = Guid.NewGuid(), AttemptId = Guid.NewGuid(), Name = "Wait", Data = "input" };
            var dispatch = new Message { Type = MessageType.Job, Data = JsonSerializer.Serialize(job, JsonDefaults.Options) };
            await first.Socket.SendAsync(new ArraySegment<byte>(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(dispatch, JsonDefaults.Options))),
                WebSocketMessageType.Text, true, deadline.Token);
            await started.Task.WaitAsync(deadline.Token);
            first.Socket.Abort();
            var second = await infos.Reader.ReadAsync(deadline.Token);
            Assert.Equal(first.Header, second.Header);
            Assert.Equal(first.Info.ProcessInstanceId, second.Info.ProcessInstanceId);
            Assert.Equal(job.AttemptId, Assert.Single(second.Info.ActiveAttemptIds!));
            finish.TrySetResult();
            var returned = await results.Reader.ReadAsync(deadline.Token);
            Assert.Equal(job.AttemptId, returned.AttemptId);
            Assert.Equal("kept-result", returned.Data);
        }
        finally
        {
            finish.TrySetResult();
            using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            await binder.StopAsync(stop.Token);
            await schedulerStop.CancelAsync();
            await scheduling;
            await sender.StopAsync(stop.Token);
            await web.StopAsync(stop.Token);
        }
    }
}
