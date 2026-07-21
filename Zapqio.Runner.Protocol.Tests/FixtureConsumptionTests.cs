using System.Text.Json;
using Zapqio.Runner.Protocol.Enums;

namespace Zapqio.Runner.Protocol.Tests;

/// <summary>
/// PROTOCOL.md §10: an implementation conforms if it can <b>consume</b> every fixture such that,
/// after full decoding, the logical content equals the fixture's documented content
/// (fixtures/README.md). These tests read every canonical frame with the types in
/// Zapqio.Runner.Protocol and check the values that come out.
/// </summary>
public class FixtureConsumptionTests
{
    private const string JobId = "a1b2c3d4-e5f6-7890-abcd-ef1234567890";

    private static TPayload Decode<TPayload>(string fixture, MessageType expectedType)
    {
        var envelope = JsonSerializer.Deserialize<Message>(ProtocolRepo.Fixture(fixture), JsonDefaults.Options);
        Assert.NotNull(envelope);
        Assert.Equal(expectedType, envelope!.Type);
        Assert.NotNull(envelope.Data);

        var payload = JsonSerializer.Deserialize<TPayload>(envelope.Data, JsonDefaults.Options);
        Assert.NotNull(payload);
        return payload!;
    }

    [Theory]
    [InlineData("info.json", MessageType.Info)]
    [InlineData("job-poll.json", MessageType.Job)]
    [InlineData("job-dispatch.json", MessageType.Job)]
    [InlineData("log-info.json", MessageType.Log)]
    [InlineData("log-error.json", MessageType.Log)]
    [InlineData("job-return-ok.json", MessageType.JobReturn)]
    [InlineData("job-return-error.json", MessageType.JobReturn)]
    public void Envelope_of_every_fixture_is_readable(string fixture, MessageType expectedType)
    {
        var envelope = JsonSerializer.Deserialize<Message>(ProtocolRepo.Fixture(fixture), JsonDefaults.Options);

        Assert.NotNull(envelope);
        Assert.Equal(expectedType, envelope!.Type);
    }

    [Fact]
    public void Info_announces_the_runner_name_and_its_methods()
    {
        var info = Decode<MessageInfo>("info.json", MessageType.Info);

        Assert.Equal("build-agent-01", info.Name);
        var method = Assert.Single(info.Methods);
        Assert.Equal("resize-image", method.Name);
        Assert.Null(method.Out);

        // `in` carries a JSON Schema *as a string* (§7).
        using var inputSchema = JsonDocument.Parse(method.In);
        Assert.Equal("object", inputSchema.RootElement.GetProperty("type").GetString());
        Assert.Equal(
            "integer",
            inputSchema.RootElement.GetProperty("properties").GetProperty("width").GetProperty("type").GetString());
    }

    [Fact]
    public void Job_poll_carries_no_payload()
    {
        var envelope = JsonSerializer.Deserialize<Message>(ProtocolRepo.Fixture("job-poll.json"), JsonDefaults.Options);

        Assert.NotNull(envelope);
        Assert.Equal(MessageType.Job, envelope!.Type);
        Assert.Null(envelope.Data);
    }

    [Fact]
    public void Job_dispatch_decodes_through_both_string_layers()
    {
        var job = Decode<MessageJob>("job-dispatch.json", MessageType.Job);

        Assert.Equal(Guid.Parse(JobId), job.Id);
        Assert.Equal("resize-image", job.Name);

        // §8: MessageJob.data is itself a JSON string — the third decoding level.
        using var input = JsonDocument.Parse(job.Data);
        Assert.Equal(800, input.RootElement.GetProperty("width").GetInt32());
        Assert.Equal(600, input.RootElement.GetProperty("height").GetInt32());
    }

    [Theory]
    [InlineData("log-info.json", MessageLogLevel.Info, "Run Job: 2026-06-12T14:30:00", "2026-06-12T14:30:00.123+00:00")]
    [InlineData("log-error.json", MessageLogLevel.Error, "Main exception: boom", "2026-06-12T14:30:01.456+00:00")]
    public void Log_decodes_level_message_and_timestamp(
        string fixture, MessageLogLevel level, string message, string date)
    {
        var log = Decode<MessageLog>(fixture, MessageType.Log);

        Assert.Equal(Guid.Parse(JobId), log.JobId);
        Assert.Equal(level, log.Level);
        Assert.Equal(message, log.Message);
        Assert.Equal(DateTimeOffset.Parse(date), log.Date);
    }

    [Fact]
    public void JobReturn_ok_carries_the_job_output()
    {
        var result = Decode<MessageJobReturn>("job-return-ok.json", MessageType.JobReturn);

        Assert.Equal(Guid.Parse(JobId), result.Id);
        Assert.Equal(MessageResponseStatus.OK, result.Status);

        using var output = JsonDocument.Parse(result.Data);
        Assert.Equal("https://cdn.example.com/out/123.png", output.RootElement.GetProperty("url").GetString());
    }

    [Fact]
    public void JobReturn_error_carries_no_output()
    {
        var result = Decode<MessageJobReturn>("job-return-error.json", MessageType.JobReturn);

        Assert.Equal(Guid.Parse(JobId), result.Id);
        Assert.Equal(MessageResponseStatus.ERROR, result.Status);
        Assert.Null(result.Data);
    }
}
