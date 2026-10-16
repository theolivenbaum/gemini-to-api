namespace GeminiToApi.Configuration;

public sealed class GeminiOptions
{
    public string Cookies         { get; set; } = "";
    public string Secure1PSID     { get; set; } = "";
    public string Secure1PSIDTS   { get; set; } = "";
    public string AuthUser        { get; set; } = "";
    public int    RefreshInterval { get; set; } = AppConfig.DEFAULT_REFRESH_INTERVAL;
    public int    MaxRetries      { get; set; } = AppConfig.DEFAULT_MAX_RETRIES;
    public bool   Temporary       { get; set; }

    /// <summary>Continue the Gemini conversation a follow-up request belongs to instead of starting a new one.</summary>
    public bool   ContinueConversations { get; set; } = true;

    /// <summary>Folder for the rotated <c>__Secure-1PSIDTS</c> cache.</summary>
    public string CookieCacheDirectory { get; set; } = "";
}

public sealed class RateLimitOptions
{
    public bool Enabled     { get; set; }
    public int  WindowMs    { get; set; } = 60000;
    public int  MaxRequests { get; set; } = 10;
}

/// <summary>
/// Application configuration. Keeps the Go server's environment variable names. Sources, from
/// lowest to highest priority: defaults, <c>&lt;data dir&gt;/credentials.env</c> (written by the
/// <c>login</c> command), a <c>.env</c> file, process environment variables, command-line flags.
/// </summary>
public sealed class AppConfig
{
    public const int    DEFAULT_PORT             = 4981;
    public const int    DEFAULT_REFRESH_INTERVAL = 1440;
    public const int    MIN_REFRESH_INTERVAL     = 10; // rotating more often looks automated to Google
    public const int    DEFAULT_MAX_RETRIES      = 3;
    public const string CREDENTIALS_FILE         = "credentials.env";

    public GeminiOptions    Gemini    { get; } = new();
    public RateLimitOptions RateLimit { get; } = new();

    public string Host     { get; set; } = "localhost";
    public int    Port     { get; set; } = DEFAULT_PORT;
    public string LogLevel { get; set; } = "info";
    public string ApiKey   { get; set; } = "";
    public string DataDirectory { get; set; } = DefaultDataDirectory();

    public static string DefaultDataDirectory()
    {
        var configured = Environment.GetEnvironmentVariable("GEMINI_TO_API_HOME");

        if (!string.IsNullOrWhiteSpace(configured)) return configured;

        return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".gemini-to-api");
    }

    public static string CredentialsPath(string dataDirectory) => Path.Combine(dataDirectory, CREDENTIALS_FILE);

    /// <summary>Builds the configuration. <paramref name="overrides"/> holds command-line values keyed by env-var name.</summary>
    public static AppConfig Load(IReadOnlyDictionary<string, string>? overrides = null, string? envFile = null, IReadOnlyDictionary<string, string>? environment = null)
    {
        overrides ??= new Dictionary<string, string>();

        var values = new Dictionary<string, string>(StringComparer.Ordinal);

        var dataDirectory = overrides.TryGetValue("GEMINI_TO_API_HOME", out var dir) && !string.IsNullOrWhiteSpace(dir)
            ? dir
            : (environment is null ? DefaultDataDirectory() : environment.GetValueOrDefault("GEMINI_TO_API_HOME") ?? DefaultDataDirectory());

        foreach (var (k, v) in DotEnv.Read(CredentialsPath(dataDirectory))) values[k] = v;

        foreach (var (k, v) in DotEnv.Read(envFile ?? Path.Combine(Directory.GetCurrentDirectory(), ".env"))) values[k] = v;

        if (environment is null)
        {
            foreach (System.Collections.DictionaryEntry entry in Environment.GetEnvironmentVariables())
            {
                values[(string)entry.Key] = (string?)entry.Value ?? "";
            }
        }
        else
        {
            foreach (var (k, v) in environment) values[k] = v;
        }

        foreach (var (k, v) in overrides) values[k] = v;

        return FromValues(values, dataDirectory);
    }

    internal static AppConfig FromValues(IReadOnlyDictionary<string, string> values, string dataDirectory)
    {
        string Get(string key, string fallback) => values.TryGetValue(key, out var v) ? v : fallback;

        int GetInt(string key, int fallback) => values.TryGetValue(key, out var v) && int.TryParse(v.Trim(), out var n) ? n : fallback;

        bool GetBool(string key, bool fallback) => values.TryGetValue(key, out var v) && TryParseBool(v, out var b) ? b : fallback;

        var cfg = new AppConfig { DataDirectory = dataDirectory };

        cfg.Host     = Get("HOST", "localhost").Trim();
        cfg.LogLevel = Get("LOG_LEVEL", "info");
        cfg.ApiKey   = Get("API_KEY", "").Trim();

        var port = Get("PORT", DEFAULT_PORT.ToString()).Trim();

        if (port.Length == 0) port = DEFAULT_PORT.ToString();

        if (!int.TryParse(port, out var portNumber) || portNumber <= 0 || portNumber > 65535)
            throw new ConfigurationException($"invalid PORT value: \"{port}\" (must be a number)");

        cfg.Port = portNumber;

        cfg.RateLimit.Enabled     = GetBool("RATE_LIMIT_ENABLED", false);
        cfg.RateLimit.WindowMs    = GetInt("RATE_LIMIT_WINDOW_MS", 60000);
        cfg.RateLimit.MaxRequests = GetInt("RATE_LIMIT_MAX_REQUESTS", 10);

        var gemini = cfg.Gemini;

        gemini.Cookies       = Get("GEMINI_COOKIES", "");
        gemini.AuthUser      = Get("GEMINI_AUTH_USER", "").Trim();
        gemini.Secure1PSID   = CookieHeader.Value(gemini.Cookies, "__Secure-1PSID");
        gemini.Secure1PSIDTS = CookieHeader.Value(gemini.Cookies, "__Secure-1PSIDTS");
        gemini.MaxRetries    = GetInt("GEMINI_MAX_RETRIES", DEFAULT_MAX_RETRIES);
        gemini.Temporary     = GetBool("GEMINI_TEMPORARY", false);
        gemini.ContinueConversations = GetBool("GEMINI_CONTINUE_CONVERSATIONS", true);

        gemini.RefreshInterval = GetInt("GEMINI_REFRESH_INTERVAL", DEFAULT_REFRESH_INTERVAL);

        if (gemini.RefreshInterval <= 0) gemini.RefreshInterval = DEFAULT_REFRESH_INTERVAL;
        else if (gemini.RefreshInterval < MIN_REFRESH_INTERVAL) gemini.RefreshInterval = MIN_REFRESH_INTERVAL;

        if (gemini.AuthUser.Length > 0 && (!int.TryParse(gemini.AuthUser, out var slot) || slot < 0))
            throw new ConfigurationException($"invalid GEMINI_AUTH_USER value: \"{gemini.AuthUser}\" (must be a non-negative account slot)");

        gemini.CookieCacheDirectory = Path.Combine(dataDirectory, "cookies");

        return cfg;
    }

    /// <summary>Checks what the server needs to start (the <c>login</c> command does not need it).</summary>
    public void Validate()
    {
        if (string.IsNullOrWhiteSpace(Gemini.Cookies))
            throw new ConfigurationException(
                "missing required environment variables: [GEMINI_COOKIES]. Set it, put it in a .env file, " +
                "or run `gemini-to-api login` to capture it from your browser.");

        if (Gemini.Secure1PSID.Length == 0 || Gemini.Secure1PSIDTS.Length == 0)
            throw new ConfigurationException("GEMINI_COOKIES must contain __Secure-1PSID and __Secure-1PSIDTS");
    }

    // Accepts the same spellings as Go's strconv.ParseBool.
    private static bool TryParseBool(string value, out bool result)
    {
        switch (value.Trim())
        {
            case "1": case "t": case "T": case "TRUE": case "true": case "True": result = true; return true;
            case "0": case "f": case "F": case "FALSE": case "false": case "False": result = false; return true;
            default: result = false; return false;
        }
    }
}

public sealed class ConfigurationException(string message) : Exception(message);

/// <summary>Helpers for <c>Cookie</c> request headers.</summary>
public static class CookieHeader
{
    public static string Value(string header, string wanted)
    {
        foreach (var pair in header.Split(';'))
        {
            var trimmed = pair.Trim();
            var eq      = trimmed.IndexOf('=');

            if (eq < 0) continue;

            if (trimmed[..eq].Trim() == wanted) return trimmed[(eq + 1)..].Trim();
        }

        return "";
    }

    /// <summary>Merges headers left to right: later values win, first-seen order is kept.</summary>
    public static string Merge(params string[] headers)
    {
        var values = new Dictionary<string, string>(StringComparer.Ordinal);
        var order  = new List<string>();

        foreach (var header in headers)
        {
            if (string.IsNullOrEmpty(header)) continue;

            foreach (var pair in header.Split(';'))
            {
                var trimmed = pair.Trim();
                var eq      = trimmed.IndexOf('=');

                if (eq < 0) continue;

                var name = trimmed[..eq].Trim();

                if (name.Length == 0) continue;

                if (!values.ContainsKey(name)) order.Add(name);

                values[name] = trimmed[(eq + 1)..].Trim();
            }
        }

        return string.Join("; ", order.Select(n => n + "=" + values[n]));
    }

    /// <summary>Strips quotes, whitespace and a trailing semicolon from a pasted cookie value.</summary>
    public static string Clean(string value)
    {
        value = value.Trim().Trim('"').Trim('\'');
        return value.EndsWith(';') ? value[..^1] : value;
    }
}
