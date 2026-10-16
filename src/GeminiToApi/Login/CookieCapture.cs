using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Text.RegularExpressions;
using GeminiToApi.Configuration;
using Microsoft.Playwright;

namespace GeminiToApi.Login;

public sealed class LoginOptions
{
    public BrowserKind? Browser      { get; set; }
    public string?      BrowserPath  { get; set; }
    public bool         AutoMessage  { get; set; } = true;
    public string       Message      { get; set; } = "What's 1+1?";
    public TimeSpan     Timeout      { get; set; } = TimeSpan.FromMinutes(10);
    public bool         KeepOpen     { get; set; }
    public bool         Headless     { get; set; }
    public List<string> BrowserArgs  { get; set; } = new();
    public string       DataDirectory { get; set; } = AppConfig.DefaultDataDirectory();
}

/// <summary>What the login flow captured from the browser.</summary>
public sealed record CapturedSession(string CookieHeader, string AuthUser, string Source);

/// <summary>
/// Captures the Gemini session cookie from a real browser: launches it with a dedicated profile, waits
/// for the user to sign in to gemini.google.com, sends a message when needed, and reads the Cookie
/// header of the page's own <c>batchexecute</c> / <c>StreamGenerate</c> requests.
/// </summary>
public sealed partial class CookieCapture(LoginOptions options, TextWriter output)
{
    private const string GEMINI_URL = "https://gemini.google.com/app";

    /// <summary>How long to wait for a request after sign-in before sending a message to trigger one.</summary>
    private static readonly TimeSpan MessageGracePeriod = TimeSpan.FromSeconds(8);

    [GeneratedRegex(@"^/u/(\d+)/")]
    private static partial Regex AccountSlotRegex();

    private static readonly string[] PromptSelectors =
    [
        "rich-textarea div.ql-editor[contenteditable='true']",
        "div.ql-editor[contenteditable='true']",
        "div[contenteditable='true'][role='textbox']",
        "textarea",
    ];

    private readonly TaskCompletionSource<CapturedSession> _captured = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public async Task<CapturedSession> RunAsync(CancellationToken cancellationToken)
    {
        var browser = ResolveBrowser();

        output.WriteLine($"Using {browser.Kind}: {browser.ExecutablePath}");

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(options.Timeout);

        using var playwright = await Playwright.CreateAsync();

        return browser.IsChromium
            ? await RunChromiumAsync(playwright, browser, timeout.Token)
            : await RunFirefoxAsync(playwright, timeout.Token);
    }

    private BrowserInstallation ResolveBrowser()
    {
        if (!string.IsNullOrWhiteSpace(options.BrowserPath))
        {
            if (!File.Exists(options.BrowserPath)) throw new LoginException($"browser not found: {options.BrowserPath}");

            return new BrowserInstallation(options.Browser ?? BrowserLocator.KindFromPath(options.BrowserPath), options.BrowserPath);
        }

        if (!OperatingSystem.IsWindows())
            throw new LoginException("browser auto-detection is only supported on Windows for now; pass --browser-path <path to chrome/msedge>");

        var installed = BrowserLocator.Detect();

        if (options.Browser is { } wanted)
        {
            return installed.FirstOrDefault(b => b.Kind == wanted)
                ?? throw new LoginException($"{wanted} is not installed; installed browsers: {(installed.Count == 0 ? "none" : string.Join(", ", installed.Select(b => b.Kind)))}");
        }

        return installed.FirstOrDefault() ?? throw new LoginException("no supported browser found (Chrome, Edge or Firefox); install one or pass --browser-path");
    }

    private string ProfileDirectory(BrowserKind kind)
    {
        var path = Path.Combine(options.DataDirectory, "browser-profile", kind.ToString().ToLowerInvariant());
        Directory.CreateDirectory(path);
        return path;
    }

    // ---------------------------------------------------------------- Chrome / Edge

    /// <summary>
    /// Starts the real browser binary with remote debugging and attaches over CDP. A browser started by
    /// Playwright itself carries automation flags that Google's sign-in page refuses.
    /// </summary>
    private async Task<CapturedSession> RunChromiumAsync(IPlaywright playwright, BrowserInstallation browser, CancellationToken ct)
    {
        var port = FreePort();
        var info = new ProcessStartInfo(browser.ExecutablePath)
        {
            UseShellExecute        = false,
            RedirectStandardOutput = true, // keep the browser's own logging out of the console
            RedirectStandardError  = true,
        };

        info.ArgumentList.Add($"--remote-debugging-port={port}");
        info.ArgumentList.Add($"--user-data-dir={ProfileDirectory(browser.Kind)}");
        info.ArgumentList.Add("--no-first-run");
        info.ArgumentList.Add("--no-default-browser-check");

        if (options.Headless) info.ArgumentList.Add("--headless=new");

        // Chromium refuses to start as root with its sandbox on (containers, CI).
        if (!OperatingSystem.IsWindows() && Environment.UserName == "root") info.ArgumentList.Add("--no-sandbox");

        foreach (var arg in options.BrowserArgs) info.ArgumentList.Add(arg);

        info.ArgumentList.Add(GEMINI_URL);

        using var process = Process.Start(info) ?? throw new LoginException("could not start " + browser.ExecutablePath);

        var browserLog = new System.Collections.Concurrent.ConcurrentQueue<string>();

        void Collect(object _, DataReceivedEventArgs e)
        {
            if (e.Data is null) return;

            browserLog.Enqueue(e.Data);

            while (browserLog.Count > 20) browserLog.TryDequeue(out var _);
        }

        process.OutputDataReceived += Collect;
        process.ErrorDataReceived  += Collect;
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();

        try
        {
            var endpoint = $"http://127.0.0.1:{port}";

            try
            {
                await WaitForDevToolsAsync(endpoint, process, ct);
            }
            catch (LoginException e)
            {
                throw new LoginException(e.Message + (browserLog.IsEmpty ? "" : Environment.NewLine + "Browser output:" + Environment.NewLine + string.Join(Environment.NewLine, browserLog)));
            }

            var cdp     = await playwright.Chromium.ConnectOverCDPAsync(endpoint);
            var context = cdp.Contexts.FirstOrDefault() ?? throw new LoginException("the browser exposed no context over CDP");

            return await CaptureAsync(context, ct);
        }
        finally
        {
            if (!options.KeepOpen && !process.HasExited)
            {
                try
                {
                    process.Kill(entireProcessTree: true);
                }
                catch (InvalidOperationException)
                {
                    // Already exited.
                }
            }
        }
    }

    private static int FreePort()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        return ((IPEndPoint)listener.LocalEndpoint).Port;
    }

    private static async Task WaitForDevToolsAsync(string endpoint, Process process, CancellationToken ct)
    {
        using var http     = new HttpClient { Timeout = TimeSpan.FromSeconds(2) };
        var       deadline = DateTime.UtcNow + TimeSpan.FromSeconds(30);

        while (DateTime.UtcNow < deadline)
        {
            if (process.HasExited)
                throw new LoginException("the browser exited before opening its debugging port; close any window still using the login profile and retry");

            try
            {
                using var response = await http.GetAsync(endpoint + "/json/version", ct);

                if (response.IsSuccessStatusCode) return;
            }
            catch (Exception e) when (e is HttpRequestException or TaskCanceledException && !ct.IsCancellationRequested)
            {
                // Not listening yet.
            }

            await Task.Delay(250, ct);
        }

        throw new LoginException("timed out waiting for the browser's debugging port");
    }

    // ---------------------------------------------------------------- Firefox

    /// <summary>
    /// Firefox has no CDP, and Playwright can only drive its own patched build, so it is installed on
    /// demand and launched with a persistent profile.
    /// </summary>
    private async Task<CapturedSession> RunFirefoxAsync(IPlaywright playwright, CancellationToken ct)
    {
        output.WriteLine("Firefox is driven through Playwright's own Firefox build; installing it if needed...");

        var exitCode = await Task.Run(() => Microsoft.Playwright.Program.Main(["install", "firefox"]), ct);

        if (exitCode != 0) throw new LoginException($"installing Playwright Firefox failed (exit code {exitCode})");

        await using var context = await playwright.Firefox.LaunchPersistentContextAsync(ProfileDirectory(BrowserKind.Firefox), new BrowserTypeLaunchPersistentContextOptions
        {
            Headless = options.Headless,
            Args     = options.BrowserArgs,
        });

        var page = context.Pages.FirstOrDefault() ?? await context.NewPageAsync();
        await page.GotoAsync(GEMINI_URL);

        return await CaptureAsync(context, ct);
    }

    // ---------------------------------------------------------------- capture

    private async Task<CapturedSession> CaptureAsync(IBrowserContext context, CancellationToken ct)
    {
        context.Request += (_, request) => _ = InspectRequestAsync(request);

        var page = context.Pages.FirstOrDefault(p => p.Url.Contains("gemini.google.com", StringComparison.Ordinal)) ?? context.Pages.FirstOrDefault() ?? await context.NewPageAsync();

        if (!page.Url.Contains("gemini.google.com", StringComparison.Ordinal)) await page.GotoAsync(GEMINI_URL);

        await page.BringToFrontAsync();

        output.WriteLine("Sign in to Google in the browser window if asked. Waiting for the Gemini session...");

        DateTime? signedInAt  = null;
        var       messageSent = false;

        while (!_captured.Task.IsCompleted)
        {
            ct.ThrowIfCancellationRequested();

            var current = context.Pages.FirstOrDefault(p => p.Url.Contains("gemini.google.com", StringComparison.Ordinal));

            if (current is not null && await HasSessionCookiesAsync(context))
            {
                if (signedInAt is null)
                {
                    signedInAt = DateTime.UtcNow;
                    output.WriteLine("Signed in. Watching Gemini's requests for the session cookie...");
                }

                if (options.AutoMessage && !messageSent && DateTime.UtcNow - signedInAt > MessageGracePeriod && await TrySendMessageAsync(current))
                {
                    messageSent = true;
                    output.WriteLine($"Sent \"{options.Message}\" to trigger a Gemini request.");
                }

                // Fallback: no request carried the cookies (or messages are disabled) - read the cookie jar.
                if (DateTime.UtcNow - signedInAt > MessageGracePeriod * 4 && await FromCookieJarAsync(context, current) is { } fromJar)
                {
                    _captured.TrySetResult(fromJar);
                    break;
                }
            }

            await Task.WhenAny(_captured.Task, Task.Delay(1000, ct));
        }

        return await _captured.Task;
    }

    private async Task InspectRequestAsync(IRequest request)
    {
        try
        {
            if (!Uri.TryCreate(request.Url, UriKind.Absolute, out var url) || url.Host != "gemini.google.com") return;

            if (!url.AbsolutePath.Contains("batchexecute", StringComparison.Ordinal) && !url.AbsolutePath.Contains("StreamGenerate", StringComparison.Ordinal)) return;

            var headers = await request.AllHeadersAsync();

            if (!headers.TryGetValue("cookie", out var cookie) || !IsCompleteSession(cookie)) return;

            var slot = AccountSlotRegex().Match(url.AbsolutePath);

            _captured.TrySetResult(new CapturedSession(cookie, slot.Success ? slot.Groups[1].Value : "", url.AbsolutePath.Contains("batchexecute") ? "batchexecute request" : "StreamGenerate request"));
        }
        catch (PlaywrightException)
        {
            // The request went away (page closed or navigated) before its headers could be read.
        }
    }

    internal static bool IsCompleteSession(string cookieHeader)
        => CookieHeader.Value(cookieHeader, "__Secure-1PSID").Length > 0 && CookieHeader.Value(cookieHeader, "__Secure-1PSIDTS").Length > 0;

    private static async Task<bool> HasSessionCookiesAsync(IBrowserContext context)
    {
        var cookies = await context.CookiesAsync(["https://gemini.google.com"]);
        return cookies.Any(c => c.Name == "__Secure-1PSID") && cookies.Any(c => c.Name == "__Secure-1PSIDTS");
    }

    private static async Task<CapturedSession?> FromCookieJarAsync(IBrowserContext context, IPage page)
    {
        var cookies = await context.CookiesAsync(["https://gemini.google.com"]);
        var header  = string.Join("; ", cookies.Select(c => c.Name + "=" + c.Value));

        if (!IsCompleteSession(header)) return null;

        var slot = Uri.TryCreate(page.Url, UriKind.Absolute, out var url) ? AccountSlotRegex().Match(url.AbsolutePath) : Match.Empty;

        return new CapturedSession(header, slot.Success ? slot.Groups[1].Value : "", "browser cookie jar");
    }

    private async Task<bool> TrySendMessageAsync(IPage page)
    {
        foreach (var selector in PromptSelectors)
        {
            var input = page.Locator(selector).First;

            try
            {
                if (!await input.IsVisibleAsync()) continue;

                await input.ClickAsync(new LocatorClickOptions { Timeout = 5000 });
                await page.Keyboard.TypeAsync(options.Message, new KeyboardTypeOptions { Delay = 20 });
                await page.Keyboard.PressAsync("Enter");

                return true;
            }
            catch (Exception e) when (e is PlaywrightException or TimeoutException)
            {
                // Try the next selector; the UI may still be loading.
            }
        }

        return false;
    }

    /// <summary>Writes the captured session where the server reads it at start-up.</summary>
    public static string Save(CapturedSession session, string dataDirectory)
    {
        var path = AppConfig.CredentialsPath(dataDirectory);

        DotEnv.Write(path, new Dictionary<string, string>
        {
            ["GEMINI_COOKIES"]   = session.CookieHeader,
            ["GEMINI_AUTH_USER"] = session.AuthUser,
        });

        return path;
    }
}

public sealed class LoginException(string message) : Exception(message);
