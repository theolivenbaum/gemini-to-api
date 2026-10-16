# gemini-to-api

A local **OpenAI-, Claude- and Gemini-compatible API server** backed by the
Gemini web app (gemini.google.com) and your own signed-in browser session — no
API key needed. Distributed as a **.NET global tool**.

This is a C# / .NET 10 reimplementation of
[gemini-web-to-api](https://github.com/ntthanh2603/gemini-web-to-api) (Go). The
original sources are kept in [`.reference/`](.reference/) for porting
reference.

> [!WARNING]
> Not affiliated with Google. It uses your browser cookies against Gemini's
> reverse-engineered web protocol and may not comply with Google's Terms of
> Service. Use at your own risk, for research and educational purposes. Your
> cookies give full access to your Google account — never share them.

## Install

```bash
dotnet tool install -g GeminiToApi      # requires the .NET 10 runtime
```

## Get your Gemini cookie

### Option A — `gemini-to-api login` (Windows)

```bash
gemini-to-api login
```

1. Detects an installed browser — **Chrome**, then **Edge**, then **Firefox**
   (`--browser edge` to choose) — and opens it on gemini.google.com with a
   dedicated profile under `~/.gemini-to-api/browser-profile/`.
2. Sign in to Google in that window (only needed the first time).
3. Once you are signed in, the tool watches Gemini's own `batchexecute` /
   `StreamGenerate` requests. If none happens within a few seconds it types
   **"What's 1+1?"** into Gemini and sends it for you (`--no-auto-message` to
   disable, `--message` to change the text).
4. It reads the request's full `Cookie` header and the `/u/<n>/` account slot,
   saves them to `~/.gemini-to-api/credentials.env`, checks them against
   Gemini, and closes the browser.

Chrome and Edge are started as the real browser binary and attached with
Playwright over the DevTools protocol, so Google's sign-in page treats them as
a normal browser. Firefox goes through Playwright's own Firefox build, which is
downloaded on first use. On macOS/Linux, pass `--browser-path` to a Chrome or
Edge executable.

### Option B — copy it by hand

1. Open [gemini.google.com](https://gemini.google.com) and sign in.
2. `F12` → **Network**, reload, select a `batchexecute?rpcids=otAQ7b` request.
3. Copy its complete `Cookie` request header into `GEMINI_COOKIES`. If the
   request URL contains `/u/2/`, also set `GEMINI_AUTH_USER=2`.

## Run

```bash
gemini-to-api                                   # uses credentials.env from `login`
GEMINI_COOKIES='<cookie header>' gemini-to-api  # or pass it explicitly
gemini-to-api --host 0.0.0.0 --port 4981        # listen on all interfaces
```

The server listens on `http://localhost:4981`. Interactive API docs are at
`/docs`, health at `/health`.

```bash
curl http://localhost:4981/openai/v1/chat/completions \
  -H "Content-Type: application/json" \
  -d '{"model": "gemini-advanced", "messages": [{"role": "user", "content": "Hello!"}]}'
```

## APIs

| Surface | Base URL | Routes |
| --- | --- | --- |
| OpenAI | `/openai/v1` | `models`, `chat/completions` (streaming, tools, `reasoning_effort`, image input), `images/generations`, `audio/speech` (music), `videos` |
| Claude | `/claude/v1` | `models`, `messages` (streaming, tools, thinking, images), `messages/count_tokens` |
| Gemini | `/gemini/v1beta` | `models`, `models/{m}:generateContent`, `:streamGenerateContent` (`alt=sse`), `:predictLongRunning` + `operations` + `files` (video), `deepresearch`, `interactions` |

SDK base URLs: OpenAI `http://localhost:4981/openai/v1`, Anthropic
`http://localhost:4981/claude`, google-genai `http://localhost:4981/gemini`
with `api_version="v1beta"`. Any API key works unless you set `API_KEY`.

**Models** are discovered from your account at start-up (`GET
/openai/v1/models`); `gemini-advanced` is an alias for the Pro model. Append
`-canvas` to a model for Gemini Canvas files, `-music` for music generation.

Gemini Web returns whole answers, so streaming responses are replayed in small
chunks. Tool calling is emulated by prompting for JSON.

## Configuration

Environment variables (or a `.env` file, or the matching `--flag`; flags win):

| Variable | Default | Description |
| --- | --- | --- |
| `GEMINI_COOKIES` | — | Full Cookie header from a gemini.google.com request (required) |
| `GEMINI_AUTH_USER` | — | Google account slot from `/u/<n>/` URLs |
| `GEMINI_REFRESH_INTERVAL` | `1440` | Cookie rotation interval in minutes (minimum 10) |
| `GEMINI_MAX_RETRIES` | `3` | Retries for failed Gemini calls |
| `GEMINI_TEMPORARY` | `false` | Don't keep conversations in Gemini's history |
| `PORT` / `HOST` | `4981` / `localhost` | Listen address (`HOST=0.0.0.0` for Docker/LAN) |
| `API_KEY` | — | If set, clients must send it (`Authorization: Bearer`, `x-api-key`, `x-goog-api-key` or `?key=`) |
| `RATE_LIMIT_ENABLED` | `false` | Per-IP fixed-window rate limit |
| `RATE_LIMIT_WINDOW_MS` / `RATE_LIMIT_MAX_REQUESTS` | `60000` / `10` | Rate limit window and size |
| `LOG_LEVEL` | `info` | `debug`, `info`, `warn`, `error` |
| `GEMINI_TO_API_HOME` | `~/.gemini-to-api` | Data folder: `credentials.env`, cookie cache, browser profiles |

Rotating cookies too often looks automated to Google; keep the default refresh
interval unless the session expires between rotations.

## Develop

```bash
dotnet build GeminiToApi.slnx
dotnet test  GeminiToApi.slnx
dotnet run --project src/GeminiToApi -- --help
dotnet pack src/GeminiToApi -c Release -o artifacts
```

See [CLAUDE.md](CLAUDE.md) for the code layout and porting rules, and
[TODO.md](TODO.md) for the port's status.

## License

MIT — see [LICENSE](LICENSE). Original Go implementation © Nguyễn Tuấn Thành.
