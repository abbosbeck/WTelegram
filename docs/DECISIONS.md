# Architectural Decisions

Living document of the significant design calls in this project. New entries go at the bottom. Keep entries short; one paragraph per decision is the target.

---

## Project layout (Clean Architecture)

5 projects under `src/`, single solution `WTelegram.slnx` at repo root:

```
src/
  Domain/            Pure types. References WTelegramClient pragmatically
                     (MediaItem wraps TL.Document / TL.Photo / TL.StoryItem).
  Application/       Options, abstractions, login state machine.
                     References Domain only.
  Infrastructure/    EF Core + Postgres, AES-GCM, WTelegramClient,
                     YoutubeDLSharp. References Application + Domain.
  ConsoleUI/         Interactive debug host. References Application + Infrastructure.
  Bot/               Telegram bot host (Phase 2 placeholder). Same refs as ConsoleUI.
```

Each non-host project exposes a single DI extension: `AddApplication()`, `AddInfrastructure(IConfiguration)`. Host projects compose them in `Program.cs`.

The `Console` folder/csproj was renamed `ConsoleUI` to avoid colliding with `System.Console`.

---

## Domain may reference WTelegramClient

Pragmatic call. Wrapping every `TL.Document` / `TL.Photo` in a pure DTO only to unwrap it again in Infrastructure is busywork. Revisit if we ever add non-Telegram media sources to `MediaItem`.

---

## Per-user MTProto sessions, no shared service account

Each end-user logs in to Telegram via their **own** account. We do **not** maintain a shared "service account" that downloads on everyone's behalf. Reasons:
- Cleanest legal/ethical posture: each user only accesses what they're authorized to.
- Each user can access their own private channels and stories (bots can't read stories at all).
- No single account becomes a flood-wait / ban bottleneck.

Trade-off: each user goes through phone + SMS code + (optional) 2FA the first time, inside the bot. After that, their encrypted session lives in Postgres and is rehydrated automatically.

---

## Session storage: Postgres + EF Core, AES-GCM at rest

- **DB**: Postgres (Azure Flexible Server in this deployment).
- **Schema**: one table `user_sessions` keyed by `telegram_user_id`. Encrypted session bytes + 12-byte nonce + 16-byte tag + metadata (`phone_number`, `display_name`, `created_at`, `last_used_at`, `is_active`).
- **Cipher**: AES-GCM 256, 32-byte key supplied via `Sessions:EncryptionKey` (base64). **Required at startup** — app refuses to run without it.
- **Key bootstrap**: `dotnet run -- gen-key` prints a fresh random key. Store via user-secrets (`dotnet user-secrets set "Sessions:EncryptionKey" "<base64>"`) or env var `SESSIONS__ENCRYPTIONKEY`.
- Rejected alternatives: SQLite (Postgres was already provisioned); plaintext storage (obvious no); auto-generated key on first run (too easy to lose and brick all stored sessions).

---

## SessionPool: per-user WTelegramClient with LRU eviction

`Infrastructure.Sessions.SessionPool` owns a `ConcurrentDictionary<long, Client>` keyed by Telegram user ID.

- **Lazy connect**: a `Client` is built on first `AcquireAsync(userId)` for that user. Encrypted session is loaded from Postgres into a `PostgresSessionStream` (a `Stream` subclass we pass to WTelegramClient's ctor), which flushes encrypted bytes back to Postgres on writes (debounced 1s).
- **Capacity cap**: `Sessions:MaxConcurrentSessions` (default 200). When full, oldest idle client is evicted to make room.
- **Idle eviction**: `SessionPoolEvictionService` hosted background service evicts clients idle longer than `Sessions:IdleEvictionMinutes` (default 15). Encrypted bytes stay in Postgres, so next request rehydrates instantly.
- **Login wiring**: WTelegramClient's config callback (`phone_number` / `verification_code` / `password`) is per-user. The callback awaits TaskCompletionSources held by `Application.Sessions.LoginSession`, which is registered via `LoginCoordinator` by the UI driver (console today, bot tomorrow).

---

## Login state machine

`Application.Sessions.LoginSession`:
- States exposed as `IsPhoneAwaited`, `IsCodeAwaited`, `IsPasswordAwaited` properties.
- Driver calls `SubmitPhone(string)`, `SubmitCode(string)`, `SubmitPassword(string)` whenever the user provides input.
- The pool's config callback awaits the matching TCS.

`Application.Sessions.LoginCoordinator` is a singleton `ConcurrentDictionary<long, LoginSession>` so the driver (`ConsoleUi.DrivePromptsAsync` in console, `BotUpdateHandler` in bot) can hand input to the right user's session.

This makes the entire login flow reusable across console and bot — same engine, different IO.

---

## yt-dlp / ffmpeg via YoutubeDLSharp, auto-downloaded

`WebVideoDownloader` lazily downloads `yt-dlp.exe` and `ffmpeg.exe` into `Sessions:ToolsDirectory` (default: `tools/` next to the executable) on first use. `Sessions:AutoUpdate=true` re-downloads yt-dlp on every startup; failures are non-fatal.

URL downloads dedup via the same `DownloadManifest` used by Telegram downloads (URL-keyed entries prefixed `url:`).

---

## Old `telegram_session.dat` is not migrated

Decided **option B**: don't try to import the pre-Postgres legacy session file. On first Telegram action, the user (operator) re-logs in. The fresh session lands in Postgres encrypted. The old `.dat` file can be deleted.

---

## Telegram codes for first login

Telegram refuses to send the **first** login code to a regular Telegram chat. The user must read it from their official Telegram app: **Settings → Devices → the new session entry** (the code is part of the title). Subsequent codes can appear in chats normally.

This is a Telegram-side rule, not ours. The console UI surfaces this in a hint when prompting for the code; the bot UI must do the same.

---

## Required configuration (user-secrets per host project)

`ConsoleUI` keeps the user-secrets GUID `poc-telegram-downloader-secrets` (so the operator's existing secrets carry over).
`Bot` uses `bot-telegram-downloader-secrets` (set when Phase 2 lands).

Required keys (set via `dotnet user-secrets set` from each host's project directory):
- `ConnectionStrings:Postgres` — full Npgsql connection string with password.
- `Sessions:EncryptionKey` — base64 32 bytes, from `dotnet run -- gen-key`.
- `Telegram:ApiId` — int, from <https://my.telegram.org>.
- `Telegram:ApiHash` — string, same source.
- `Telegram:OwnerUserId` — operator's numeric Telegram user ID (ConsoleUI only).
- `Bot:Token` — from @BotFather (Phase 2 only).

---

## Phases

### Phase 1 — Infrastructure ✅ DONE
- Postgres + EF Core, `UserSession` entity, initial migration.
- `SessionPool` service + idle eviction.
- AES-GCM session encryption.
- `TelegramService` refactored to take `Client` per call (was DI singleton before).
- `ConsoleUI` works against the new pool. First Telegram action triggers in-console login.

### Phase 2 — Bot frontend (pending)
- Add `Telegram.Bot` package to `Bot` project. Long-polling worker (`BotUpdateHandler` `BackgroundService`).
- Commands: `/login`, `/cancel`, `/logout`, `/status`.
- In-chat phone / code / 2FA prompt state machine, routed through the existing `LoginCoordinator`.
- Bot persists per-user conversation state (which step the user is on) — probably in-memory `ConcurrentDictionary` for now, Postgres later if needed.
- `ConsoleUI` keeps working unchanged.

### Phase 3 — Wire download flows to the bot (pending)
- `/download <url>` → yt-dlp (`WebVideoDownloader`).
- `/download <t.me link>` → `MessageLinkResolver` + `TelegramService.DownloadMediaAsync` using the calling user's MTProto client.
- `/chat <username>` → list recent media (paginated via Telegram inline keyboard buttons).
- Stream finished files back to the requesting user via the bot (`SendDocumentAsync`).
- Delete local files after upload (storage hygiene).

### Phase 4 (implied, post-MVP)
- Per-user `DownloadManifest` (currently file-based and global — fine for one operator, not for multi-tenant).
- Rate limits / per-user quotas.
- Structured logging + metrics.
- Docker + CI.
- Maybe: WTelegramClient bot-token MTProto login for any "the bot itself does it" features.

---

## Open issues / known sharp edges

- `DownloadManifest` is currently keyed only by `(chatId, msgId)`. For multi-user it must include `userId`. Address in Phase 4 alongside per-user output directories.
- `PostgresSessionStream` flushes via fire-and-forget `Task.Run`. If the process is force-killed mid-flush, the latest session delta is lost (previous flush is intact). Acceptable; revisit if it bites.
- Visual Studio 18.x Copilot Chat keys conversations to the solution file. After renaming/replacing the .sln, prior chats are not auto-attached. There is no built-in export. This file is the durable substitute.

---

## How to continue Phase 2 in a fresh Copilot chat

If you've opened a new VS solution and lost the prior conversation, paste this as your first message in the new chat:

> I'm continuing a previous session on the project at `C:\Users\Admin\source\repos\PoC\WTelegram.slnx`. Read `docs/DECISIONS.md` for the full architecture and decisions log. Phase 1 is done. Please start Phase 2: add `Telegram.Bot` (long-polling) inside the `Bot` project, implement `/login`, `/cancel`, `/logout`, `/status` commands, and route the in-chat phone/code/2FA prompts through the existing `Application.Sessions.LoginCoordinator`. The `ConsoleUI` project must keep working — both hosts share `Application` + `Infrastructure`. Confirm the plan, then implement in small batches.
