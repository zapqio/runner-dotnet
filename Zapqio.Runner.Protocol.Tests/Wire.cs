using System.Text.Json;
using System.Text.Json.Nodes;
using Zapqio.Runner.Protocol.Enums;

namespace Zapqio.Runner.Protocol.Tests;

/// <summary>
/// Frame-level helpers: build frames the way a conformant producer must (PROTOCOL.md §4), and
/// decode them all the way down through the string-encoded layers (§8) so two frames can be
/// compared semantically (§10) rather than byte for byte.
/// </summary>
internal static class Wire
{
    /// <summary>Serializes a payload and wraps it in the envelope — exactly what goes on the wire.</summary>
    public static string Frame<TPayload>(MessageType type, TPayload payload)
        => JsonSerializer.Serialize(
            new Message
            {
                Type = type,
                Data = JsonSerializer.Serialize(payload, JsonDefaults.Options),
            },
            JsonDefaults.Options);

    /// <summary>The Job poll: <c>{ "type": "Job", "data": null }</c> (§5.2).</summary>
    public static string PollFrame()
        => JsonSerializer.Serialize(new Message { Type = MessageType.Job, Data = null! }, JsonDefaults.Options);

    /// <summary>Parses a frame into its envelope object.</summary>
    public static JsonObject Envelope(string frame) => JsonNode.Parse(frame)!.AsObject();

    /// <summary>Parses <c>envelope.data</c> — the second decoding level. Null for a Job poll.</summary>
    public static JsonNode? Payload(string frame)
    {
        var data = Envelope(frame)["data"];
        return data is null || data.GetValueKind() == JsonValueKind.Null
            ? null
            : JsonNode.Parse(data.GetValue<string>());
    }

    /// <summary>
    /// Decodes a frame through every documented layer of string-encoding — envelope → payload →
    /// nested job I/O and method schemas — producing the frame's logical content. Two frames are
    /// conformance-equal when their fully decoded forms are deep-equal.
    /// </summary>
    public static JsonNode FullyDecode(string frame)
    {
        var envelope = Envelope(frame);
        var type = envelope["type"]?.GetValue<string>();
        var payload = Payload(frame);

        if (payload is JsonObject obj)
        {
            switch (type)
            {
                case "Job":
                case "JobReturn":
                    obj["data"] = DecodeNested(obj["data"]);
                    break;
                case "Info":
                    foreach (var method in (obj["methods"] as JsonArray ?? []).OfType<JsonObject>())
                    {
                        method["in"] = DecodeNested(method["in"]);
                        method["out"] = DecodeNested(method["out"]);
                    }

                    break;
            }
        }

        return new JsonObject
        {
            ["type"] = type is null ? null : JsonValue.Create(type),
            ["data"] = payload,
        };
    }

    /// <summary>Parses a value that carries JSON inside a JSON string; leaves anything else alone.</summary>
    private static JsonNode? DecodeNested(JsonNode? node)
    {
        if (node is null || node.GetValueKind() != JsonValueKind.String)
        {
            return node?.DeepClone();
        }

        var text = node.GetValue<string>();
        if (string.IsNullOrEmpty(text))
        {
            return JsonValue.Create(text);
        }

        try
        {
            return JsonNode.Parse(text);
        }
        catch (JsonException)
        {
            // Not JSON — an opaque job input/output may legitimately be a bare string.
            return JsonValue.Create(text);
        }
    }

    /// <summary>
    /// Semantic equality per §10: key order and insignificant whitespace do not matter.
    /// </summary>
    public static bool DeepEquals(JsonNode? left, JsonNode? right)
    {
        if (left is null || right is null)
        {
            return left is null && right is null;
        }

        var kind = left.GetValueKind();
        if (kind != right.GetValueKind())
        {
            return false;
        }

        switch (kind)
        {
            case JsonValueKind.Object:
                var a = left.AsObject();
                var b = right.AsObject();
                return a.Count == b.Count
                       && a.All(p => b.TryGetPropertyValue(p.Key, out var other) && DeepEquals(p.Value, other));

            case JsonValueKind.Array:
                var x = left.AsArray();
                var y = right.AsArray();
                return x.Count == y.Count && x.Select((item, i) => DeepEquals(item, y[i])).All(equal => equal);

            case JsonValueKind.String:
                return left.GetValue<string>() == right.GetValue<string>();

            case JsonValueKind.Number:
                return left.GetValue<JsonElement>().GetDecimal() == right.GetValue<JsonElement>().GetDecimal();

            default:
                return true; // True / False / Null / Undefined are fully described by their kind.
        }
    }

    public static void AssertSameMessage(string expectedFrame, string actualFrame, string what)
    {
        var expected = FullyDecode(expectedFrame);
        var actual = FullyDecode(actualFrame);
        Assert.True(
            DeepEquals(expected, actual),
            $"""
             {what} is not semantically equal to the protocol fixture.
             expected (fully decoded): {expected.ToJsonString()}
             actual   (fully decoded): {actual.ToJsonString()}
             actual   (raw frame):     {actualFrame}
             """);
    }
}
