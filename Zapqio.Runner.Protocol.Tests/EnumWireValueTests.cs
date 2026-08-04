using System.Text.Json;
using System.Text.Json.Nodes;
using Zapqio.Runner.Protocol.Enums;

namespace Zapqio.Runner.Protocol.Tests;

/// <summary>
/// PROTOCOL.md §6: enums travel as their exact names — PascalCase/uppercase, never camelCase and
/// never integers. The allowed values are read out of schemas.json so the spec, not this test, is
/// the source of truth.
/// </summary>
public class EnumWireValueTests
{
    private static string[] AllowedValues(string def, string property) =>
        ProtocolRepo.Defs[def]!["properties"]![property]!["enum"]!
            .AsArray()
            .Select(value => value!.GetValue<string>())
            .ToArray();

    private static string Serialize<TEnum>(TEnum value) => JsonSerializer.Serialize(value, JsonDefaults.Options);

    private static void AssertEnumMatchesSpec<TEnum>(string def, string property) where TEnum : struct, Enum
    {
        var allowed = AllowedValues(def, property);
        var declared = Enum.GetNames<TEnum>();

        Assert.Equal(allowed.OrderBy(v => v, StringComparer.Ordinal), declared.OrderBy(v => v, StringComparer.Ordinal));

        foreach (var member in Enum.GetValues<TEnum>())
        {
            var json = Serialize(member);

            // A string, not an integer (§6: "MUST NOT emit integers").
            Assert.Equal(JsonValueKind.String, JsonNode.Parse(json)!.GetValueKind());
            Assert.Equal($"\"{member}\"", json);
            Assert.Contains(member.ToString(), allowed);

            // ...and it reads back as the same member.
            Assert.Equal(member, JsonSerializer.Deserialize<TEnum>(json, JsonDefaults.Options));
        }
    }

    [Fact]
    public void Message_type_values_match_the_spec()
        => AssertEnumMatchesSpec<MessageType>("message", "type");

    [Fact]
    public void Log_level_values_match_the_spec()
        => AssertEnumMatchesSpec<MessageLogLevel>("messageLog", "level");

    [Fact]
    public void JobReturn_status_values_match_the_spec()
        => AssertEnumMatchesSpec<MessageResponseStatus>("messageJobReturn", "status");

    /// <summary>The exact strings §6 tabulates, spelled out so a rename cannot pass silently.</summary>
    [Theory]
    [InlineData(MessageType.Info, "Info")]
    [InlineData(MessageType.Job, "Job")]
    [InlineData(MessageType.JobAccepted, "JobAccepted")]
    [InlineData(MessageType.JobReturn, "JobReturn")]
    [InlineData(MessageType.Log, "Log")]
    public void Message_type_serializes_to_its_documented_string(MessageType type, string expected)
        => Assert.Equal($"\"{expected}\"", Serialize(type));

    [Theory]
    [InlineData(MessageLogLevel.Info, "Info")]
    [InlineData(MessageLogLevel.Error, "Error")]
    public void Log_level_serializes_to_its_documented_string(MessageLogLevel level, string expected)
        => Assert.Equal($"\"{expected}\"", Serialize(level));

    [Theory]
    [InlineData(MessageResponseStatus.OK, "OK")]
    [InlineData(MessageResponseStatus.ERROR, "ERROR")]
    public void JobReturn_status_serializes_to_its_documented_string(MessageResponseStatus status, string expected)
        => Assert.Equal($"\"{expected}\"", Serialize(status));
}
