using System.Text.Json;
using System.Text.Json.Serialization;

namespace Zapqio.Protocol
{
    public static class JsonDefaults
    {
        public static JsonSerializerOptions Options { get; } = new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            PropertyNameCaseInsensitive = true,
            Converters = { new JsonStringEnumConverter() }
        };
    }
}
