using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Zapqio.Runner.Protocol.Enums;

namespace Zapqio.Runner.Protocol.Tests;

/// <summary>PROTOCOL.md §7 — the scalar wire formats.</summary>
public partial class ScalarFormatTests
{
    [GeneratedRegex("^[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}$")]
    private static partial Regex CanonicalUuid();

    // ISO-8601 with a timezone offset; fractional seconds optional. "Z" is not what the reference
    // emits, but the spec's example carries an explicit +00:00, so require an offset to be present.
    [GeneratedRegex(@"^\d{4}-\d{2}-\d{2}T\d{2}:\d{2}:\d{2}(\.\d+)?([+-]\d{2}:\d{2}|Z)$")]
    private static partial Regex Iso8601WithOffset();

    private static JsonObject Serialize<T>(T value)
        => JsonNode.Parse(JsonSerializer.Serialize(value, JsonDefaults.Options))!.AsObject();

    [Fact]
    public void Ids_are_canonical_lowercase_hyphenated_uuids()
    {
        var id = Guid.Parse("A1B2C3D4-E5F6-7890-ABCD-EF1234567890");

        var job = Serialize(new MessageJob { Id = id, Name = "resize-image", Data = "{}" });
        var result = Serialize(new MessageJobReturn { Id = id, Status = MessageResponseStatus.OK, Data = "{}" });
        var log = Serialize(new MessageLog
        {
            JobId = id,
            Level = MessageLogLevel.Info,
            Message = "x",
            Date = DateTimeOffset.UtcNow,
        });

        foreach (var written in new[] { job["id"], result["id"], log["jobId"] })
        {
            var text = written!.GetValue<string>();
            Assert.Matches(CanonicalUuid(), text);
            Assert.Equal("a1b2c3d4-e5f6-7890-abcd-ef1234567890", text);
        }
    }

    [Theory]
    [InlineData("2026-06-12T14:30:00.123+00:00")]
    [InlineData("2026-06-12T14:30:00+00:00")]
    [InlineData("2026-06-12T16:30:00.123+02:00")]
    public void Log_timestamps_are_iso8601_with_an_offset_and_round_trip(string date)
    {
        var original = DateTimeOffset.Parse(date);

        var log = Serialize(new MessageLog
        {
            JobId = Guid.NewGuid(),
            Level = MessageLogLevel.Info,
            Message = "x",
            Date = original,
        });
        var written = log["date"]!.GetValue<string>();

        Assert.Matches(Iso8601WithOffset(), written);
        Assert.Equal(original, DateTimeOffset.Parse(written));
        Assert.Equal(original.Offset, DateTimeOffset.Parse(written).Offset);
    }

    /// <summary>
    /// §7: a method's <c>in</c>/<c>out</c> is a JSON Schema carried <b>as a string</b>, and a job's
    /// input/output is opaque JSON carried as a string — neither may be inlined as an object.
    /// </summary>
    [Fact]
    public void Nested_schemas_and_job_io_stay_strings()
    {
        var method = Serialize(new MessageMethod { Name = "m", In = """{"type":"object"}""", Out = """{"type":"string"}""" });
        Assert.Equal(JsonValueKind.String, method["in"]!.GetValueKind());
        Assert.Equal(JsonValueKind.String, method["out"]!.GetValueKind());

        var job = Serialize(new MessageJob { Id = Guid.NewGuid(), Name = "m", Data = """{"width":800}""" });
        Assert.Equal(JsonValueKind.String, job["data"]!.GetValueKind());

        var result = Serialize(new MessageJobReturn
        {
            Id = Guid.NewGuid(),
            Status = MessageResponseStatus.OK,
            Data = """{"url":"x"}""",
        });
        Assert.Equal(JsonValueKind.String, result["data"]!.GetValueKind());
    }
}
