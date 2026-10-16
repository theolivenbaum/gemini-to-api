using System.Threading.RateLimiting;
using GeminiToApi.Api.Claude;
using GeminiToApi.Api.Gemini;
using GeminiToApi.Api.OpenAI;
using GeminiToApi.Configuration;
using GeminiToApi.Gemini;

namespace GeminiToApi.Hosting;

/// <summary>Builds the Kestrel host: services, middleware and routes.</summary>
public static class Server
{
    private const string SCALAR_HTML = """
        <!doctype html>
        <html>
          <head>
            <title>Gemini To API</title>
            <meta charset="utf-8" />
            <meta name="viewport" content="width=device-width, initial-scale=1" />
          </head>
          <body>
            <script id="api-reference" data-url="/openapi.json"></script>
            <script src="https://cdn.jsdelivr.net/npm/@scalar/api-reference"></script>
          </body>
        </html>
        """;

    /// <param name="configure">Hook for tests (test server, replacement services).</param>
    public static WebApplication Build(AppConfig config, string[]? args = null, Action<WebApplicationBuilder>? configure = null, bool listen = true)
    {
        var builder = WebApplication.CreateSlimBuilder(new WebApplicationOptions { Args = args ?? [], ContentRootPath = AppContext.BaseDirectory });

        builder.Logging.ClearProviders();
        builder.Logging.AddSimpleConsole(o =>
        {
            o.SingleLine      = true;
            o.TimestampFormat = "HH:mm:ss ";
        });
        builder.Logging.SetMinimumLevel(ParseLogLevel(config.LogLevel));
        builder.Logging.AddFilter("Microsoft.AspNetCore", LogLevel.Warning);
        builder.Logging.AddFilter("System.Net.Http.HttpClient", LogLevel.Warning);

        if (listen)
        {
            builder.WebHost.ConfigureKestrel(kestrel =>
            {
                if (config.Host is "localhost" or "") kestrel.ListenLocalhost(config.Port);
                else if (config.Host is "0.0.0.0" or "*" or "+") kestrel.ListenAnyIP(config.Port);
                else kestrel.Listen(System.Net.IPAddress.Parse(config.Host), config.Port);
            });
        }

        builder.Services.AddSingleton(config);
        builder.Services.AddSingleton(config.Gemini);
        builder.Services.AddSingleton<GeminiClient>(sp => new GeminiClient(config.Gemini, sp.GetRequiredService<ILogger<GeminiClient>>()));
        builder.Services.AddSingleton<DeepResearch>();
        builder.Services.AddSingleton(new Api.Common.ConversationStore());
        builder.Services.AddSingleton<Api.Common.GeminiChat>();
        builder.Services.AddHostedService<GeminiSessionService>();

        builder.Services.AddCors(o => o.AddDefaultPolicy(p => p
            .AllowAnyOrigin()
            .WithHeaders("Origin", "Content-Type", "Accept", "Authorization", "X-Requested-With", "x-api-key", "anthropic-version", "x-goog-api-key")
            .WithMethods("GET", "POST", "PUT", "DELETE", "OPTIONS", "PATCH")));

        if (config.RateLimit.Enabled)
        {
            builder.Services.AddRateLimiter(o =>
            {
                o.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
                o.GlobalLimiter = PartitionedRateLimiter.Create<HttpContext, string>(context => RateLimitPartition.GetFixedWindowLimiter(
                    context.Connection.RemoteIpAddress?.ToString() ?? "unknown",
                    _ => new FixedWindowRateLimiterOptions
                    {
                        PermitLimit = Math.Max(1, config.RateLimit.MaxRequests),
                        Window      = TimeSpan.FromMilliseconds(Math.Max(1, config.RateLimit.WindowMs)),
                        QueueLimit  = 0,
                    }));
            });
        }

        configure?.Invoke(builder);

        var app = builder.Build();

        app.UseCors();

        if (config.RateLimit.Enabled) app.UseRateLimiter();

        if (config.ApiKey.Length > 0) app.Use(RequireApiKey(config.ApiKey));

        app.MapGet("/health", (GeminiClient client) => Results.Json(new { status = "ok", service = "gemini-to-api", gemini_healthy = client.IsHealthy }));
        app.MapGet("/docs", () => Results.Content(SCALAR_HTML, "text/html; charset=utf-8"));
        app.MapGet("/openapi.json", () => Results.Stream(typeof(Server).Assembly.GetManifestResourceStream("GeminiToApi.openapi.json")!, "application/json; charset=utf-8"));

        OpenAIApi.Map(app);
        ClaudeApi.Map(app);
        GeminiApi.Map(app);

        app.MapFallback((HttpContext context) => Results.Json(new
        {
            status  = 404,
            error   = "Not Found",
            message = $"Cannot {context.Request.Method} {context.Request.Path}",
        }, statusCode: 404));

        return app;
    }

    /// <summary>
    /// Optional shared secret (new in the .NET port): accepted as a bearer token, <c>x-api-key</c> (Anthropic),
    /// <c>x-goog-api-key</c> or <c>?key=</c> (Google). Health and docs stay open.
    /// </summary>
    private static Func<HttpContext, RequestDelegate, Task> RequireApiKey(string apiKey) => async (context, next) =>
    {
        var path = context.Request.Path;

        if (HttpMethods.IsOptions(context.Request.Method) || path == "/health" || path == "/docs" || path == "/openapi.json")
        {
            await next(context);
            return;
        }

        var headers   = context.Request.Headers;
        var bearer    = headers.Authorization.ToString();
        var presented = bearer.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase) ? bearer[7..].Trim() : "";

        if (presented.Length == 0) presented = headers["x-api-key"].ToString();
        if (presented.Length == 0) presented = headers["x-goog-api-key"].ToString();
        if (presented.Length == 0) presented = context.Request.Query["key"].ToString();

        if (!System.Security.Cryptography.CryptographicOperations.FixedTimeEquals(System.Text.Encoding.UTF8.GetBytes(presented), System.Text.Encoding.UTF8.GetBytes(apiKey)))
        {
            context.Response.StatusCode = StatusCodes.Status401Unauthorized;
            await context.Response.WriteAsJsonAsync(new { error = new { message = "Invalid or missing API key", type = "authentication_error" } });
            return;
        }

        await next(context);
    };

    private static LogLevel ParseLogLevel(string level) => level.Trim().ToLowerInvariant() switch
    {
        "debug" or "trace"            => LogLevel.Debug,
        "warn" or "warning"           => LogLevel.Warning,
        "error"                       => LogLevel.Error,
        "fatal" or "panic" or "dpanic" => LogLevel.Critical,
        _                             => LogLevel.Information,
    };
}

/// <summary>Initializes the Gemini session in the background and keeps it refreshed.</summary>
internal sealed class GeminiSessionService(GeminiClient client, ILogger<GeminiSessionService> log) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        log.LogInformation("Starting Gemini provider initialization in background");

        try
        {
            await client.RunAsync(stoppingToken);
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // Shutting down.
        }
    }
}
