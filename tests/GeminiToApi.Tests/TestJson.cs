using System.Text.Json.Nodes;
using GeminiToApi.Gemini;

namespace GeminiToApi.Tests;

/// <summary>Builds JSPB-shaped fixtures the way the Go tests build []interface{} literals.</summary>
internal static class TestJson
{
    public static JsonArray A(params object?[] items) => new(items.Select(Node).ToArray());

    public static object?[] Sized(int length) => new object?[length];

    public static JsonNode? Node(object? value) => value switch
    {
        null                              => null,
        JsonNode node                     => node,
        object?[] array                   => A(array),
        Dictionary<string, object?> map   => new JsonObject(map.Select(kv => KeyValuePair.Create(kv.Key, Node(kv.Value)))),
        string s                          => JsonValue.Create(s),
        bool b                            => JsonValue.Create(b),
        int i                             => JsonValue.Create(i),
        long l                            => JsonValue.Create(l),
        double d                          => JsonValue.Create(d),
        _                                 => throw new ArgumentException("unsupported fixture value " + value.GetType()),
    };

    public static string Json(object? value) => Jspb.Serialize(Node(value));

    /// <summary>Round-trips through text so values behave like parsed wire data.</summary>
    public static JsonArray Parsed(object?[] items) => (JsonArray)JsonNode.Parse(Json(items))!;

    public static IReadOnlyList<GeminiModel> TestModels() =>
    [
        new(new ModelInfo("gemini-flash", "", 1), "1111111111111111", 2, 12, 1, []),
        new(new ModelInfo("gemini-pro", "", 1), "2222222222222222", 2, 12, 3, []),
    ];
}
