using Zapqio.Runner.Protocol.Enums;

namespace Zapqio.Runner.Protocol.Tests;

/// <summary>
/// The other half of §10 conformance: building each documented message with the types in
/// Zapqio.Runner.Protocol must <b>produce</b> a frame semantically equal to the fixture — same
/// field names, same enum strings, same double-encoding of <c>data</c>.
/// </summary>
public class FixtureProductionTests
{
    private static readonly Guid JobId = Guid.Parse("a1b2c3d4-e5f6-7890-abcd-ef1234567890");

    private const string ResizeImageInputSchema =
        """{"type":"object","properties":{"width":{"type":"integer"},"height":{"type":"integer"}}}""";

    [Fact]
    public void Info_frame_matches_the_fixture()
    {
        var frame = Wire.Frame(MessageType.Info, new MessageInfo
        {
            Name = "build-agent-01",
            Methods =
            [
                new MessageMethod
                {
                    Name = "resize-image",
                    In = ResizeImageInputSchema,
                    Out = null!,
                },
            ],
        });

        Wire.AssertSameMessage(ProtocolRepo.Fixture("info.json"), frame, "The produced Info frame");
    }

    [Fact]
    public void Job_poll_frame_matches_the_fixture()
    {
        Wire.AssertSameMessage(ProtocolRepo.Fixture("job-poll.json"), Wire.PollFrame(), "The produced Job poll frame");
    }

    [Fact]
    public void Job_dispatch_frame_matches_the_fixture()
    {
        var frame = Wire.Frame(MessageType.Job, new MessageJob
        {
            Id = JobId,
            Name = "resize-image",
            Data = """{"width":800,"height":600}""",
        });

        Wire.AssertSameMessage(ProtocolRepo.Fixture("job-dispatch.json"), frame, "The produced Job dispatch frame");
    }

    [Theory]
    [InlineData("log-info.json", MessageLogLevel.Info, "Run Job: 2026-06-12T14:30:00", "2026-06-12T14:30:00.123+00:00")]
    [InlineData("log-error.json", MessageLogLevel.Error, "Main exception: boom", "2026-06-12T14:30:01.456+00:00")]
    public void Log_frame_matches_the_fixture(string fixture, MessageLogLevel level, string message, string date)
    {
        var frame = Wire.Frame(MessageType.Log, new MessageLog
        {
            JobId = JobId,
            Level = level,
            Message = message,
            Date = DateTimeOffset.Parse(date),
        });

        Wire.AssertSameMessage(ProtocolRepo.Fixture(fixture), frame, $"The produced {level} Log frame");
    }

    [Fact]
    public void JobReturn_ok_frame_matches_the_fixture()
    {
        var frame = Wire.Frame(MessageType.JobReturn, new MessageJobReturn
        {
            Id = JobId,
            Status = MessageResponseStatus.OK,
            Data = """{"url":"https://cdn.example.com/out/123.png"}""",
        });

        Wire.AssertSameMessage(ProtocolRepo.Fixture("job-return-ok.json"), frame, "The produced OK JobReturn frame");
    }

    [Fact]
    public void JobReturn_error_frame_matches_the_fixture()
    {
        var frame = Wire.Frame(MessageType.JobReturn, new MessageJobReturn
        {
            Id = JobId,
            Status = MessageResponseStatus.ERROR,
            Data = null!,
        });

        Wire.AssertSameMessage(
            ProtocolRepo.Fixture("job-return-error.json"), frame, "The produced ERROR JobReturn frame");
    }

    /// <summary>
    /// §4: <c>data</c> is a string holding JSON, never a nested object. A producer that forgets the
    /// extra encoding still round-trips against itself, so this is checked on the raw frame.
    /// </summary>
    [Fact]
    public void Envelope_data_is_a_string_not_a_nested_object()
    {
        var frame = Wire.Frame(MessageType.JobReturn, new MessageJobReturn
        {
            Id = JobId,
            Status = MessageResponseStatus.OK,
            Data = """{"url":"x"}""",
        });

        var data = Wire.Envelope(frame)["data"];

        Assert.NotNull(data);
        Assert.Equal(System.Text.Json.JsonValueKind.String, data!.GetValueKind());
    }
}
