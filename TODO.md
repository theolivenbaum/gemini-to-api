# TODO — C# port of gemini-web-to-api

Status legend: `[x]` done, `[ ]` open, `[~]` partial.

## 0. Repository setup

- [x] Move the Go implementation to `.reference/`
- [x] Disable the Go CI/CD workflows (`.github/workflows/*.yml.disabled`)
- [x] `CLAUDE.md` / `TODO.md`
- [x] Solution (`GeminiToApi.slnx`), tool project, test project
- [ ] New CI workflow: build + test on PRs, `dotnet pack` + `dotnet nuget push`
      on release tags (needs a `NUGET_API_KEY` secret)
- [ ] Optional: Dockerfile for the .NET build

## 1. Host (`internal/server`, `cmd/server`, `internal/commons/configs`)

- [x] Kestrel host on `PORT` (default 4981), `HOST` (default `localhost`)
- [x] Config from env vars, `.env` in the working directory, `--env-file`,
      `<data dir>/credentials.env` (written by `login`) and CLI flags
- [x] Validation identical to Go (`GEMINI_COOKIES` must contain
      `__Secure-1PSID` + `__Secure-1PSIDTS`, numeric `GEMINI_AUTH_USER`,
      refresh interval default 1440 / minimum 10)
- [x] CORS (any origin, same header list as Go)
- [x] Optional per-IP fixed-window rate limit (`RATE_LIMIT_*`)
- [x] `/health`, `/docs` (Scalar), `/openapi.json`, JSON 404 fallback
- [x] Logging with `LOG_LEVEL`
- [x] Optional `API_KEY` (new): when set, every API route requires it as
      `Authorization: Bearer`, `x-api-key`, `x-goog-api-key` or `?key=`

## 2. Gemini Web client (`internal/modules/providers`)

- [x] Session bootstrap: google.com → gemini.google.com → `/app` scrape of
      `SNlM0e`, `qKIAYe`, `cfb2h`, `FdrFJe`, `TuX5cc`
- [x] Multi-login account slot (`/u/<n>/`)
- [x] `__Secure-1PSIDTS` rotation (`accounts.google.com/RotateCookies`),
      on-disk cache keyed by SHA-256 of the PSID, background refresh loop and
      health flag
- [x] Model discovery (`otAQ7b` batchexecute RPC), capacity flags, alias
      generation, strict resolution (`gemini-advanced` → Pro), ambiguity errors
- [x] `StreamGenerate` payload + `x-goog-ext-*` model headers, thinking levels
- [x] Retries with exponential backoff (never for video/music)
- [x] Response parsing: text, numbered-field replies, BardErrorInfo, thinking
      markers, images (generic + generated media slot), canvases, audio, video
- [x] Served-model detection (`model` vs `requested_model`)
- [x] Resumable file upload (`content-push.googleapis.com`) with retries
- [x] Generated image download (`b64_json`) with trusted-host redirect policy
- [x] Canvas mode (`-canvas` model suffix)
- [x] Music generation (`-music` suffix, audio/speech), conversation polling
- [x] Video generation jobs (single active job, retention, MP4 duration)
- [x] Deep research (plan → sub-questions → synthesis), sync + streaming
- [ ] `ChatSession` / `StartChat` (multi-turn with conversation metadata) — not
      used by any API route in the Go server; port only if a route needs it

## 3. API surfaces

### OpenAI (`/openai/v1`)
- [x] `GET /models`, `GET /models/{model}`
- [x] `POST /chat/completions` (+ SSE streaming, tool-call bridge,
      `reasoning_effort`, image inputs as `data:` URLs, canvases)
- [x] `POST /images/generations`
- [x] `POST /audio/speech` (music)
- [x] `POST/GET /videos`, `GET/DELETE /videos/{id}`, `GET /videos/{id}/content`

### Claude (`/claude/v1`)
- [x] `GET /models`, `GET /models/{id}`
- [x] `POST /messages` (+ SSE streaming, thinking blocks, tool-use bridge,
      base64 images)
- [x] `POST /messages/count_tokens`

### Gemini (`/gemini/v1beta`)
- [x] `GET /models`, `GET /models/{model}`
- [x] `POST /models/{model}:generateContent`
- [x] `POST /models/{model}:streamGenerateContent` (NDJSON and `alt=sse`)
- [x] `POST /models/{model}:predictLongRunning`,
      `GET /models/{model}/operations/{id}`, `GET /files/{id}:download`
- [x] `POST /deepresearch`, `POST /deepresearch/stream`
- [x] `POST /interactions`, `GET /interactions/{id}`

## 4. Cookie capture — `login` command (new, not in Go)

- [x] Windows browser detection: Chrome → Edge → Firefox (well-known paths and
      the `App Paths` registry keys)
- [x] Chrome/Edge: launch the real browser with a dedicated profile and
      `--remote-debugging-port`, attach with Playwright over CDP
- [x] Firefox: Playwright-managed Firefox (installed on demand)
- [x] Detect sign-in, auto-send "What's 1+1?" (`--no-auto-message` to skip)
- [x] Capture `Cookie` header + account slot from `batchexecute` /
      `StreamGenerate` requests, fall back to the context cookie jar
- [x] Save to `<data dir>/credentials.env`; `serve` loads it automatically
- [x] Verify the captured cookie by initializing a session (`--no-verify` to skip)
- [x] `--browser-args` passthrough (proxies, host rules)
- [ ] macOS / Linux auto-detection (works today with `--browser-path` in a
      Debug build; Release packs ship only Playwright's Windows driver, so
      this also needs `PlaywrightPlatform` widened)
- [ ] `serve --login`: run the login flow automatically when no cookie is
      configured or the session expires

## 5. Tests (ported from `*_test.go`)

- [x] Model discovery / resolution
- [x] Response parsing (images, BardErrorInfo, numbered fields, canvases,
      thinking), URL normalisation, trusted hosts
- [x] Payload builders (generate inner, model headers, video/music/canvas)
- [x] Cookie header merging, config validation
- [x] Video parsing, MP4 duration, job store lifecycle
- [x] Tool-bridge parsing, message parsing, chunk splitting
- [x] End-to-end HTTP tests of the three surfaces against a fake Gemini
      (`WebApplicationFactory` + fake `HttpMessageHandler`)
- [x] Image upload through the resumable protocol (end-to-end test)
- [ ] Upload retry/backoff test
- [ ] Music / video polling tests (conversation read-back with a fake clock)
- [ ] Automated test of the `login` flow (verified manually against a local
      fake gemini.google.com with `--browser-path`, `--headless` and
      `--browser-args "--host-resolver-rules=…"`; needs a Chromium in CI)

## 6. Future improvements

- [ ] Real token streaming: read the chunked `StreamGenerate` response
      incrementally instead of splitting the finished answer into chunks
      (the Go server simulates streaming too)
- [ ] Download remote `http(s)` image URLs in chat requests (Go only accepts
      `data:` URLs)
- [ ] Real token counting instead of `chars / 4`

## Behaviour changes vs. the Go server

- Default bind address is `localhost` (Go listened on all interfaces). Set
  `HOST=0.0.0.0` (or `--host 0.0.0.0`) for Docker / LAN access.
- The cookie cache lives in `<data dir>/cookies/` instead of `./.cookies/`
  (`<data dir>` = `GEMINI_TO_API_HOME`, default `~/.gemini-to-api`).
- Claude stream events always carry `index` (Go dropped `index: 0`), the
  usage object uses `input_tokens` / `output_tokens`, and `message_delta`
  carries `usage`.
- Gemini response parts use the official camelCase field names
  (`inlineData`, `fileData`, `functionCall`); requests accept both
  `function_declarations` and `functionDeclarations`.
- Claude `/models` returns `created_at` as an RFC 3339 string plus
  `has_more` / `first_id` / `last_id`, as the Anthropic API does.
- OpenAI chunks carry `finish_reason: null` instead of omitting it, and
  `usage` is estimated as `chars / 4` instead of zeros.
- Remote upload redirects are refused instead of followed (same-host only in Go).
- Streaming requests are cancelled when the client disconnects.
