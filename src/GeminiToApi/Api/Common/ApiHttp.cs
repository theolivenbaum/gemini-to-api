using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;
using GeminiToApi.Gemini;
using Microsoft.AspNetCore.Http.Features;

namespace GeminiToApi.Api.Common;

/// <summary>An error with an HTTP status, mapped by the endpoint to the vendor's error shape.</summary>
public sealed class ApiException(int status, string message, string? type = null) : Exception(message)
{
    public int     Status { get; } = status;
    public string? Type   { get; } = type;
}

/// <summary>JSON + SSE plumbing shared by the three API surfaces.</summary>
public static class ApiHttp
{
    public static readonly JsonSerializerOptions JsonOptions = new()
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Encoder                = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    public static string Serialize(object value) => JsonSerializer.Serialize(value, value.GetType(), JsonOptions);

    public static async Task WriteJsonAsync(HttpContext context, int status, object value)
    {
        context.Response.StatusCode  = status;
        context.Response.ContentType = "application/json; charset=utf-8";
        await context.Response.WriteAsync(Serialize(value), context.RequestAborted);
    }

    /// <summary>Reads the request body as JSON. Throws <see cref="ApiException"/> (400) when it is not.</summary>
    public static async Task<JsonElement> ReadJsonAsync(HttpContext context)
    {
        try
        {
            using var doc = await JsonDocument.ParseAsync(context.Request.Body, cancellationToken: context.RequestAborted);
            return doc.RootElement.Clone();
        }
        catch (JsonException e)
        {
            throw new ApiException(400, "invalid request body: " + e.Message);
        }
    }

    public static string Str(this JsonElement obj, string name)
        => obj.ValueKind == JsonValueKind.Object && obj.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString()! : "";

    public static JsonElement Prop(this JsonElement obj, string name)
        => obj.ValueKind == JsonValueKind.Object && obj.TryGetProperty(name, out var v) ? v : default;

    public static bool Bool(this JsonElement obj, string name)
        => obj.ValueKind == JsonValueKind.Object && obj.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.True;

    public static double? Number(this JsonElement obj, string name)
    {
        if (obj.ValueKind != JsonValueKind.Object || !obj.TryGetProperty(name, out var v)) return null;

        if (v.ValueKind == JsonValueKind.Number) return v.GetDouble();

        if (v.ValueKind is JsonValueKind.Null) return null;

        throw new ApiException(400, $"invalid request body: '{name}' must be a number");
    }

    public static IEnumerable<JsonElement> Array(this JsonElement obj, string name)
    {
        var value = obj.Prop(name);
        return value.ValueKind == JsonValueKind.Array ? value.EnumerateArray() : [];
    }

    public static void StartEventStream(HttpContext context)
    {
        context.Features.Get<IHttpResponseBodyFeature>()?.DisableBuffering();

        context.Response.StatusCode           = 200;
        context.Response.ContentType          = "text/event-stream";
        context.Response.Headers.CacheControl = "no-cache";
        context.Response.Headers.Connection   = "keep-alive";
        context.Response.Headers["X-Accel-Buffering"] = "no";
    }

    public static async Task WriteDataAsync(HttpContext context, string data, CancellationToken ct)
    {
        await context.Response.WriteAsync("data: " + data + "\n\n", ct);
        await context.Response.Body.FlushAsync(ct);
    }

    public static async Task WriteEventAsync(HttpContext context, string name, object payload, CancellationToken ct)
    {
        await context.Response.WriteAsync("event: " + name + "\ndata: " + Serialize(payload) + "\n\n", ct);
        await context.Response.Body.FlushAsync(ct);
    }

    /// <summary>Request-scoped token with an upper bound, so a stuck upstream call cannot hang a client forever.</summary>
    public static CancellationTokenSource Deadline(HttpContext context, TimeSpan timeout)
    {
        var cts = CancellationTokenSource.CreateLinkedTokenSource(context.RequestAborted);
        cts.CancelAfter(timeout);
        return cts;
    }

    /// <summary>The HTTP status for an exception raised while serving a request.</summary>
    public static int StatusFor(Exception e) => e switch
    {
        ApiException api                 => api.Status,
        ModelSelectionException          => 400,
        ThinkingLevelException           => 400,
        VideoValidationException         => 400,
        MusicValidationException         => 400,
        VideoBusyException               => 429,
        VideoJobNotFoundException        => 404,
        VideoNotReadyException           => 409,
        MediaGenerationException media   => media.Code switch
        {
            MediaGenerationException.QUOTA   => 429,
            MediaGenerationException.REFUSED => 422,
            MediaGenerationException.TIMEOUT => 504,
            _                                => 502,
        },
        _                                => 500,
    };

    /// <summary>Simulated streaming pace (the Go server pauses 30 ms between chunks).</summary>
    public static readonly TimeSpan ChunkDelay = TimeSpan.FromMilliseconds(30);
}
