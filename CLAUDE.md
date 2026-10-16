# CLAUDE.md — gemini-to-api (.NET port)

## Goal

This repository is a **from-scratch C# reimplementation** of
[gemini-web-to-api](https://github.com/ntthanh2603/gemini-web-to-api), a Go
server that turns the Gemini **web app** (gemini.google.com, authenticated with
the user's browser cookies) into OpenAI-, Claude- and Gemini-compatible REST
APIs.

The port targets **.NET 10**, hosts the API on **Kestrel** (ASP.NET Core
minimal APIs) and ships as a **NuGet .NET global tool**:

```bash
dotnet tool install -g GeminiToApi
gemini-to-api login      # Windows: capture the Gemini cookie from a real browser
gemini-to-api            # start the server on http://localhost:4981
```

## Repository layout

```
.reference/                 # the original Go implementation - READ ONLY, porting reference
.github/workflows/*.disabled# the Go CI/CD pipelines, disabled on purpose
src/GeminiToApi/            # the tool (Microsoft.NET.Sdk.Web, PackAsTool)
  Program.cs                # entry point: CLI parsing, `serve` / `login` commands
  Configuration/            # AppConfig (env vars, .env files, CLI flags), DotEnv
  Gemini/                   # the Gemini Web protocol client (the hard part)
    GeminiClient*.cs        # session init, cookie rotation, StreamGenerate, uploads
    GeminiModels.cs         # model discovery (otAQ7b RPC) and name resolution
    GeminiProtocol.cs       # payload/header builders (JSPB arrays)
    GeminiResponseParser.cs # StreamGenerate response parsing, media extraction
    Canvas.cs Music.cs Video*.cs DeepResearch.cs
  Api/                      # the three public API surfaces + shared helpers
    OpenAI/ Claude/ Gemini/ Common/
  Hosting/                  # Kestrel host wiring: CORS, rate limit, docs, health, 404
  Login/                    # Playwright-based cookie capture (`login` command)
  Resources/openapi.json    # served at /openapi.json, rendered by Scalar at /docs
tests/GeminiToApi.Tests/    # xUnit tests, ported from the Go *_test.go files
```

## Porting rules

- **`.reference/` is the source of truth for behaviour.** When porting a
  feature, read the matching Go file and keep the wire format byte-for-byte
  where Google is on the other end (payload array indexes, header values, RPC
  ids, regexes). Those numbers were reverse-engineered from browser captures;
  do not "clean them up".
- **Never edit `.reference/`.** It is a frozen snapshot.
- Public API routes are kept identical to the Go server (`/openai/v1/...`,
  `/claude/v1/...`, `/gemini/v1beta/...`, `/health`, `/docs`,
  `/openapi.json`) so existing clients keep working.
- Where the Go server deviated from the official vendor wire format by
  accident (e.g. Claude stream events omitting `index: 0`, Gemini response
  parts in snake_case), the port follows the **official** format. Note any
  such deviation in `TODO.md` under "Behaviour changes".
- Configuration keeps the Go environment variable names (`GEMINI_COOKIES`,
  `GEMINI_AUTH_USER`, `GEMINI_REFRESH_INTERVAL`, `GEMINI_MAX_RETRIES`,
  `GEMINI_TEMPORARY`, `PORT`, `LOG_LEVEL`, `RATE_LIMIT_*`).

## Code conventions

- Modern C# (file-scoped namespaces, records, pattern matching, nullable on).
- JSON: `System.Text.Json` only. Google's JSPB payloads are handled as
  `JsonNode`/`JsonArray` through the helpers in `Gemini/Jspb.cs` — never with
  `dynamic`.
- Vendor DTOs use explicit `[JsonPropertyName]` so the wire names are visible
  in the code. LLMTornado is **not** used: the surfaces only need a few DTOs,
  and owning them keeps the wire format under our control (same approach as
  the OpenAI/Anthropic-compatible controllers in curiosity-ai/mosaik).
- HTTP to Google goes through the one `HttpClient` owned by `GeminiClient`,
  with `UseCookies = false`: cookies are always sent as an explicit `Cookie`
  header, exactly like the Go code does. Media downloads follow redirects by
  hand so every hop is checked against the trusted Google host list.
- Async all the way; no `.Result` / `.Wait()`.
- Errors that are the caller's fault are typed exceptions
  (`ModelSelectionException`, `VideoValidationException`, …) that the API
  layer maps to 4xx; everything else becomes 5xx.

## Build, test, pack

```bash
dotnet build GeminiToApi.slnx
dotnet test  GeminiToApi.slnx
dotnet pack  src/GeminiToApi -c Release -o artifacts
dotnet tool install -g GeminiToApi --add-source ./artifacts   # local smoke test
```

Run locally against a real account:

```bash
GEMINI_COOKIES='<full Cookie header>' dotnet run --project src/GeminiToApi
```

Tests never talk to Google: `GeminiClient` takes an injectable
`HttpMessageHandler`, and the tests route requests to canned responses.

## The `login` command (Playwright)

`gemini-to-api login` captures the cookie for the user:

1. Detects an installed browser (Windows only for auto-detection): Chrome,
   then Edge, then Firefox. `--browser` / `--browser-path` override it.
2. Chrome/Edge are started as **real browser processes** with a dedicated
   profile (`<data dir>/browser-profile/<browser>`) and
   `--remote-debugging-port`, then attached with
   `Playwright.Chromium.ConnectOverCDPAsync`. Launching the real binary
   (instead of a Playwright-launched, automation-flagged one) is what lets
   Google sign-in work.
3. Firefox cannot be driven over CDP; it uses Playwright's own Firefox build
   (installed on demand with `playwright install firefox`).
4. The flow waits for the user to sign in (session cookies appear in the
   context). If no Gemini request has carried them within a few seconds, it
   types "What's 1+1?" into the prompt box and sends it (unless
   `--no-auto-message`).
5. It watches the page's requests for `batchexecute` / `StreamGenerate`,
   reads the full `Cookie` header and the `/u/<n>/` account slot, verifies it
   contains `__Secure-1PSID` and `__Secure-1PSIDTS`, and saves both to
   `<data dir>/credentials.env`, which the server loads at start-up.

Release packs set `PlaywrightPlatform=win`, so the tool ships only the Windows
Node driver (~40 MB package). Debug builds keep the host's driver, which is how
the flow can be exercised on Linux with `--browser-path`, `--headless` and
`--browser-args "\"--host-resolver-rules=MAP gemini.google.com 127.0.0.1:8443\" --ignore-certificate-errors"`
against a local fake Gemini page.

## Keep in sync

When a change touches behaviour, update in the same commit:

- `TODO.md` (tick the item, or add a follow-up),
- `README.md` if a user-visible option, route or command changed,
- `Resources/openapi.json` if a route or request/response shape changed,
- the tests.
