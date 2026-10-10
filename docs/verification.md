# Open items (SPEC §12) — status

Everything below is marked in code with `TODO(verify)`. Nothing here was guessed silently: each item
either follows librus-sdk (which was verified against live accounts in 2026) or ships the documented
fallback.

| # | Item | Status | Where |
| - | ---- | ------ | ----- |
| 1 | Portal login request (CSRF field, action URL) | **Verified 2026-10-10** against the live login page: `GET /konto-librus/login` renders `<form id="login" action="…/konto-librus/login/action">` with `email`, `password` and hidden `_token`. Matches librus-sdk. Login success is checked with `GET /api/v3/Me`. | `Librus.Auth/Portal/PortalClient.cs`, `CsrfTokenExtractor.cs` |
| 2 | `SynergiaAccounts` shape, where the bearer lives | Ported from librus-sdk (validated there against a live two-child account): `{ lastModification, accounts: [{ id, accountIdentifier, group, accessToken, login, studentName, state, scopes: [] \| "" }] }`. Bearer = `accounts[].accessToken`. Refresh path = `GET /api/v3/SynergiaAccounts/fresh/{login}`. Needs a live login to re-confirm. | `Librus.Auth/Portal/Child.cs`, `LibrusSession.cs` |
| 3 | `/ChangeRegister` under the portal bearer | **Unconfirmed.** Not in the librus-sdk OpenAPI. Implemented behind `IChangeSource` (`ChangeRegisterSource`) with a loose parser; the default poll strategy is the collection diff (`--strategy diff`). `--strategy auto` tries ChangeRegister and falls back per child on 403/404/unexpected shape. To verify: `librus-net notifications --child <id>`. | `Librus.Poller/Sources/ChangeRegisterSource.cs`, `LibrusChildClient.GetChangeRegisterAsync` |
| 4 | `/Messages` 403 and the `wiadomosci.librus.pl/api` fallback | Ported from librus-sdk `WiadomosciMessagesClient`: fresh bearer → `POST api.librus.pl/2.0/AutoLoginToken` → `GET synergia.librus.pl/loguj/token/{Token}/przenies/uczen/widok/centrum_powiadomien` → `GET /wiadomosci3` → cookie-backed `GET wiadomosci.librus.pl/api/inbox/messages[/{id}]`, `/api/inbox/unreadMessagesCount`. The client tries api 3.0 first and switches permanently (per child, per process) on 403. Sender display names are read best-effort from unknown fields (`senderFirstName`/`senderLastName`/`senderName`). The web deep link `wiadomosci.librus.pl/module/inbox/details/{id}` is unverified. | `Librus.Api/Wiadomosci/WiadomosciClient.cs`, `LibrusChildClient.Messages.cs`, `Messages/MessageNormalizer.cs` |
| 5 | `reply_to_message` POST contract | **Not confirmed → shipped read-only** (SPEC §8). The MCP tool exists only with `LIBRUS_ENABLE_SEND=true`, is reply-only, returns a `{to, subject, body}` preview unless `confirm=true`, and then fails with `SEND_NOT_IMPLEMENTED`. No POST to wiadomosci is ever issued. To implement: capture the reply request from the Wiadomości web UI and fill in `IMessageReplySender`. | `Librus.Api/Messages/MessageReply.cs`, `Librus.Mcp/LibrusTools.cs` |

## How to verify with a real account

```bash
export LIBRUS_PORTAL_EMAIL=… LIBRUS_PORTAL_PASSWORD=…
librus-net login --verbose                     # 1, 2
librus-net children
librus-net notifications --child <id>          # 3: JSON feed, or ACCESS_DENIED/NOT_FOUND
librus-net messages --child <id> --verbose     # 4: shows whether api3 or the wiadomosci handoff is used
```

Logs never contain credentials, bearer tokens, cookies or bodies; the one-time auto-login token in the
Synergia URL is masked as `/loguj/token/REDACTED/`.
