using System.Net;
using System.Text;
using Microsoft.Extensions.Logging;

namespace GeminiToApi.Gemini;

public sealed partial class GeminiClient
{
    private const string UPLOAD_AUTHORIZATION = "Basic c2F2ZXM6cyNMdGhlNmxzd2F2b0RsN3J1d1U=";

    internal string UploadEndpoint { get; set; } = GeminiProtocol.ENDPOINT_UPLOAD;

    private sealed class UploadHttpException(int status, string body, string? retryAfter)
        : GeminiException($"upload failed with status {status}: {body}")
    {
        public int     Status     { get; } = status;
        public string? RetryAfter { get; } = retryAfter;
    }

    private async Task<List<GeminiProtocol.UploadedFile>> UploadFilesAsync(IReadOnlyList<InputFile> files, SessionState session, CancellationToken ct)
    {
        var uploaded = new List<GeminiProtocol.UploadedFile>(files.Count);

        for (var i = 0; i < files.Count; i++)
        {
            var file     = files[i];
            var name     = string.IsNullOrWhiteSpace(file.Name) ? $"input_{i + 1}{MediaUrls.ExtensionForMimeType(file.MimeType)}" : file.Name.Trim();
            var mimeType = string.IsNullOrWhiteSpace(file.MimeType) ? DetectContentType(file.Data) : file.MimeType.Trim();

            uploaded.Add(await UploadFileAsync(name, mimeType, file.Data, session, ct));
        }

        return uploaded;
    }

    internal async Task<GeminiProtocol.UploadedFile> UploadFileAsync(string fileName, string mimeType, byte[] data, SessionState session, CancellationToken ct)
    {
        var maxAttempts = _maxRetries <= 0 ? 1 : _maxRetries;

        Exception? lastError = null;

        for (var attempt = 1; attempt <= maxAttempts; attempt++)
        {
            try
            {
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
                timeout.CancelAfter(TimeSpan.FromMinutes(2));

                return await ResumableUploadAsync(fileName, mimeType, data, session, timeout.Token);
            }
            catch (Exception e) when (IsRetryableUploadError(e, ct))
            {
                lastError = e;

                if (attempt == maxAttempts) break;

                var delay = UploadRetryDelay(e, attempt);
                _log.LogWarning("Retrying Gemini file upload {File} (attempt {Attempt}) in {Delay}: {Error}", fileName, attempt + 1, delay, e.Message);
                await Task.Delay(delay, ct);
            }
            catch (GeminiException e)
            {
                lastError = e;
                break;
            }
        }

        throw new GeminiException($"upload \"{fileName}\" failed after {maxAttempts} attempt(s): {lastError?.Message}", lastError);
    }

    private async Task<GeminiProtocol.UploadedFile> ResumableUploadAsync(string fileName, string mimeType, byte[] data, SessionState session, CancellationToken ct)
    {
        var headers = new Dictionary<string, string>
        {
            ["Accept"]                              = "*/*",
            ["Authorization"]                       = UPLOAD_AUTHORIZATION,
            ["Origin"]                              = "https://gemini.google.com",
            ["Referer"]                             = "https://gemini.google.com/",
            ["Push-ID"]                             = session.PushId.Length > 0 ? session.PushId : GeminiProtocol.DEFAULT_PUSH_ID,
            ["X-Goog-Upload-Command"]               = "start",
            ["X-Goog-Upload-Header-Content-Length"] = data.Length.ToString(),
            ["X-Goog-Upload-Protocol"]              = "resumable",
            ["X-Tenant-Id"]                         = "bard-storage",
            ["User-Agent"]                          = GeminiProtocol.USER_AGENT,
        };

        if (session.CookieHeader.Length > 0) headers["Cookie"] = session.CookieHeader;

        const string formType = "application/x-www-form-urlencoded;charset=UTF-8";

        (await SendUploadAsync(HttpMethod.Options, UploadEndpoint, [], formType, headers, ct)).Dispose();

        var startHeaders = new Dictionary<string, string>(headers) { ["Size"] = data.Length.ToString() };

        string uploadUrl;

        using (var start = await SendUploadAsync(HttpMethod.Post, UploadEndpoint, Encoding.UTF8.GetBytes("File name: " + fileName), formType, startHeaders, ct))
        {
            uploadUrl = start.Headers.TryGetValues("X-Goog-Upload-Url", out var values) ? values.First().Trim() : "";
        }

        if (uploadUrl.Length == 0) throw new GeminiException("start upload: Gemini did not return an upload URL");

        ValidateUploadUrl(uploadUrl, UploadEndpoint);

        (await SendUploadAsync(HttpMethod.Options, uploadUrl, [], formType, startHeaders, ct)).Dispose();

        var finalHeaders = new Dictionary<string, string>(startHeaders)
        {
            ["X-Goog-Upload-Command"] = "upload, finalize",
            ["X-Goog-Upload-Offset"]  = "0",
        };

        using var final = await SendUploadAsync(HttpMethod.Post, uploadUrl, data, mimeType, finalHeaders, ct);

        var body = await final.Content.ReadAsStringAsync(ct);
        var id   = (body.Length > 4096 ? body[..4096] : body).Trim();

        if (id.Length == 0) throw new GeminiException("upload returned empty file id");

        return new GeminiProtocol.UploadedFile(id, fileName);
    }

    private async Task<HttpResponseMessage> SendUploadAsync(HttpMethod method, string url, byte[] body, string contentType, Dictionary<string, string> headers, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(method, url) { Content = new ByteArrayContent(body) };

        request.Content.Headers.TryAddWithoutValidation("Content-Type", contentType);

        foreach (var (name, value) in headers) request.Headers.TryAddWithoutValidation(name, value);

        var response = await _httpManual.SendAsync(request, ct);

        if ((int)response.StatusCode is >= 200 and < 300) return response;

        using (response)
        {
            var text = await response.Content.ReadAsStringAsync(ct);

            if (text.Length > 4096) text = text[..4096];

            var retryAfter = response.Headers.RetryAfter?.ToString();

            throw (int)response.StatusCode is >= 300 and < 400
                ? new GeminiException("Gemini redirected an upload request; redirects are not followed")
                : new UploadHttpException((int)response.StatusCode, text.Trim(), retryAfter);
        }
    }

    internal static void ValidateUploadUrl(string raw, string endpoint)
    {
        if (!Uri.TryCreate(raw, UriKind.Absolute, out var url) || url.Host.Length == 0) throw new GeminiException("Gemini returned an invalid upload URL");
        if (!Uri.TryCreate(endpoint, UriKind.Absolute, out var baseUrl)) throw new GeminiException("invalid configured upload endpoint");

        if (baseUrl.Scheme == Uri.UriSchemeHttps)
        {
            if (url.Scheme != Uri.UriSchemeHttps) throw new GeminiException("Gemini returned an insecure upload URL");
        }
        else if (!string.Equals(url.Scheme, baseUrl.Scheme, StringComparison.OrdinalIgnoreCase))
        {
            throw new GeminiException("Gemini returned an upload URL with an unexpected scheme");
        }

        if (!string.Equals(url.Host, baseUrl.Host, StringComparison.OrdinalIgnoreCase)) throw new GeminiException("Gemini returned an upload URL on an untrusted host");
    }

    private static bool IsRetryableUploadError(Exception e, CancellationToken callerToken)
    {
        if (callerToken.IsCancellationRequested) return false;

        return e switch
        {
            UploadHttpException http => http.Status is 408 or 425 or 429 or 500 or 502 or 503 or 504,
            HttpRequestException     => true,
            TaskCanceledException    => true, // the per-attempt timeout
            _                        => false,
        };
    }

    private static TimeSpan UploadRetryDelay(Exception e, int attempt)
    {
        if (e is UploadHttpException { RetryAfter: { } retryAfter })
        {
            if (int.TryParse(retryAfter.Trim(), out var seconds) && seconds >= 0) return TimeSpan.FromSeconds(Math.Min(seconds, 60));

            if (DateTimeOffset.TryParse(retryAfter, out var at))
            {
                var wait = at - DateTimeOffset.UtcNow;
                return wait < TimeSpan.Zero ? TimeSpan.Zero : wait > TimeSpan.FromMinutes(1) ? TimeSpan.FromMinutes(1) : wait;
            }
        }

        return TimeSpan.FromSeconds(Math.Min(1 << (attempt - 1), 30));
    }

    /// <summary>Content sniffing for the common upload types (Go: http.DetectContentType).</summary>
    internal static string DetectContentType(byte[] data)
    {
        ReadOnlySpan<byte> d = data;

        if (d.StartsWith("\x89PNG"u8)) return "image/png";
        if (d.StartsWith(new byte[] { 0xFF, 0xD8, 0xFF })) return "image/jpeg";
        if (d.StartsWith("GIF8"u8)) return "image/gif";
        if (d.Length >= 12 && d.StartsWith("RIFF"u8) && d.Slice(8, 4).SequenceEqual("WEBP"u8)) return "image/webp";
        if (d.StartsWith("%PDF-"u8)) return "application/pdf";
        if (Video.IsMp4(d)) return "video/mp4";

        return d.IndexOfAnyInRange((byte)0, (byte)8) < 0 ? "text/plain; charset=utf-8" : "application/octet-stream";
    }
}
