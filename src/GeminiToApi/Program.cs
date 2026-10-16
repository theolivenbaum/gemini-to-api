using System.Reflection;
using GeminiToApi.Configuration;
using GeminiToApi.Gemini;
using GeminiToApi.Hosting;
using GeminiToApi.Login;

namespace GeminiToApi;

public static class Program
{
    private const string USAGE = """
        gemini-to-api - OpenAI, Claude and Gemini compatible APIs backed by the Gemini web app

        Usage:
          gemini-to-api [serve] [options]   Start the API server (default command)
          gemini-to-api login [options]     Capture the Gemini cookie from your browser (Windows)
          gemini-to-api --version | --help

        Server options (each also has an environment variable; flags win):
          --port <port>              PORT                     (default 4981)
          --host <address>           HOST                     (default localhost; 0.0.0.0 for all interfaces)
          --cookies <header>         GEMINI_COOKIES           full Cookie header from gemini.google.com
          --auth-user <n>            GEMINI_AUTH_USER         account slot from /u/<n>/ URLs
          --refresh-interval <min>   GEMINI_REFRESH_INTERVAL  (default 1440, minimum 10)
          --max-retries <n>          GEMINI_MAX_RETRIES       (default 3)
          --temporary                GEMINI_TEMPORARY=true    don't keep conversations in Gemini history
          --api-key <key>            API_KEY                  require this key from API clients
          --log-level <level>        LOG_LEVEL                debug | info | warn | error
          --env-file <path>          read settings from this file instead of ./.env
          --data-dir <path>          GEMINI_TO_API_HOME       (default ~/.gemini-to-api)

        Login options:
          --browser <name>           chrome | edge | firefox  (default: first installed, in that order)
          --browser-path <path>      use this browser executable (any OS)
          --no-auto-message          never send a message; wait for you to use Gemini instead
          --message <text>           message sent to trigger a request (default "What's 1+1?")
          --timeout <minutes>        give up after this long (default 10)
          --browser-args "<args>"    extra command-line arguments for the browser (e.g. a proxy)
          --keep-open                leave the browser running afterwards
          --no-verify                don't test the captured cookie against Gemini
          --print                    also print the captured values

        Credentials captured by `login` are stored in <data dir>/credentials.env and used by `serve`.
        """;

    public static async Task<int> Main(string[] args)
    {
        var command = args.Length > 0 && !args[0].StartsWith('-') ? args[0].ToLowerInvariant() : "serve";
        var rest    = args.Length > 0 && !args[0].StartsWith('-') ? args[1..] : args;

        if (args.Contains("--help") || args.Contains("-h") || command == "help")
        {
            Console.WriteLine(USAGE);
            return 0;
        }

        if (args.Contains("--version"))
        {
            Console.WriteLine(typeof(Program).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? "unknown");
            return 0;
        }

        try
        {
            var parsed = CommandLine.Parse(rest);

            return command switch
            {
                "serve" => await ServeAsync(parsed),
                "login" => await LoginAsync(parsed),
                _       => throw new ArgumentException($"unknown command \"{command}\"; run gemini-to-api --help"),
            };
        }
        catch (Exception e) when (e is ArgumentException or ConfigurationException or LoginException)
        {
            Console.Error.WriteLine("Error: " + e.Message);
            return 1;
        }
    }

    private static AppConfig LoadConfig(CommandLine parsed)
    {
        var overrides = new Dictionary<string, string>();

        void Map(string flag, string env)
        {
            if (parsed.Values.TryGetValue(flag, out var value)) overrides[env] = value;
        }

        Map("port", "PORT");
        Map("host", "HOST");
        Map("cookies", "GEMINI_COOKIES");
        Map("auth-user", "GEMINI_AUTH_USER");
        Map("refresh-interval", "GEMINI_REFRESH_INTERVAL");
        Map("max-retries", "GEMINI_MAX_RETRIES");
        Map("api-key", "API_KEY");
        Map("log-level", "LOG_LEVEL");
        Map("data-dir", "GEMINI_TO_API_HOME");

        if (parsed.Flags.Contains("temporary")) overrides["GEMINI_TEMPORARY"] = "true";

        return AppConfig.Load(overrides, parsed.Values.GetValueOrDefault("env-file"));
    }

    private static async Task<int> ServeAsync(CommandLine parsed)
    {
        parsed.EnsureOnly("port", "host", "cookies", "auth-user", "refresh-interval", "max-retries", "temporary", "api-key", "log-level", "env-file", "data-dir");

        var config = LoadConfig(parsed);

        try
        {
            config.Validate();
        }
        catch (ConfigurationException) when (string.IsNullOrWhiteSpace(config.Gemini.Cookies))
        {
            Console.Error.WriteLine("No Gemini cookie is configured.");
            Console.Error.WriteLine(OperatingSystem.IsWindows()
                ? "Run `gemini-to-api login` to capture it from your browser, or set GEMINI_COOKIES."
                : "Set GEMINI_COOKIES to the full Cookie header of a gemini.google.com request (see --help).");
            return 1;
        }

        var app = Server.Build(config);

        Console.WriteLine($"Gemini To API listening on http://{(config.Host is "0.0.0.0" or "*" or "+" ? "localhost" : config.Host)}:{config.Port}  (docs: /docs)");

        await app.RunAsync();
        return 0;
    }

    private static async Task<int> LoginAsync(CommandLine parsed)
    {
        parsed.EnsureOnly("browser", "browser-path", "browser-args", "no-auto-message", "message", "timeout", "keep-open", "no-verify", "print", "headless", "data-dir", "env-file");

        var dataDirectory = parsed.Values.GetValueOrDefault("data-dir") ?? AppConfig.DefaultDataDirectory();

        var options = new LoginOptions
        {
            Browser       = parsed.Values.TryGetValue("browser", out var browser) ? BrowserLocator.ParseKind(browser) : null,
            BrowserPath   = parsed.Values.GetValueOrDefault("browser-path"),
            AutoMessage   = !parsed.Flags.Contains("no-auto-message"),
            KeepOpen      = parsed.Flags.Contains("keep-open"),
            Headless      = parsed.Flags.Contains("headless"),
            DataDirectory = dataDirectory,
            BrowserArgs   = parsed.Values.TryGetValue("browser-args", out var browserArgs) ? CommandLine.SplitArguments(browserArgs) : new(),
        };

        if (parsed.Values.TryGetValue("message", out var message) && message.Trim().Length > 0) options.Message = message;

        if (parsed.Values.TryGetValue("timeout", out var timeout))
        {
            if (!double.TryParse(timeout, System.Globalization.CultureInfo.InvariantCulture, out var minutes) || minutes <= 0)
                throw new ArgumentException("--timeout must be a positive number of minutes");

            options.Timeout = TimeSpan.FromMinutes(minutes);
        }

        using var cancel = new CancellationTokenSource();

        Console.CancelKeyPress += (_, e) =>
        {
            e.Cancel = true;
            cancel.Cancel();
        };

        CapturedSession session;

        try
        {
            session = await new CookieCapture(options, Console.Out).RunAsync(cancel.Token);
        }
        catch (OperationCanceledException)
        {
            Console.Error.WriteLine(cancel.IsCancellationRequested ? "Login cancelled." : $"Timed out after {options.Timeout.TotalMinutes:0} minutes without capturing a Gemini session.");
            return 1;
        }
        catch (Microsoft.Playwright.PlaywrightException e)
        {
            Console.Error.WriteLine("Browser automation failed: " + e.Message);
            return 1;
        }

        var path = CookieCapture.Save(session, dataDirectory);

        Console.WriteLine($"Captured the Gemini session from a {session.Source}{(session.AuthUser.Length > 0 ? $" (account slot /u/{session.AuthUser}/)" : "")}.");
        Console.WriteLine("Saved to " + path);

        if (parsed.Flags.Contains("print"))
        {
            Console.WriteLine("GEMINI_COOKIES=" + session.CookieHeader);
            Console.WriteLine("GEMINI_AUTH_USER=" + session.AuthUser);
        }

        if (!parsed.Flags.Contains("no-verify")) await VerifyAsync(session, dataDirectory);

        Console.WriteLine("Start the server with: gemini-to-api");
        return 0;
    }

    private static async Task VerifyAsync(CapturedSession session, string dataDirectory)
    {
        Console.WriteLine("Verifying the session with Gemini...");

        var config = AppConfig.FromValues(new Dictionary<string, string>
        {
            ["GEMINI_COOKIES"]   = session.CookieHeader,
            ["GEMINI_AUTH_USER"] = session.AuthUser,
        }, dataDirectory);

        using var loggerFactory = LoggerFactory.Create(b => b.AddSimpleConsole(o => o.SingleLine = true).SetMinimumLevel(LogLevel.Warning));
        using var client        = new GeminiClient(config.Gemini, loggerFactory.CreateLogger<GeminiClient>());

        try
        {
            await client.InitAsync();
            Console.WriteLine("OK. Available models: " + string.Join(", ", client.ListModels().Select(m => m.Id)));
        }
        catch (Exception e) when (e is GeminiException or HttpRequestException or TaskCanceledException)
        {
            Console.Error.WriteLine("Warning: the captured cookie did not pass verification: " + e.Message);
        }
    }
}

/// <summary>Tiny flag parser: <c>--name value</c>, <c>--name=value</c> and boolean <c>--flag</c>.</summary>
internal sealed class CommandLine
{
    private static readonly HashSet<string> BooleanFlags = ["temporary", "no-auto-message", "keep-open", "no-verify", "print", "headless"];

    public Dictionary<string, string> Values { get; } = new(StringComparer.Ordinal);
    public HashSet<string>            Flags  { get; } = new(StringComparer.Ordinal);

    public static CommandLine Parse(string[] args)
    {
        var parsed = new CommandLine();

        for (var i = 0; i < args.Length; i++)
        {
            var arg = args[i];

            if (!arg.StartsWith("--", StringComparison.Ordinal)) throw new ArgumentException($"unexpected argument \"{arg}\"");

            var name = arg[2..];
            var eq   = name.IndexOf('=');

            if (eq >= 0)
            {
                parsed.Values[name[..eq]] = name[(eq + 1)..];
            }
            else if (BooleanFlags.Contains(name))
            {
                parsed.Flags.Add(name);
            }
            else
            {
                if (i + 1 >= args.Length) throw new ArgumentException($"--{name} needs a value");

                parsed.Values[name] = args[++i];
            }
        }

        return parsed;
    }

    /// <summary>Splits on spaces, keeping "double-quoted parts" together (quotes removed).</summary>
    public static List<string> SplitArguments(string value)
    {
        var result  = new List<string>();
        var current = new System.Text.StringBuilder();
        var quoted  = false;

        foreach (var ch in value)
        {
            if (ch == '"') quoted = !quoted;
            else if (ch == ' ' && !quoted)
            {
                if (current.Length > 0) result.Add(current.ToString());
                current.Clear();
            }
            else current.Append(ch);
        }

        if (current.Length > 0) result.Add(current.ToString());

        return result;
    }

    public void EnsureOnly(params string[] allowed)
    {
        foreach (var name in Values.Keys.Concat(Flags))
        {
            if (!allowed.Contains(name)) throw new ArgumentException($"unknown option --{name}; run gemini-to-api --help");
        }
    }
}
