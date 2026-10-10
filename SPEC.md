# SPEC — Librus Synergia .NET client + MCP server (`librus-net`)

Handoff brief for a coding agent. Build an unofficial **.NET 9 (C#)** client for
Librus Synergia, a notification poller, and an **MCP server**, so an AI assistant
can read a parent's children's data and (opt-in) reply to teacher messages.

This is a **personal, single-user** tool for a parent with **two children under
one Konto LIBRUS login**. It is unofficial and reverse-engineered; keep it modest
in traffic and treat sending as a sensitive, gated action (see §9).

---

## 1. Goal & non-goals

**Goal**
- Log in once with one Konto LIBRUS account, enumerate linked children, and read
  per-child data (grades, timetable, attendance, messages, notes, announcements,
  homework, notifications).
- A poller that detects *new* items since last run and pushes a notification
  (ntfy / Telegram).
- An MCP server exposing those reads as tools to an AI assistant, plus **one
  opt-in write tool** (`reply_to_message`).

**Non-goals**
- No mass messaging, no bulk scraping, no multi-tenant/SaaS.
- No FCM/push receiver (we poll instead — simpler, no Firebase impersonation).
- No HTML scraping of `synergia.librus.pl` unless a datum is only available there
  (prefer the JSON API).

---

## 2. Tech constraints

- **Runtime:** .NET 9, C#. Target **NativeAOT** (`PublishAot=true`) for a small
  single static binary suitable for a home-lab cron/daemon.
  - NativeAOT implies: use **source-generated** `System.Text.Json`
    (`JsonSerializerContext`), avoid reflection-heavy libs.
- **HTTP:** `HttpClient` + `SocketsHttpHandler` with a shared `CookieContainer`
  per session (portal login is cookie-backed).
- **JSON:** `System.Text.Json` only.
- **HTML parsing** (login page CSRF only, if needed): `AngleSharp`. Do not pull a
  heavy scraping stack; the data layer is JSON, not HTML.
- **MCP:** official **`ModelContextProtocol` C# SDK**
  (`github.com/modelcontextprotocol/csharp-sdk`), stdio transport.
- **Secrets:** read from env or a private file (`~/.config/librus-net/secrets.json`,
  mode 600). Never commit; never log credentials, tokens, or cookie values.

---

## 3. Reference implementations (use, don't reinvent)

The hard part is auth. Do **not** invent the flow — port it from a known-good one.

- **Canonical auth + data reference:** `github.com/andrewkoltsov/librus-sdk`
  (TypeScript). It uses the **current (2026) flow**: portal login →
  `portal/api/v3/SynergiaAccounts` → Bearer per child → `api.librus.pl/3.0`.
  It ships an **OpenAPI document** (`openapi/api_v3_openapi.json`) describing the
  child-scoped GET surface — **generate C# DTOs from it** rather than hand-typing.
- **Secondary reference (Python, scraping flow):** `krzysztofbury/librus-python-api`
  — useful only to cross-check field meanings, not for the transport.

> ⚠️ The legacy OAuth `grant_type=password` (direct `api.librus.pl` login with a
> school login like `1234567u`) was **disabled by Librus in 2026**
> (`unsupported_grant_type`). Do **not** implement it. Use the portal flow.

---

## 4. Authentication flow (implement in `Librus.Auth`)

Port precisely from `librus-sdk`. Expected shape (verify against its source):

1. **Portal login** to `https://portal.librus.pl`:
   - GET the login page (`/konto-librus/login`), extract the CSRF token (HTML).
   - POST credentials to the login action (`/konto-librus/login/action`) with the
     cookie jar; this establishes a portal session.
2. **List children:** GET `https://portal.librus.pl/api/v3/SynergiaAccounts`
   (a.k.a. `/Me` + `/SynergiaAccounts`). Returns all linked child accounts, each
   with its own `id`/`login` and an **access token (Bearer)**.
3. **Per-child API calls:** use that child's Bearer against
   `https://api.librus.pl/3.0/...` with header `Authorization: Bearer <token>`.
4. **Token lifecycle:** reuse the access token until it expires; on `401`, refresh
   the child's bearer (re-run step 2 or the refresh path librus-sdk uses). **Do
   not** re-login from scratch on every call — token churn is the most
   bot-like signal.

Expose:
```csharp
var session = await LibrusSession.LoginAsync(credentials); // portal login
IReadOnlyList<Child> children = await session.ListChildrenAsync();
LibrusChildClient child = session.ForChild(children[0]);    // bearer-scoped
```

---

## 5. Data surface (implement in `Librus.Api`, generate DTOs from OpenAPI)

Base URL: `https://api.librus.pl/3.0`. All reads are child-scoped GETs with the
child's Bearer. Endpoints **confirmed from the official app bundle**:

- **Timetable:** `GET /Timetables?weekStart=YYYY-MM-DD` (Monday) and
  `GET /Timetables?day=YYYY-MM-DD`.
- **Calendar extras:** `/Calendars/Substitutions`, `/Calendars/SchoolFreeDays`,
  `/Calendars/ClassFreeDays`, `/Calendars/TeacherFreeDays`.
- **Grades:** `/Grades` (+ categories, comments, averages).
- **Attendance:** `/Attendances` (+ types).
- **Homework:** `/HomeWorks`, `/HomeWorkAssignments`.
- **Messages:** `/Messages`, `/Messages/Unread`. ⚠️ `/Messages` on `3.0` may
  return **403** for some tokens; fall back to the portal-authenticated
  `wiadomosci.librus.pl/api` backend (librus-sdk calls this `forChildWiadomosci`).
- **Announcements / notes:** `/SchoolNotices`, `/Notes`.
- **Misc:** `/Me`, `/LuckyNumbers`, `/Subjects`, `/Classrooms`,
  `/NotificationCenter`.

> Treat the OpenAPI doc as the source of truth for exact paths, query params, and
> response shapes. The list above is the scope, not the schema.

### 4a. Change feed (for notifications) — verify shape
The app builds its timeline from **`GET /ChangeRegister`** (optionally
`?provider=…`): a list of change entries each with an `Id`. The app keeps a cursor
(last-seen `Id`) and treats higher ids as new. **Confirm whether this endpoint is
usable with the portal-bearer flow.** If reliable, prefer it as the poll target
(one cheap call per child). If not exposed/usable, fall back to **diffing fetched
collections** (see §6). Implement behind an interface so either strategy plugs in.

---

## 6. Notification poller (implement in `Librus.Poller`)

- For each child, on a schedule, fetch the "what's new" signal and emit new items.
- **State:** persist seen ids per child per category to
  `~/.config/librus-net/state/<childId>.json` (atomic write + checkpoint before
  acting, so an interrupted run doesn't lose or double-fire — see how
  librus-python-api checkpoints).
- **Strategy A (preferred):** poll `/ChangeRegister`, diff against cursor.
- **Strategy B (fallback):** fetch messages-unread / grades / notes / notices and
  diff ids against stored state.
- **Cadence:** every **12–15 min with ±jitter**. Process children **sequentially**,
  not in parallel bursts. This mirrors the official app (15-min feed timer) and
  stays well under anything that would look abusive.
- **Sink:** pluggable `INotifier` → implement `NtfyNotifier` and
  `TelegramNotifier`. A new teacher message → notification with subject + sender +
  a deep link to the thread in Synergia (so the human replies there by default).

---

## 7. MCP server (implement in `Librus.Mcp`, official C# SDK, stdio)

Expose tools with correct **MCP tool annotations** (`readOnly` / `idempotent` /
`destructive`). Each tool takes a `child` selector (id or login).

**Read-only tools** (annotate `readOnly=true`):
- `list_children`
- `get_grades(child)`, `get_attendance(child)`
- `get_timetable(child, weekStart)`
- `list_messages(child, folder?, unreadOnly?)`, `get_message(child, id)`
- `list_notes(child)`, `list_announcements(child)`, `get_homework(child)`
- `get_lucky_number(child)`, `get_notifications(child)` (ChangeRegister-backed)

**Write tool** (annotate `destructive=true`, **disabled by default**):
- `reply_to_message(child, threadOrMessageId, body)` — see §8.

Config:
- Credentials via env/secrets file (§2), never via MCP client project config.
- A flag `LIBRUS_ENABLE_SEND` (default **false**) gates registration of the write
  tool. When false, the server advertises only read tools.
- Structured, **secret-safe** logging (urls, status, duration, stable error codes;
  never bodies/tokens/cookies).

---

## 8. `reply_to_message` — the one write path (reverse-engineer carefully)

**This is the only non-GET in the project. Treat it as the riskiest, most fragile
part.** No reference library implements send on the current flow, so the agent
must reverse-engineer the request shape:

- Target the portal-authenticated **`wiadomosci.librus.pl/api`** inbox backend
  (same origin librus-sdk reads messages from). Reuse that authenticated session.
- Determine the POST shape for *replying to an existing thread*: recipient(s)
  (derive from the original message's sender), subject, body, thread/parent id,
  and any CSRF/anti-forgery token the form requires.
- Scope it strictly to **replying to a received message** — do not implement
  arbitrary/new-recipient composition.
- Require an explicit confirmation contract: the tool must echo back
  `{to, subject, body}` and only send when called with `confirm: true`. The MCP
  client should be configured to require human approval for destructive tools.

If the request shape can't be confirmed safely, **ship read-only** and leave a
`NotImplemented` stub with notes — a working reader with no send is a correct
outcome; a half-working sender is not.

---

## 9. Safety, ToS, and anti-blocking (hard requirements)

- **Single user, personal use.** No concurrency storms: sequential per child,
  jittered 12–15 min cadence, token reuse + refresh (never re-login per call).
- **Retry/backoff:** retry only idempotent GETs on `408, 425, 429, 500, 502, 503,
  504`; exponential backoff + full jitter; honor `Retry-After` on 429; cap
  attempts (e.g. 3). Surface `503` as a maintenance state, don't hammer.
- **Run from a residential/home IP** (home lab), not a datacenter range.
- **Unofficial API** — may break when Librus changes login/version; fail cleanly
  with clear error codes. This is expected and acceptable for a personal tool.
- **Sending** is an active action closer to the line of the Synergia regulamin;
  keep it opt-in, reply-only, human-confirmed.
- Never log or persist credentials, bearer tokens, or cookies.

---

## 10. Suggested project layout

```
librus-net/
  src/
    Librus.Auth/        # portal login, SynergiaAccounts, per-child bearer, refresh
    Librus.Api/         # HttpClient wrapper + OpenAPI-generated DTOs (read surface)
    Librus.Poller/      # state store, diff strategies, INotifier (ntfy/telegram)
    Librus.Mcp/         # MCP server (official C# SDK), tool defs + annotations
    Librus.Cli/         # `login`, `children`, `grades`, `poll`, `mcp` verbs
  openapi/
    api_v3_openapi.json # copied from librus-sdk; generate DTOs from this
  tests/
  SPEC.md               # this file
```

Deliverables: NativeAOT-publishable `Librus.Cli` (with an `mcp` verb that starts
the stdio MCP server), unit tests for auth parsing + diff logic, and a README
with the `claude mcp add` / secrets-file setup.

---

## 11. Build order (recommended for the agent)

1. `Librus.Auth` — portal login + `SynergiaAccounts` + per-child bearer + refresh.
   Prove it with a CLI `children list` and `me --child <id>`.
2. `Librus.Api` — generate DTOs from OpenAPI; implement grades + timetable first,
   then the rest. CLI read verbs with `--format json`.
3. `Librus.Poller` — state store + diff (Strategy B first, then ChangeRegister if
   confirmed) + ntfy notifier. CLI `poll` verb.
4. `Librus.Mcp` — wrap reads as annotated read-only tools; stdio server; `mcp` verb.
5. `reply_to_message` — **last**, behind `LIBRUS_ENABLE_SEND`, reply-only,
   confirm-gated. Ship read-only if the shape can't be confirmed.
6. NativeAOT publish + README.

---

## 12. Open items the agent must resolve (don't guess silently)

- Exact portal login request (CSRF field names, action URL) — read from librus-sdk
  source and verify live.
- `SynergiaAccounts` response shape and where the per-child bearer lives.
- Whether `/ChangeRegister` works under the portal-bearer flow (decides §6 strategy).
- The `/Messages` 403 condition and the `wiadomosci.librus.pl/api` fallback shape.
- The `reply_to_message` POST contract (§8).

For each, if it can't be confirmed, implement the documented fallback and leave a
clear `// TODO(verify):` note rather than shipping a guess.
