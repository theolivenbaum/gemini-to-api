using System.Net;
using System.Text;
using Microsoft.Extensions.Logging;

namespace GeminiToApi.Gemini;

public sealed partial class GeminiClient
{
    private const int MAX_GENERATED_IMAGE_BYTES = 50 << 20;

    /// <summary>Sends one prompt (plus optional files) to StreamGenerate and parses the reply.</summary>
    public async Task<GeminiResponse> GenerateContentAsync(string prompt, GenerateOptions? options = null, CancellationToken cancellationToken = default)
    {
        options ??= new GenerateOptions();

        var thinkingLevel = GeminiProtocol.ParseThinkingLevel(options.ThinkingLevel);

        var requestedModel = options.Model ?? "";
        var canvas         = options.Canvas;
        var music          = options.Music;

        var (withoutCanvas, isCanvas) = Canvas.SplitModel(requestedModel);

        if (isCanvas)
        {
            requestedModel = withoutCanvas;
            canvas         = true;
        }

        var (withoutMusic, isMusic) = Music.SplitModel(requestedModel);

        if (isMusic)
        {
            requestedModel = withoutMusic;
            music        ??= new MusicConfig();
        }

        var session = _session;

        if (session is null || session.AccessToken.Length == 0) throw new GeminiException("client not initialized");

        var model = GeminiModels.Resolve(requestedModel, session.Models);

        _log.LogDebug("Selected Gemini model {Model} ({ModelId}) for request {Requested}", model.Id, model.ModelId, requestedModel);

        var uploaded = await UploadFilesAsync(options.InputFiles, session, cancellationToken);

        var requestId = Guid.NewGuid().ToString().ToUpperInvariant();
        var inner     = GeminiProtocol.BuildGenerateInner(prompt, uploaded, model.ModelNumber, session.Language, requestId, _temporary);

        inner[80] = thinkingLevel;

        var webTool = options.Video is not null || music is not null || canvas;
        var headers = GeminiProtocol.BuildModelHeaders(model, requestId, GenerationId, thinkingLevel, webTool);

        if (options.Video is { } aspect) GeminiProtocol.ApplyVideoInner(inner, aspect);
        else if (music is not null) GeminiProtocol.ApplyMusicInner(inner, music);
        else if (canvas) GeminiProtocol.ApplyCanvasInner(inner);

        var outer = Jspb.Serialize(Jspb.Of(null, Jspb.Serialize(inner)));

        var form = new Dictionary<string, string> { ["at"] = session.AccessToken, ["f.req"] = outer };

        var query = new Dictionary<string, string>
        {
            ["at"]     = session.AccessToken,
            ["hl"]     = session.Language,
            ["_reqid"] = GeminiProtocol.RandomRequestId(),
            ["rt"]     = "c",
        };

        if (session.BuildLabel.Length > 0) query["bl"]    = session.BuildLabel;
        if (session.SessionId.Length > 0)  query["f.sid"] = session.SessionId;

        var url = GeminiProtocol.AccountUrl(GeminiProtocol.ENDPOINT_GENERATE, AuthUser) + "?" + GeminiProtocol.FormEncode(query);

        // A retried video/music request could start (and bill) a second generation.
        var maxAttempts = _maxRetries <= 0 || options.Video is not null || music is not null ? 1 : _maxRetries;

        Exception? lastError = null;

        for (var attempt = 1; attempt <= maxAttempts; attempt++)
        {
            if (attempt > 1)
            {
                var backoff = TimeSpan.FromSeconds(1 << (attempt - 2));
                _log.LogWarning("Retrying GenerateContent (attempt {Attempt}/{Max}) in {Backoff}: {Error}", attempt, maxAttempts, backoff, lastError?.Message);
                await Task.Delay(backoff, cancellationToken);
            }

            using var request = new HttpRequestMessage(HttpMethod.Post, url) { Content = FormContent(form) };

            foreach (var (name, value) in GeminiProtocol.DefaultHeaders) request.Headers.TryAddWithoutValidation(name, value);
            foreach (var (name, value) in headers) request.Headers.TryAddWithoutValidation(name, value);

            if (session.CookieHeader.Length > 0) request.Headers.TryAddWithoutValidation("Cookie", session.CookieHeader);

            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(TimeSpan.FromMinutes(5));

            string body;

            try
            {
                using var response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeout.Token);

                if (response.StatusCode != HttpStatusCode.OK)
                {
                    var snippet = await response.Content.ReadAsStringAsync(timeout.Token);

                    if (snippet.Length > 512) snippet = snippet[..512];

                    lastError = new GeminiException($"generate failed with status: {(int)response.StatusCode}");
                    _log.LogWarning("Generate returned {Status} (attempt {Attempt}): {Snippet}", (int)response.StatusCode, attempt, snippet);

                    if ((int)response.StatusCode >= 500) continue;

                    throw lastError;
                }

                body = await response.Content.ReadAsStringAsync(timeout.Token);
            }
            catch (Exception e) when (e is HttpRequestException || e is TaskCanceledException && !cancellationToken.IsCancellationRequested)
            {
                _log.LogWarning("Generate request failed, will retry (attempt {Attempt}): {Error}", attempt, e.Message);
                lastError = e;
                continue;
            }

            GeminiResponse result;

            try
            {
                result = GeminiResponseParser.Parse(body);
            }
            catch (GeminiException e)
            {
                _log.LogWarning("Failed to parse response, will retry (attempt {Attempt}): {Error}", attempt, e.Message);
                lastError = e;
                continue;
            }

            result.Model          = model.Id;
            result.ModelId        = model.ModelId;
            result.RequestedModel = requestedModel.Trim().Length > 0 ? requestedModel.Trim() : model.Id;

            // Gemini may silently serve a lower-tier model when the selected model's web quota is
            // exhausted. A single known model ID in the protocol body is routing evidence.
            if (ServedModel(body, session.Models) is { } actual && actual.ModelId != model.ModelId)
            {
                result.Model   = actual.Id;
                result.ModelId = actual.ModelId;
                _log.LogWarning("Gemini served a different model: requested {Requested}, actual {Actual}", model.Id, actual.Id);
            }

            if (canvas)
            {
                result.Model          += Canvas.MODEL_SUFFIX;
                result.RequestedModel += Canvas.MODEL_SUFFIX;
            }

            if (attempt > 1) _log.LogInformation("GenerateContent succeeded after retry (attempt {Attempt})", attempt);

            if (options.DownloadGeneratedImages)
            {
                foreach (var image in result.Images.Where(i => i.Generated))
                {
                    try
                    {
                        image.B64Json = Convert.ToBase64String(await DownloadGeneratedImageAsync(image.Url, session.CookieHeader, cancellationToken));
                    }
                    catch (Exception e) when (e is GeminiException or HttpRequestException)
                    {
                        _log.LogWarning("Failed to download generated image: {Error}", e.Message);
                    }
                }
            }

            return result;
        }

        _log.LogError("GenerateContent failed after {Attempts} attempts: {Error}", maxAttempts, lastError?.Message);

        throw new GeminiException($"after {maxAttempts} attempts: {lastError?.Message}", lastError);
    }

    /// <summary>The one registry model whose ID appears in the reply, if exactly one does.</summary>
    internal static GeminiModel? ServedModel(string responseBody, IReadOnlyList<GeminiModel> models)
    {
        GeminiModel? found = null;

        foreach (var model in models)
        {
            if (model.ModelId.Length == 0 || !responseBody.Contains(model.ModelId, StringComparison.Ordinal)) continue;

            if (found is not null) return null;

            found = model;
        }

        return found;
    }

    // ---------------------------------------------------------------- media downloads

    /// <summary>
    /// GET a Google-hosted media URL with the session cookie, following redirects by hand: every hop
    /// must stay on https and a trusted Google host, and only then gets the cookie.
    /// </summary>
    internal async Task<(byte[] Data, string ContentType)> DownloadMediaAsync(string url, string cookieHeader, int maxBytes, TimeSpan timeout, CancellationToken cancellationToken)
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        cts.CancelAfter(timeout);

        var current = url;

        for (var hop = 0; hop <= 10; hop++)
        {
            if (!MediaUrls.IsTrustedMediaUrl(current)) throw new GeminiException("refusing media download from untrusted host");

            using var request = new HttpRequestMessage(HttpMethod.Get, current);
            request.Headers.TryAddWithoutValidation("User-Agent", GeminiProtocol.USER_AGENT);
            request.Headers.TryAddWithoutValidation("Referer", "https://gemini.google.com/");

            if (cookieHeader.Length > 0) request.Headers.TryAddWithoutValidation("Cookie", cookieHeader);

            using var response = await _httpManual.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cts.Token);

            if ((int)response.StatusCode is >= 300 and < 400 && response.Headers.Location is { } location)
            {
                current = new Uri(new Uri(current), location).ToString();
                continue;
            }

            var contentType = response.Content.Headers.ContentType?.MediaType ?? "";

            if (response.StatusCode != HttpStatusCode.OK) throw new GeminiException($"download returned HTTP {(int)response.StatusCode}");

            var data = await ReadLimitedAsync(response, maxBytes, $"downloaded media exceeds {maxBytes} byte limit", cts.Token);
            return (data, contentType);
        }

        throw new GeminiException("too many media redirects");
    }

    internal async Task<byte[]> DownloadGeneratedImageAsync(string rawUrl, string cookieHeader, CancellationToken cancellationToken)
    {
        var url = MediaUrls.AppendImageSize(rawUrl, 2048);

        if (!MediaUrls.IsTrustedMediaUrl(url)) throw new GeminiException("refusing generated-image download from untrusted host");

        var (data, contentType) = await DownloadMediaAsync(url, cookieHeader, MAX_GENERATED_IMAGE_BYTES, TimeSpan.FromMinutes(2), cancellationToken);

        if (!contentType.StartsWith("image/", StringComparison.Ordinal)) throw new GeminiException($"generated image download returned {contentType}");

        return data;
    }

    // ---------------------------------------------------------------- conversation read-back (hNvQHb)

    /// <summary>Reads the newest reply's candidates of a conversation, used to finish video/music jobs.</summary>
    internal async Task<(List<System.Text.Json.Nodes.JsonArray> Candidates, string Text)> ReadConversationAsync(string conversationId, CancellationToken cancellationToken)
    {
        var session = _session ?? throw new GeminiException("client not initialized");

        var payload  = Jspb.Serialize(Jspb.Of(conversationId, 10, null, 1, Jspb.Ints(1), Jspb.Ints(4), null, 1));
        var envelope = Jspb.Serialize(Jspb.Of(Jspb.Of(Jspb.Of(GeminiProtocol.READ_CONVERSATION_RPC, payload, null, "generic"))));

        var query = new Dictionary<string, string>
        {
            ["rpcids"]      = GeminiProtocol.READ_CONVERSATION_RPC,
            ["hl"]          = session.Language.Length > 0 ? session.Language : "en",
            ["_reqid"]      = GeminiProtocol.RandomRequestId(),
            ["rt"]          = "c",
            ["source-path"] = GeminiProtocol.SourcePath(AuthUser) + "/" + (conversationId.StartsWith("c_", StringComparison.Ordinal) ? conversationId[2..] : conversationId),
        };

        if (session.BuildLabel.Length > 0) query["bl"]    = session.BuildLabel;
        if (session.SessionId.Length > 0)  query["f.sid"] = session.SessionId;

        using var request = BatchRequest(query, new Dictionary<string, string> { ["at"] = session.AccessToken, ["f.req"] = envelope }, session.CookieHeader, webTool: true);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromMinutes(1));

        using var response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeout.Token);

        if (response.StatusCode != HttpStatusCode.OK) throw new GeminiException($"read conversation failed with HTTP status {(int)response.StatusCode}");

        var body = await ReadLimitedAsync(response, 32 << 20, "conversation response is too large", timeout.Token);
        return Video.ParseConversationCandidates(Encoding.UTF8.GetString(body));
    }
}
