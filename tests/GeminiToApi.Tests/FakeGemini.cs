using System.Collections.Concurrent;
using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using System.Web;
using static GeminiToApi.Tests.TestJson;

namespace GeminiToApi.Tests;

/// <summary>Stands in for gemini.google.com: answers StreamGenerate with a canned reply and records requests.</summary>
internal sealed class FakeGemini : HttpMessageHandler
{
    public sealed record Captured(HttpMethod Method, Uri Url, Dictionary<string, string> Headers, string Body)
    {
        public string Form(string key) => HttpUtility.ParseQueryString(Body)[key] ?? "";

        /// <summary>The decoded StreamGenerate inner payload.</summary>
        public JsonArray Inner => JsonNode.Parse(JsonNode.Parse(Form("f.req"))![1]!.GetValue<string>())!.AsArray();

        public string Prompt => Inner[0]![0]!.GetValue<string>();

        /// <summary>inner[2][0..2]: the conversation this request continues ("" for a new one).</summary>
        public (string Cid, string Rid, string Rcid) Conversation
            => (Inner[2]![0]!.GetValue<string>(), Inner[2]![1]!.GetValue<string>(), Inner[2]![2]!.GetValue<string>());
    }

    public ConcurrentQueue<Captured> Requests { get; } = new();

    /// <summary>Text of the next StreamGenerate reply.</summary>
    public string Reply { get; set; } = "Hello from Gemini";

    public Func<Captured, HttpResponseMessage?>? Override { get; set; }

    private int _turn;

    /// <summary>Gemini's reply frame: payload[1] = [conversation id, response id], candidate[0] = choice id.</summary>
    public static string StreamReply(string text, string conversationId = "c_test", int turn = 1)
    {
        var payload = Json(new object?[] { null, new object?[] { conversationId, "r_" + turn }, null, null, new object?[] { new object?[] { "rc_" + turn, new object?[] { text } } } });
        var frame   = Json(new object?[] { new object?[] { "wrb.fr", null, payload } });

        return ")]}'\n\n" + frame.Length + "\n" + frame + "\n";
    }

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var headers = request.Headers.ToDictionary(h => h.Key, h => string.Join(",", h.Value), StringComparer.OrdinalIgnoreCase);
        var body    = request.Content is null ? "" : await request.Content.ReadAsStringAsync(cancellationToken);
        var capture = new Captured(request.Method, request.RequestUri!, headers, body);

        Requests.Enqueue(capture);

        if (Override?.Invoke(capture) is { } custom) return custom;

        if (request.RequestUri!.AbsolutePath.EndsWith("/StreamGenerate", StringComparison.Ordinal))
        {
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(StreamReply(Reply, turn: Interlocked.Increment(ref _turn)), Encoding.UTF8) };
        }

        return new HttpResponseMessage(HttpStatusCode.NotFound) { Content = new StringContent("not found") };
    }
}
