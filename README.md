# ApiPin

A lightweight, local-first alternative to Postman for building and sending HTTP requests to the APIs I develop.

- **Key feature:** construct and execute HTTP requests — URL, params, headers, auth, and body
- **Secondary features:** request collections and environment management, stored as Git-friendly JSON files

## Status

**v0.6 — daily use plus chaining and CI.** Requests, collections, environments with encrypted secrets, OAuth 2.0 and
the other auth types, history, imports (Postman, OpenAPI, cURL), code snippets, a command palette, and captures,
assertions and a collection runner in the app and on the command line. Packaging (Phase 8) is next.
See [IMPLEMENTATION_PLAN.md](IMPLEMENTATION_PLAN.md) for the architecture and roadmap.

What works today:

- **Requests:** any method (including custom verbs), URL with two-way query-param sync, `:path` params,
  headers with autocomplete and bulk edit
- **Auth:** None, Basic, Bearer (custom prefix), API key (header or query), Windows (Negotiate/NTLM, current or explicit user),
  **OAuth 2.0** (client credentials, authorization code with PKCE in your browser, password; tokens cached and refreshed,
  JWT claims shown) and **Azure CLI** tokens for Entra ID-protected APIs (`az login` once)
- **Body:** raw JSON/XML/text/HTML/JavaScript with a Format button, form URL-encoded, multipart (text and file parts), binary file
- **Settings per request:** timeout, follow redirects, verify TLS (off for local dev certs), HTTP/1.1 / 2 / 3,
  client certificate for mutual TLS (PFX file or Windows certificate store); **app-wide proxy** (system, none or custom
  with a bypass list) in ⚙ Settings
- **Response:** status, time, size; body as Pretty (syntax-highlighted), Raw or Preview (HTML, images), with search,
  copy and save; headers, cookies, timings, and the request as actually sent
- **Friendly errors** for DNS failures, refused connections, TLS problems, timeouts and cancellation
- **Collections:** save requests (`Ctrl+S`, `Ctrl+Shift+S` for Save as) into collections and folders; open, rename,
  duplicate and delete them from the sidebar; filter by name or URL; unsaved changes are marked and you're asked before closing
- **Folder and collection settings:** headers, auth and variables set on a collection or folder apply to the requests
  inside (requests inherit auth by default; a request's own header wins); `{{variables}}` work in the URL, params,
  headers, auth and body, including `{{$guid}}`, `{{$timestamp}}`, `{{$randomInt}}` and friends
- **Environments:** pick Local / Staging / … in the top bar; manage them and the workspace globals in one dialog.
  Variables are highlighted where you type them (green defined, red undefined), `{{` suggests names, the 👁 button
  shows every variable in effect and where it comes from, and the **Resolved** tab shows the request as it will be sent
- **Secrets:** variables marked secret are masked and their values are stored encrypted with Windows DPAPI in app data,
  never in the workspace files; after a clone, missing secrets are flagged so you can enter them once
- **Drag and drop** requests, folders and collections in the sidebar to reorder or move them
- **Git-friendly:** changes made outside ApiPin (a `git pull`) are picked up automatically; a tab with unsaved edits
  asks whether to keep them or take the version on disk
- **History** of every sent request (sidebar → History): search it, and click an entry to reopen it with its response
- **Import** Postman collections and environments, OpenAPI 3 / Swagger 2.0 (JSON or YAML) and `curl` commands —
  paste a `curl` straight into the URL bar; **Code** turns a request into cURL, PowerShell, C#, JavaScript or Python
- **GraphQL** bodies (query + variables); a **cookie jar** per environment (🍪 in the top bar)
- **Command palette** (`Ctrl+K`) for commands, saved requests and environments; `Ctrl+/` lists the shortcuts;
  drag tabs to reorder, right-click them to close others, `Ctrl+Shift+T` reopens a closed one
- **Tests:** in a request's **Tests** tab, *captures* set variables from the response (`$.access_token` → `{{token}}`)
  and *assertions* check it (status, headers, JSONPath, response time, body, JSON Schema); results show in the
  response's Tests tab. **Run** a collection or folder from its `⋯` menu to send its requests in order with a summary,
  or use `apipin run` in CI (below)
- **Postman export:** any collection to Postman Collection v2.1 (`<name>.postman_collection.json`), which Postman,
  Insomnia and Bruno can import
- **Tabs** are kept across restarts in `%APPDATA%\ApiPin\scratch-tabs.json`; dark/light theme follows Windows
- **Shortcuts:** `Ctrl+K` palette · `Ctrl+Enter` send · `Ctrl+S` save · `Ctrl+T` new tab · `Ctrl+W` close tab ·
  `Ctrl+Shift+T` reopen · `Ctrl+Tab` / `Ctrl+Shift+Tab` switch tabs (in the browser version: `Alt+T` / `Alt+W` /
  `Alt+Shift+T`, since the browser keeps the Ctrl ones)
- **Two hosts, same UI:** a Windows desktop app, and a browser version served from `localhost`

> Open tabs, including any passwords and tokens you typed into them, are saved as plain text in app data.
> Put credentials in secret variables (`{{apiKey}}`) to keep them encrypted and out of Git.

## Tech stack

.NET 10 · Blazor Hybrid (WPF + WebView2) · plain CSS (light/dark) · xUnit v3

The UI lives in a Razor Class Library (`ApiPin.UI`), so Fluent UI Blazor and Monaco can be added later
without touching the engine or the host.

## Solution layout

| Project | What it is |
|---|---|
| `src/ApiPin.Core` | UI-free engine: request model, URL tools, `{{variable}}` resolver, request builder, auth, executor, body formatting |
| `src/ApiPin.Storage` | JSON persistence: workspaces and collections on disk, open tabs, app settings, Postman export |
| `src/ApiPin.UI` | Razor components, UI state, CSS and a small JS module |
| `src/ApiPin.Desktop` | WPF window hosting the UI in a `BlazorWebView` |
| `src/ApiPin.Web` | Blazor Server host: the same UI in any browser, served from `localhost` |
| `src/ApiPin.Cli` | `apipin run`: runs a collection with captures and assertions, for terminals and CI |
| `samples/echo-api-tests` | A workspace with a log in → create → get → delete flow against the EchoServer |
| `tests/*` | Unit tests, plus integration tests that run every body and auth type against the EchoServer in-process |
| `tools/ApiPin.EchoServer` | Answers any request with a JSON description of it, for manual testing |

## Download

Get the Windows desktop app from [Releases](https://github.com/SilentVis/ApiPin/releases): unzip
`ApiPin-<version>-win-x64.zip` and run `ApiPin\ApiPin.exe`. It's self-contained, so no .NET install is needed.

Releases are built by GitHub Actions when a `v*` tag is pushed, for example:

```bash
git tag v0.2
git push origin v0.2
```

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

### Workspaces and collections

Saved requests live in a **workspace folder**, by default `Documents\ApiPin`. Use the sidebar's `⋯` menu to open or
create another one, for example a folder inside your API's repository so the requests are versioned with the code.
You can also open one at startup:

```bash
dotnet run --project src/ApiPin.Desktop -- D:\repos\orders-api\api-tests
```

```bash
dotnet run --project src/ApiPin.Web -- --workspace D:\repos\orders-api\api-tests
```

The layout is plain JSON with readable names, so it diffs and merges well in Git:

```
api-tests/
├─ apipin.workspace.json        # name, id, global variables
├─ .gitignore                   # generated: keeps .apipin/ (local state) out of Git
├─ environments/
│  ├─ local.env.json
│  └─ staging.env.json
└─ collections/
   └─ orders-api/
      ├─ collection.json
      ├─ list-orders.req.json
      └─ auth/
         ├─ folder.json
         └─ log-in.req.json
```

File and folder names follow the display names (renaming in ApiPin renames them on disk), while ids inside the files
keep links stable. Files ApiPin can't read are skipped and listed in the sidebar rather than breaking the workspace.
Changes from a `git pull` are picked up automatically (**Reload from disk** in the sidebar menu forces it).

Export a collection with **⋯ → Export as Postman v2.1…**. Per-request timeout and HTTP version have no Postman
equivalent and are left out; a Bearer token with a custom prefix is exported as an `Authorization` header;
Windows auth becomes NTLM (with blank credentials when it used the current user).

> Saved requests store auth values (passwords, tokens) as written. Use secret variables instead, for example
> a Bearer token of `{{token}}` with `token` marked secret in an environment, so nothing sensitive is committed.

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

### Command line and CI

`apipin run` runs a collection (or one folder of it) in order. Each request's **captures** set variables for the
requests after it (for example `$.access_token` → `{{token}}`), and its **assertions** check the response. It exits
with 0 when everything passed, 1 when something failed and 2 for usage errors.

```bash
dotnet run --project src/ApiPin.Cli -- run samples/echo-api-tests -e Local
```

```bash
dotnet run --project src/ApiPin.Cli -- run ./api-tests -c "Orders API" -e Staging -r junit -o results.xml -v token=$TOKEN
```

Secret variables come from your ApiPin secret store when it's there; in CI, pass them with `--var key=value` or as
`APIPIN_VAR_<key>` environment variables. Run `apipin --help` for every option. The sample needs the echo server
running (below).

Captures and assertions are edited in the app's Tests tab and saved in the request files; see
`samples/echo-api-tests/collections/items-api` for examples. The runner dialog in the app shows the matching
`apipin run` command for the folder you're running.

### Echo server

Run the echo server to try requests against. It listens on HTTP port 5080, and on HTTPS port 7090 when the
ASP.NET dev certificate is installed (`dotnet dev-certs https --trust`):

```bash
dotnet run --project tools/ApiPin.EchoServer -- --port 5080 --https-port 7090
```

Any path not listed below answers with a JSON description of the request: method, URL, query, decoded auth,
cookies, headers and body, exactly as the server received them.

| Endpoint | What it does |
|---|---|
| `/echo` | Echoes the request (also the fallback for any other path) |
| `/status/{code}` | Answers with that status code |
| `/delay/{ms}` | Waits, then echoes |
| `/redirect/{n}` | Redirects `n` times, then lands on `/echo`; `/redirect-to?url=…&status=307` for one redirect |
| `/auth/basic` | Needs Basic `user` / `pass` (or `?user=…&password=…`) |
| `/auth/bearer` | Needs any Bearer token (or exactly `?token=…`) |
| `/auth/apikey` | Needs `X-API-Key: apipin-key` or `?api_key=apipin-key` |
| `/cookies`, `/cookies/set?a=1`, `/cookies/delete?a` | Shows, sets and deletes cookies |
| `/upload` | Lists multipart fields and files with sizes and SHA-256 hashes |
| `/large/{mb}` | Streams a JSON array of about that many megabytes |
| `/oauth/token` | Fake OAuth 2.0 token endpoint: client credentials, password, authorization code (with PKCE) and refresh token. Client `apipin` / `secret`, user `user` / `pass`; add `?lifetime=60` for short-lived tokens |
| `/oauth/authorize` | Approves at once and redirects back with a code |
| `/oauth/me` | Protected resource: needs a valid token from `/oauth/token` |
| `/items`, `/items/{id}` | A small CRUD API (GET, POST, DELETE) behind tokens from `/oauth/token`, for chaining demos |

### App data

Settings, open tabs and logs live in `%APPDATA%\ApiPin`. Set the `APIPIN_DATA_DIR` environment variable to use
another folder, for a portable setup or to try a build without touching your own data.

### Logs

The desktop app writes one log file per day to `%APPDATA%\ApiPin\logs` (the browser version to `logs\web`) and
keeps the last 14 days.
