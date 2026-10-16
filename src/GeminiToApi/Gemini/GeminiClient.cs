using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using GeminiToApi.Configuration;
using Microsoft.Extensions.Logging;

namespace GeminiToApi.Gemini;

/// <summary>
/// Client for the Gemini web app, authenticated with the browser cookies of a signed-in Google
/// account. Owns the session (access token, build label, model registry), keeps the
/// <c>__Secure-1PSIDTS</c> cookie rotated, and sends StreamGenerate / batchexecute requests.
/// </summary>
public sealed partial class GeminiClient : IDisposable
{
    internal sealed record SessionState(
        string                     AccessToken,
        string                     CookieHeader,
        string                     PushId,
        string                     BuildLabel,
        string                     SessionId,
        string                     Language,
        IReadOnlyList<GeminiModel> Models);

    [GeneratedRegex("\"SNlM0e\":\"([^\"]+)\"")]      private static partial Regex AccessTokenRegex();
    [GeneratedRegex("\\[\"SNlM0e\",\"([^\"]+)\"\\]")] private static partial Regex AccessTokenFallbackRegex();
    [GeneratedRegex("\"qKIAYe\":\"([^\"]+)\"")]      private static partial Regex PushIdRegex();
    [GeneratedRegex("\"cfb2h\":\"([^\"]+)\"")]       private static partial Regex BuildLabelRegex();
    [GeneratedRegex("\"FdrFJe\":\"([^\"]+)\"")]      private static partial Regex SessionIdRegex();
    [GeneratedRegex("\"TuX5cc\":\"([^\"]+)\"")]      private static partial Regex LanguageRegex();

    private const int MAX_MODEL_RESPONSE_BYTES = 8 << 20;

    private readonly ILogger         _log;
    private readonly HttpClient      _http;           // follows redirects (navigation, RPCs)
    private readonly HttpClient      _httpManual;     // never follows redirects (uploads, media downloads)
    private readonly SemaphoreSlim   _cookieLock = new(1, 1);
    private readonly string          _configuredCookieHeader;
    private readonly string          _cookieCacheDirectory;
    private readonly int             _maxRetries;
    private readonly bool            _temporary;
    private readonly TimeSpan        _refreshInterval;
    private readonly CancellationTokenSource _shutdown = new();

    private string                   _psid;
    private string                   _psidts;
    private volatile SessionState?   _session;
    private volatile bool            _healthy;
    private VideoJobStore?           _videoJobs;

    internal string AuthUser     { get; }
    internal string GenerationId { get; } = Guid.NewGuid().ToString().ToUpperInvariant();

    /// <summary>Poll interval for video/music jobs that finish after the stream ends.</summary>
    internal TimeSpan MediaPollInterval { get; set; } = TimeSpan.FromSeconds(10);

    public GeminiClient(GeminiOptions options, ILogger<GeminiClient> log, HttpMessageHandler? handler = null)
    {
        _log                    = log;
        _configuredCookieHeader = options.Cookies.Trim();
        _psid                   = CookieHeader.Clean(options.Secure1PSID);
        _psidts                 = CookieHeader.Clean(options.Secure1PSIDTS);
        _cookieCacheDirectory   = options.CookieCacheDirectory;
        _maxRetries             = options.MaxRetries;
        _temporary              = options.Temporary;
        _refreshInterval        = TimeSpan.FromMinutes(options.RefreshInterval > 0 ? options.RefreshInterval : AppConfig.DEFAULT_REFRESH_INTERVAL);
        AuthUser                = options.AuthUser.Trim();

        if (handler is null)
        {
            _http       = new HttpClient(CreateHandler(allowRedirects: true)) { Timeout = Timeout.InfiniteTimeSpan };
            _httpManual = new HttpClient(CreateHandler(allowRedirects: false)) { Timeout = Timeout.InfiniteTimeSpan };
        }
        else
        {
            _http       = new HttpClient(handler, disposeHandler: false) { Timeout = Timeout.InfiniteTimeSpan };
            _httpManual = _http;
        }
    }

    private static SocketsHttpHandler CreateHandler(bool allowRedirects) => new()
    {
        UseCookies               = false, // cookies always travel as an explicit Cookie header
        AllowAutoRedirect        = allowRedirects,
        AutomaticDecompression   = DecompressionMethods.All,
        PooledConnectionLifetime = TimeSpan.FromMinutes(10),
    };

    public bool IsHealthy => _healthy;

    internal SessionState? Session => _session;

    /// <summary>Test hook: installs a session without talking to Google.</summary>
    internal void SetSessionForTesting(SessionState session)
    {
        _session = session;
        _healthy = true;
    }

    public IReadOnlyList<ModelInfo> ListModels() => _session?.Models.Select(m => m.Info).ToList() ?? [];

    /// <summary>Validates a public model name (with optional -canvas / -music suffix) against the registry.</summary>
    public ModelInfo ResolveModel(string? requested)
    {
        var (withoutCanvas, canvas) = Canvas.SplitModel(requested);
        var (baseModel, music)      = Music.SplitModel(withoutCanvas);

        var info = GeminiModels.Resolve(baseModel, _session?.Models ?? []).Info;

        if (canvas) info = info with { Id = info.Id + Canvas.MODEL_SUFFIX, DisplayName = info.DisplayName.Length > 0 ? info.DisplayName + " (Canvas)" : "" };
        if (music)  info = info with { Id = info.Id + Music.MODEL_SUFFIX, DisplayName = info.DisplayName.Length > 0 ? info.DisplayName + " (Music)" : "" };

        return info;
    }

    // ---------------------------------------------------------------- lifecycle

    /// <summary>Initializes the session, then keeps it alive until <paramref name="cancellationToken"/> fires.</summary>
    public async Task RunAsync(CancellationToken cancellationToken)
    {
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _shutdown.Token);

        try
        {
            await InitAsync(linked.Token);
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            _log.LogError(
                "Gemini provider initialization failed: {Error}. Check GEMINI_COOKIES (expired or incomplete?), GEMINI_AUTH_USER " +
                "(must match the /u/<number>/ account slot), or run `gemini-to-api login` to capture a fresh cookie.", e.Message);
        }

        using var timer = new PeriodicTimer(_refreshInterval);

        while (await timer.WaitForNextTickAsync(linked.Token))
        {
            await RefreshSessionAsync(linked.Token);
        }
    }

    public async Task InitAsync(CancellationToken cancellationToken = default)
    {
        var configPsidts = _psidts;

        if (_psid.Length > 0)
        {
            var cached = LoadCachedPsidts();

            if (configPsidts.Length > 0 && cached.Length > 0 && configPsidts != cached)
            {
                ClearCookieCache(); // a new cookie was configured: it wins over the cache
            }
            else if (cached.Length > 0 && configPsidts.Length == 0)
            {
                _psidts = cached;
                _log.LogInformation("Loaded __Secure-1PSIDTS from cache");
            }
        }

        if (_psid.Length > 0 && _psidts.Length == 0)
        {
            _log.LogInformation("Only __Secure-1PSID provided, attempting to obtain __Secure-1PSIDTS via rotation...");

            try
            {
                await RotateCookiesAsync(cancellationToken);
            }
            catch (GeminiException e)
            {
                _log.LogInformation("Rotation failed, proceeding with just __Secure-1PSID (might fail): {Error}", e.Message);
            }
        }

        try
        {
            await RefreshSessionTokenAsync(cancellationToken);
        }
        catch (GeminiException first)
        {
            _log.LogDebug("Initial session token fetch failed ({Error}), attempting cookie rotation", first.Message);

            try
            {
                await RotateCookiesAsync(cancellationToken);
            }
            catch (GeminiException rotationError)
            {
                _log.LogDebug("Cookie rotation failed: {Error}", rotationError.Message);
                throw first;
            }

            await RefreshSessionTokenAsync(cancellationToken);
        }

        SaveCachedPsidts();
        _log.LogInformation("Gemini client initialized successfully");
    }

    /// <summary>
    /// Scheduled refresh: rotate the cookie, then always verify the session against Gemini, because
    /// RotateCookies can reject a session that Gemini still accepts. Healthy if either succeeded.
    /// </summary>
    internal async Task RefreshSessionAsync(CancellationToken cancellationToken)
    {
        _log.LogDebug("Starting scheduled cookie refresh");

        Exception? rotateError  = null;
        Exception? sessionError = null;

        try { await RotateCookiesAsync(cancellationToken); }
        catch (Exception e) when (e is not OperationCanceledException) { rotateError = e; }

        try { await RefreshSessionTokenAsync(cancellationToken); }
        catch (Exception e) when (e is not OperationCanceledException) { sessionError = e; }

        if (rotateError is not null) _log.LogWarning("Cookie rotation failed; verified the Gemini session separately: {Error}", rotateError.Message);
        if (sessionError is not null) _log.LogWarning("Gemini session token refresh failed: {Error}", sessionError.Message);

        _healthy = rotateError is null || sessionError is null;

        if (_healthy)
        {
            _log.LogInformation("Gemini session refresh completed (cookie_rotated={Rotated}, session_token_refreshed={Refreshed})", rotateError is null, sessionError is null);
        }
        else
        {
            _log.LogError("Cookie rotation and session verification both failed; marking client unhealthy. " +
                          "Visit https://gemini.google.com -> F12 -> Network and copy the complete Cookie request header, or run `gemini-to-api login`.");
        }
    }

    // ---------------------------------------------------------------- session bootstrap

    internal async Task RefreshSessionTokenAsync(CancellationToken cancellationToken)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(60));
        var ct = timeout.Token;

        // 1. google.com hands out extra cookies (NID, …).
        var extraCookies = "";

        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, GeminiProtocol.ENDPOINT_GOOGLE);
            request.Headers.TryAddWithoutValidation("User-Agent", GeminiProtocol.USER_AGENT);

            using var response = await _http.SendAsync(request, ct);
            extraCookies = string.Join("; ", SetCookies(response).Select(c => c.Name + "=" + c.Value));
        }
        catch (HttpRequestException)
        {
            // Optional step, like in the Go client.
        }

        // 2. The full browser header carries rollout and account-selection cookies that PSID/PSIDTS
        //    alone may not reproduce; the explicit auth values win.
        string psid, psidts;

        await _cookieLock.WaitAsync(ct);

        try
        {
            psid   = _psid;
            psidts = _psidts;
        }
        finally
        {
            _cookieLock.Release();
        }

        var cookieHeader = CookieHeader.Merge(extraCookies, _configuredCookieHeader, $"__Secure-1PSID={psid}; __Secure-1PSIDTS={psidts}");

        using (var request = NavigationRequest("https://gemini.google.com/?hl=en", cookieHeader, sameOrigin: false))
        {
            try
            {
                using var response = await _http.SendAsync(request, ct);
                cookieHeader = MergeSetCookies(cookieHeader, response);
            }
            catch (HttpRequestException)
            {
                // Optional step, like in the Go client.
            }
        }

        string body;

        using (var request = NavigationRequest(GeminiProtocol.AccountUrl(GeminiProtocol.ENDPOINT_INIT, AuthUser) + "?hl=en", cookieHeader, sameOrigin: true))
        {
            HttpResponseMessage response;

            try
            {
                response = await _http.SendAsync(request, ct);
            }
            catch (HttpRequestException e)
            {
                throw new GeminiException("failed to reach gemini app: " + e.Message, e);
            }

            using (response)
            {
                body         = await response.Content.ReadAsStringAsync(ct);
                cookieHeader = MergeSetCookies(cookieHeader, response);
            }
        }

        var token = AccessTokenRegex().Match(body);

        if (!token.Success) token = AccessTokenFallbackRegex().Match(body);

        if (!token.Success)
        {
            var message = body.Contains("Sign in") || body.Contains("login")
                ? "authentication failed: GEMINI_COOKIES is invalid or expired; copy the complete Cookie header from Gemini Web (or run `gemini-to-api login`)"
                : "authentication failed: SNlM0e not found";

            _log.LogInformation("{Message}", message);
            throw new GeminiException(message);
        }

        string Find(Regex regex, string fallback) => regex.Match(body) is { Success: true } m ? m.Groups[1].Value : fallback;

        var accessToken = token.Groups[1].Value;
        var pushId      = Find(PushIdRegex(), GeminiProtocol.DEFAULT_PUSH_ID);
        var buildLabel  = Find(BuildLabelRegex(), "");
        var sessionId   = Find(SessionIdRegex(), "");
        var language    = Find(LanguageRegex(), "en");

        IReadOnlyList<GeminiModel> models;

        try
        {
            models = await FetchModelsAsync(accessToken, cookieHeader, buildLabel, sessionId, language, ct);
        }
        catch (GeminiException e)
        {
            throw new GeminiException("failed to discover Gemini models: " + e.Message, e);
        }

        _session = new SessionState(accessToken, cookieHeader, pushId, buildLabel, sessionId, language, models);
        _healthy = true;

        _log.LogInformation("Refreshed available models from Gemini Web: {Models}", string.Join(", ", models.Select(m => m.Id)));
    }

    private static HttpRequestMessage NavigationRequest(string url, string cookieHeader, bool sameOrigin)
    {
        var request = new HttpRequestMessage(HttpMethod.Get, url);

        void Set(string name, string value) => request.Headers.TryAddWithoutValidation(name, value);

        Set("Accept", "text/html,application/xhtml+xml,application/xml;q=0.9,image/avif,image/webp,image/apng,*/*;q=0.8,application/signed-exchange;v=b3;q=0.7");
        Set("Accept-Language", "en-US,en;q=0.9");
        Set("Cache-Control", "max-age=0");
        Set("Origin", "https://gemini.google.com");
        Set("Sec-Ch-Ua", "\"Not_A Brand\";v=\"8\", \"Chromium\";v=\"120\", \"Google Chrome\";v=\"120\"");
        Set("Sec-Ch-Ua-Mobile", "?0");
        Set("Sec-Ch-Ua-Platform", "\"Windows\"");
        Set("Sec-Fetch-Dest", "document");
        Set("Sec-Fetch-Mode", "navigate");
        Set("Sec-Fetch-Site", sameOrigin ? "same-origin" : "none");
        Set("Sec-Fetch-User", "?1");
        Set("Upgrade-Insecure-Requests", "1");
        Set("X-Same-Domain", "1");
        Set("User-Agent", GeminiProtocol.USER_AGENT);
        Set("Cookie", cookieHeader);

        if (sameOrigin) Set("Referer", "https://gemini.google.com/");

        return request;
    }

    internal async Task<List<GeminiModel>> FetchModelsAsync(string accessToken, string cookieHeader, string buildLabel, string sessionId, string language, CancellationToken ct)
    {
        if (accessToken.Length == 0 || cookieHeader.Length == 0) throw new GeminiException("Gemini model discovery requires an authenticated session");

        if (language.Length == 0) language = "en";

        var query = new Dictionary<string, string>
        {
            ["rpcids"]      = GeminiModels.USER_STATUS_RPC,
            ["hl"]          = language,
            ["_reqid"]      = GeminiProtocol.RandomRequestId(),
            ["rt"]          = "c",
            ["source-path"] = GeminiProtocol.SourcePath(AuthUser),
        };

        if (buildLabel.Length > 0) query["bl"]    = buildLabel;
        if (sessionId.Length > 0)  query["f.sid"] = sessionId;

        var form = new Dictionary<string, string>
        {
            ["at"]    = accessToken,
            ["f.req"] = "[[[\"otAQ7b\",\"[]\",null,\"generic\"]]]",
        };

        using var request = BatchRequest(query, form, cookieHeader, webTool: false);

        HttpResponseMessage response;

        try
        {
            response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
        }
        catch (HttpRequestException e)
        {
            // Never echo the request URL: it carries session values.
            throw new GeminiException("Gemini model discovery request failed: " + (e.InnerException?.Message ?? e.Message), e);
        }

        using (response)
        {
            if (response.StatusCode != HttpStatusCode.OK)
                throw new GeminiException($"Gemini model discovery failed with HTTP status {(int)response.StatusCode}");

            var body = await ReadLimitedAsync(response, MAX_MODEL_RESPONSE_BYTES, "Gemini model discovery response is too large", ct);
            return GeminiModels.Parse(Encoding.UTF8.GetString(body));
        }
    }

    internal HttpRequestMessage BatchRequest(Dictionary<string, string> query, Dictionary<string, string> form, string cookieHeader, bool webTool)
    {
        var url     = GeminiProtocol.AccountUrl(GeminiProtocol.ENDPOINT_BATCH_EXEC, AuthUser) + "?" + GeminiProtocol.FormEncode(query);
        var request = new HttpRequestMessage(HttpMethod.Post, url)
        {
            Content = FormContent(form),
        };

        foreach (var (name, value) in GeminiProtocol.DefaultHeaders) request.Headers.TryAddWithoutValidation(name, value);

        request.Headers.TryAddWithoutValidation("Cookie", cookieHeader);
        request.Headers.TryAddWithoutValidation(GeminiProtocol.MODEL_HEADER_KEY, GeminiProtocol.BuildBatchHeader(GenerationId, webTool));
        request.Headers.TryAddWithoutValidation("x-goog-ext-73010989-jspb", "[0]");

        return request;
    }

    internal static StringContent FormContent(IEnumerable<KeyValuePair<string, string>> form)
    {
        var content = new StringContent(GeminiProtocol.FormEncode(form), Encoding.UTF8);
        content.Headers.Remove("Content-Type");
        content.Headers.TryAddWithoutValidation("Content-Type", GeminiProtocol.FORM_CONTENT_TYPE);
        return content;
    }

    // ---------------------------------------------------------------- cookie rotation & cache

    public async Task RotateCookiesAsync(CancellationToken cancellationToken = default)
    {
        await _cookieLock.WaitAsync(cancellationToken);

        try
        {
            var parts = new List<string>();

            if (_psid.Length > 0) parts.Add("__Secure-1PSID=" + _psid);
            if (_psidts.Length > 0) parts.Add("__Secure-1PSIDTS=" + _psidts);

            using var request = new HttpRequestMessage(HttpMethod.Post, GeminiProtocol.ENDPOINT_ROTATE_COOKIES)
            {
                Content = new StringContent("[000,\"-0000000000000000000\"]", Encoding.UTF8, "application/json"),
            };

            request.Headers.TryAddWithoutValidation("User-Agent", GeminiProtocol.USER_AGENT);
            request.Headers.TryAddWithoutValidation("Cookie", string.Join("; ", parts));

            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(TimeSpan.FromSeconds(5));

            HttpResponseMessage response;

            try
            {
                response = await _http.SendAsync(request, timeout.Token);
            }
            catch (Exception e) when (e is HttpRequestException || e is TaskCanceledException && !cancellationToken.IsCancellationRequested)
            {
                _log.LogInformation("Rotation request failed (network/auth issue): {Error}", e.Message);
                throw new GeminiException("failed to call rotation endpoint: " + e.Message, e);
            }

            using (response)
            {
                if (response.StatusCode != HttpStatusCode.OK)
                {
                    _log.LogInformation("Rotation failed (likely invalid __Secure-1PSID), status {Status}", (int)response.StatusCode);
                    throw new GeminiException($"rotation failed with status {(int)response.StatusCode}");
                }

                var rotated = SetCookies(response).LastOrDefault(c => c.Name == "__Secure-1PSIDTS");

                if (rotated.Name is null)
                {
                    // 200 without a new cookie: the current one is still valid.
                    _log.LogDebug("No new __Secure-1PSIDTS issued; existing cookie is still valid");
                    return;
                }

                _psidts = rotated.Value;
                SaveCachedPsidts();
                _log.LogInformation("Cookie rotated successfully");
            }
        }
        finally
        {
            _cookieLock.Release();
        }
    }

    private string CacheFile()
    {
        var hash = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(_psid)));
        return Path.Combine(_cookieCacheDirectory, hash + ".txt");
    }

    internal string LoadCachedPsidts()
    {
        if (_psid.Length == 0 || _cookieCacheDirectory.Length == 0) return "";

        try
        {
            return File.Exists(CacheFile()) ? File.ReadAllText(CacheFile()).Trim() : "";
        }
        catch (IOException)
        {
            return "";
        }
    }

    internal void SaveCachedPsidts()
    {
        if (_psid.Length == 0 || _psidts.Length == 0 || _cookieCacheDirectory.Length == 0) return;

        try
        {
            Directory.CreateDirectory(_cookieCacheDirectory);

            var file = CacheFile();
            File.WriteAllText(file, _psidts);

            if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(file, UnixFileMode.UserRead | UnixFileMode.UserWrite);

            _log.LogDebug("Saved __Secure-1PSIDTS to local cache for future use: {File}", file);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            _log.LogWarning("Failed to save cookies to cache: {Error}", e.Message);
        }
    }

    private void ClearCookieCache()
    {
        if (_psid.Length == 0 || _cookieCacheDirectory.Length == 0) return;

        try
        {
            File.Delete(CacheFile());
        }
        catch (IOException e)
        {
            _log.LogDebug("Failed to clear the cookie cache: {Error}", e.Message);
        }
    }

    // ---------------------------------------------------------------- helpers

    internal static List<(string Name, string Value)> SetCookies(HttpResponseMessage response)
    {
        var cookies = new List<(string, string)>();

        if (!response.Headers.TryGetValues("Set-Cookie", out var values)) return cookies;

        foreach (var header in values)
        {
            var pair = header.Split(';', 2)[0];
            var eq   = pair.IndexOf('=');

            if (eq <= 0) continue;

            cookies.Add((pair[..eq].Trim(), pair[(eq + 1)..].Trim()));
        }

        return cookies;
    }

    private static string MergeSetCookies(string cookieHeader, HttpResponseMessage response)
    {
        var cookies = SetCookies(response);
        return cookies.Count == 0 ? cookieHeader : CookieHeader.Merge(cookieHeader, string.Join("; ", cookies.Select(c => c.Name + "=" + c.Value)));
    }

    internal static async Task<byte[]> ReadLimitedAsync(HttpResponseMessage response, int maxBytes, string tooLargeMessage, CancellationToken ct)
    {
        await using var stream = await response.Content.ReadAsStreamAsync(ct);
        using var buffer       = new MemoryStream();

        var chunk = new byte[81920];
        int read;

        while ((read = await stream.ReadAsync(chunk, ct)) > 0)
        {
            buffer.Write(chunk, 0, read);

            if (buffer.Length > maxBytes) throw new GeminiException(tooLargeMessage);
        }

        return buffer.ToArray();
    }

    public void Dispose()
    {
        _shutdown.Cancel();
        _videoJobs?.Dispose();
        _healthy = false;

        if (!ReferenceEquals(_http, _httpManual)) _httpManual.Dispose();

        _http.Dispose();
        _shutdown.Dispose();
    }
}
