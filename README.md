# ApiPin

A lightweight, local-first alternative to Postman for building and sending HTTP requests to the APIs I develop.

- **Key feature:** construct and execute HTTP requests — URL, params, headers, auth, and body
- **Secondary features:** request collections and environment management, stored as Git-friendly JSON files

## Status

**v0.1 — key features.** Request tabs, the request editor and the response viewer work end to end.
Collections and environments (Phases 3–4) and OAuth 2.0 (Phase 5) are next.
See [IMPLEMENTATION_PLAN.md](IMPLEMENTATION_PLAN.md) for the architecture and roadmap.

What works today:

- **Requests:** any method (including custom verbs), URL with two-way query-param sync, `:path` params,
  headers with autocomplete and bulk edit
- **Auth:** None, Basic, Bearer (custom prefix), API key (header or query), Windows (Negotiate/NTLM, current or explicit user)
- **Body:** raw JSON/XML/text/HTML/JavaScript with a Format button, form URL-encoded, multipart (text and file parts), binary file
- **Settings per request:** timeout, follow redirects, verify TLS (off for local dev certs), HTTP/1.1 / 2 / 3
- **Response:** status, time, size; body as Pretty (syntax-highlighted), Raw or Preview (HTML, images), with search,
  copy and save; headers, cookies, timings, and the request as actually sent
- **Friendly errors** for DNS failures, refused connections, TLS problems, timeouts and cancellation
- **Tabs** are kept across restarts in `%APPDATA%\ApiPin\scratch-tabs.json`; dark/light theme follows Windows
- **Shortcuts:** `Ctrl+Enter` send · `Ctrl+T` new tab · `Ctrl+W` close tab · `Ctrl+Tab` / `Ctrl+Shift+Tab` switch tabs
  (in the browser version: `Alt+T` / `Alt+W`, since the browser keeps the Ctrl ones)
- **Two hosts, same UI:** a Windows desktop app, and a browser version served from `localhost`

> Open tabs, including any passwords and tokens you typed, are saved as plain text in app data.
> Encrypted secret storage (DPAPI) comes with environments in Phase 4.

## Tech stack

.NET 10 · Blazor Hybrid (WPF + WebView2) · plain CSS (light/dark) · xUnit v3

The UI lives in a Razor Class Library (`ApiPin.UI`), so Fluent UI Blazor and Monaco can be added later
without touching the engine or the host.

## Solution layout

| Project | What it is |
|---|---|
| `src/ApiPin.Core` | UI-free engine: request model, URL tools, request builder, auth, executor, body formatting |
| `src/ApiPin.Storage` | JSON persistence (open tabs today; workspaces later) |
| `src/ApiPin.UI` | Razor components, UI state, CSS and a small JS module |
| `src/ApiPin.Desktop` | WPF window hosting the UI in a `BlazorWebView` |
| `src/ApiPin.Web` | Blazor Server host: the same UI in any browser, served from `localhost` |
| `tests/*` | Unit tests, plus integration tests against an in-process Kestrel server |
| `tools/ApiPin.EchoServer` | Answers any request with a JSON description of it, for manual testing |

## Getting started

Requirements: Windows 10 1809+ or Windows 11, a .NET SDK that can build `net10.0` (10.0 or later),
and the WebView2 runtime (already installed on Windows 11).

Build and run the tests:

```bash
dotnet build ApiPin.slnx
dotnet test --solution ApiPin.slnx
```

Run the app:

```bash
dotnet run --project src/ApiPin.Desktop
```

Or run it in a browser, then open <http://localhost:5100>:

```bash
dotnet run --project src/ApiPin.Web
```

### Browser version

`ApiPin.Web` is Blazor Server: the C# runs in the server process, which also sends your requests, and the
browser only shows the UI. So, unlike a pure web app, it isn't limited by CORS or forbidden headers.
Differences from the desktop app:

- **File paths** for multipart and binary bodies are typed in and refer to the server's disk;
  **Save…** downloads through the browser
- **Windows auth** runs as the user running the server
- **One shared set of tabs** for every browser window, saved to `%APPDATA%\ApiPin\scratch-tabs-web.json`
  (separate from the desktop app's file, so both can run at once)
- **Shortcuts** use `Alt+T` / `Alt+W` for tabs

It listens on `localhost:5100` only. Change it with `--urls`, for example
`dotnet run --project src/ApiPin.Web -- --urls http://localhost:6000`.
Don't bind it to a network address without putting authentication in front: anyone who can reach it
can make this machine send HTTP requests on their behalf.

### Echo server

Run the echo server to try requests against (default port 5080):

```bash
dotnet run --project tools/ApiPin.EchoServer -- 5080
```

Then send anything to `http://localhost:5080/...` from ApiPin. The response shows the method, URL, query,
decoded auth, headers and body exactly as the server received them.
