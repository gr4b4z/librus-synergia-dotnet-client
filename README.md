# librus-net

Unofficial **.NET 9** client for **Librus Synergia** (Polish school e-register), a notification
poller and an **MCP server**, for a parent with one Konto LIBRUS login and several linked children.

- Auth is the current (2026) family-portal flow, ported from
  [andrewkoltsov/librus-sdk](https://github.com/andrewkoltsov/librus-sdk):
  `portal.librus.pl` login → `api/v3/SynergiaAccounts` → per-child Bearer → `api.librus.pl/3.0`.
  The legacy `grant_type=password` flow (disabled by Librus in 2026) is **not** implemented.
- DTOs are **generated** from librus-sdk's OpenAPI documents (`openapi/`), with
  source-generated `System.Text.Json` — the CLI publishes as a single **NativeAOT** binary.
- Read-only by default. The only write path (`reply_to_message`) is opt-in, reply-only,
  confirm-gated, and currently a stub (see [docs/verification.md](docs/verification.md)).

> ⚠️ Unofficial and reverse-engineered. It can break whenever Librus changes something; failures
> surface as stable error codes. Personal, single-user use only; keep traffic modest and run it from
> your home network.

## Layout

```
src/
  Librus.Auth/    portal login (CSRF), SynergiaAccounts, per-child bearer + refresh, retry/backoff, secret-safe logging
  Librus.Api/     LibrusChildClient (77 generated GETs), wiadomosci fallback, name-resolved views
  Librus.Poller/  state store (atomic, outbox checkpoint), diff / ChangeRegister strategies, ntfy + Telegram
  Librus.Mcp/     stdio MCP server on the official ModelContextProtocol C# SDK
  Librus.Cli/     `librus-net` binary: login, children, grades, …, poll, mcp
tools/Librus.OpenApiGen/   OpenAPI → C# DTO + JsonSerializerContext + operations generator
openapi/                   api_v3 + wiadomosci OpenAPI documents (from librus-sdk, MIT)
tests/Librus.Tests/        xUnit: auth parsing, refresh, retry, fallback, diff/state, MCP gating
```

## Build

```bash
dotnet build
dotnet test tests/Librus.Tests

# single static-ish binary (needs clang + zlib1g-dev on Linux)
dotnet publish src/Librus.Cli -c Release -r linux-x64 -o artifacts/linux-x64
./artifacts/linux-x64/librus-net --help
```

Other RIDs: `linux-arm64` (Raspberry Pi / home-lab ARM), `osx-arm64`, `win-x64`
(NativeAOT does not cross-compile across operating systems — build on the target OS).

## Credentials

Environment variables win; otherwise a private JSON file is read
(`~/.config/librus-net/secrets.json`, override with `LIBRUS_SECRETS_FILE`):

```bash
mkdir -p ~/.config/librus-net
cat > ~/.config/librus-net/secrets.json <<'JSON'
{
  "email": "parent@example.com",
  "password": "…",
  "telegramBotToken": "optional",
  "ntfyToken": "optional"
}
JSON
chmod 600 ~/.config/librus-net/secrets.json
```

| Variable | Purpose |
| --- | --- |
| `LIBRUS_PORTAL_EMAIL`, `LIBRUS_PORTAL_PASSWORD` | Konto LIBRUS login (alternative to the secrets file) |
| `LIBRUS_SECRETS_FILE` | Path of the secrets file |
| `LIBRUS_CONFIG_DIR` | Config/state root (default `~/.config/librus-net`) |
| `LIBRUS_CHILD` | Default child selector (id, login or first name) |
| `LIBRUS_TIMEOUT_MS` | Request timeout (default 30000) |
| `LIBRUS_LOG_LEVEL` | `debug` \| `info` \| `warn` \| `error` (logs go to stderr) |
| `LIBRUS_ENABLE_SEND` | `true` registers the `reply_to_message` MCP tool (default off) |

Credentials, bearer tokens and cookies are never logged, printed or persisted. Logs contain only
method, (redacted) URL, status, duration and stable error codes.

## CLI

```bash
librus-net login                       # verifies the login, prints the number of children
librus-net children                    # ID / LOGIN / NAME
librus-net grades --child Ania         # child = id, login or (unique) first name
librus-net timetable --child 123456 --week-start 2026-10-12
librus-net attendance --child Ania --since 2026-09-01
librus-net messages --child Ania --unread
librus-net message --child Ania --id 501
librus-net notes | announcements | homework | lucky-number | notifications --child Ania
librus-net me --child Ania             # raw /Me
librus-net grades --child Ania --format json         # compact JSON view
librus-net grades --child Ania --format json --raw   # raw API DTO
```

Errors go to stderr (`--format json` → `{"error":{"code":"…","message":"…"}}`), exit code 1
(2 for usage errors). Codes: `AUTHENTICATION_FAILED`, `AUTH_REFRESH_FAILED`, `PORTAL_LOGIN_PAGE_INVALID`,
`ACCESS_DENIED`, `NOT_FOUND`, `RATE_LIMITED`, `SERVICE_MAINTENANCE`, `NETWORK_ERROR`,
`NETWORK_TIMEOUT`, `RESPONSE_VALIDATION_FAILED`, `CONFIGURATION_ERROR`, `CHILD_NOT_FOUND`,
`AMBIGUOUS_CHILD`, `CHILD_REQUIRED`, `SEND_NOT_IMPLEMENTED`, `INVALID_ARGUMENT`.

## MCP server

```bash
claude mcp add librus -- /opt/librus-net/librus-net mcp
# or with explicit env (prefer the secrets file over putting the password here):
claude mcp add librus -e LIBRUS_SECRETS_FILE=$HOME/.config/librus-net/secrets.json -- /opt/librus-net/librus-net mcp
```

Generic MCP client config:

```json
{
  "mcpServers": {
    "librus": { "command": "/opt/librus-net/librus-net", "args": ["mcp"] }
  }
}
```

The server logs in lazily on the first tool call and reuses the session (no re-login per call).

| Tool | Annotations | Notes |
| --- | --- | --- |
| `list_children` | readOnly, idempotent | id, login, name, state |
| `get_grades(child)` | readOnly, idempotent | subject / category / teacher names resolved |
| `get_attendance(child, since?)` | readOnly, idempotent | default: last 30 days |
| `get_timetable(child, weekStart?)` | readOnly, idempotent | any date → that week's Monday |
| `list_messages(child, folder?, unreadOnly?, limit?)` | readOnly, idempotent | inbox only; api3 → wiadomosci fallback |
| `get_message(child, id)` | readOnly, idempotent | full body |
| `list_notes(child)`, `list_announcements(child)` | readOnly, idempotent | |
| `get_homework(child, since?)` | readOnly, idempotent | default: from today |
| `get_lucky_number(child)` | readOnly, idempotent | |
| `get_notifications(child)` | readOnly, idempotent | `/ChangeRegister`; `{available:false}` if not exposed |
| `reply_to_message(child, threadOrMessageId, body, confirm?)` | **destructive**, only with `LIBRUS_ENABLE_SEND=true` | preview `{to, subject, body}` unless `confirm=true`; sending is not implemented yet |

Configure your MCP client to require human approval for destructive tools.

## Notification poller

```bash
# try it: one pass, print instead of pushing
librus-net poll --dry-run

# ntfy
export LIBRUS_NTFY_TOPIC=my-secret-topic        # LIBRUS_NTFY_URL=https://ntfy.example.org, LIBRUS_NTFY_TOKEN=…
# Telegram
export LIBRUS_TELEGRAM_BOT_TOKEN=123:ABC LIBRUS_TELEGRAM_CHAT_ID=4242

librus-net poll                  # daemon: every 12–15 min (jittered), children one after another
librus-net poll --once           # one pass (for cron), still state-safe
librus-net poll --notifier telegram --child Ania --interval-min 13 --interval-max 15
```

- **Strategy B (default, `--strategy diff`)**: fetches unread-capable inbox, grades, notes,
  notices and homework, diffs ids against `~/.config/librus-net/state/<childId>.json`.
- **Strategy A (`--strategy changeregister`)**: `/ChangeRegister` cursor (one call per child) — unverified,
  see [docs/verification.md](docs/verification.md). `--strategy auto` tries A and falls back to B per child.
- The first run of each category only records a baseline (no notification flood).
- New items are written to an **outbox in the state file before** any notifier is called and removed one
  by one after delivery, so a crash neither loses nor re-detects items; a failed push is retried next tick.
- Notifications are in Polish, e.g. `Ania: ocena 5 — Matematyka`, `Ania: wiadomość — Wycieczka`
  (`Od: …`, with a link to reply in Synergia).

Prefer the daemon over cron: it keeps the portal session and bearer tokens in memory across ticks
instead of logging in every 15 minutes. Example systemd user unit:

```ini
# ~/.config/systemd/user/librus-poll.service
[Unit]
Description=librus-net notification poller
After=network-online.target

[Service]
ExecStart=/opt/librus-net/librus-net poll
Environment=LIBRUS_NTFY_TOPIC=my-secret-topic
Restart=on-failure
RestartSec=5min

[Install]
WantedBy=default.target
```

```bash
systemctl --user daemon-reload && systemctl --user enable --now librus-poll
```

## Library use

```csharp
using Librus;
using Librus.Api;
using Librus.Api.Views;

using var session = await LibrusSession.LoginAsync(LibrusCredentials.Resolve());
IReadOnlyList<Librus.Portal.Child> children = await session.ListChildrenAsync();
LibrusChildClient child = session.ForChild(children[0]);       // bearer-scoped

var grades = await child.GetGradesAsync();                     // raw generated DTO
var views = await child.GetGradeViewsAsync();                  // names resolved
var inbox = await child.ListInboxAsync(unreadOnly: true);      // api3 or wiadomosci fallback
```

## Safety

- GETs only (plus the portal login POST and the AutoLoginToken handoff POST); non-GETs are never retried.
- Retries only on 408/425/429/500/502/503/504, max 3 attempts, exponential backoff with full jitter,
  `Retry-After` honored; Librus `{"Status":"Maintenance"}` / 503 → `SERVICE_MAINTENANCE`.
- 401 → one bearer refresh via the portal (`SynergiaAccounts/fresh/{login}`), re-login only if the
  portal cookie session itself died.

## Regenerating DTOs

```bash
./tools/generate.sh    # rewrites src/Librus.Api/Generated/*.g.cs from openapi/*.json
```

## Credits

Auth flow, wiadomosci handoff and OpenAPI documents come from
[andrewkoltsov/librus-sdk](https://github.com/andrewkoltsov/librus-sdk) (MIT); see `openapi/README.md`.
