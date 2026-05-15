# TelegramDownloader

A small .NET 10 console app that logs into your Telegram account via [WTelegramClient](https://github.com/wiz0u/WTelegramClient) (MTProto) and downloads videos from any chat or channel you have access to.

> ⚠️ **Personal-use tool.** It uses *your* Telegram account, not the Bot API. Don't use it to scrape content you don't have the right to download.

---

## Features

- Login with phone number + verification code (2FA supported); session is persisted so you only sign in once.
- Browse recent chats / channels / groups.
- Scan the last *N* messages of a peer and list all video documents (with filename, size, duration).
- Bulk-select downloads via `1,3`, `1-5`, or `all`.
- Live progress bar with throughput (MB/s).
- Configurable via `appsettings.json`, user-secrets, environment variables, or a local override file.
- Graceful Ctrl+C shutdown.

---

## Requirements

- [.NET 10 SDK](https://dotnet.microsoft.com/download)
- A Telegram API ID & API Hash — get them at <https://my.telegram.org> → **API development tools**.

---

## Project layout

```
PoC/
├── Program.cs                          # Composition root (Generic Host + DI)
├── appsettings.json                    # Config skeleton
├── Configuration/
│   ├── TelegramOptions.cs              # Bound options
│   └── WTelegramConfigProvider.cs      # WTelegram config callback
├── Services/
│   ├── TelegramService.cs              # Connect, resolve peer, list, download
│   └── VideoItem.cs
├── Ui/
│   ├── ConsoleUi.cs                    # BackgroundService – interactive menu
│   └── IConsolePrompt.cs
└── Helpers/
    └── FileHelpers.cs                  # FormatSize, SanitizeFileName
```

---

## Configuration

The app reads configuration from these sources, in order (later wins):

1. `appsettings.json`
2. `appsettings.Local.json` *(git-ignored, ideal for local overrides)*
3. User-secrets (`UserSecretsId` is set in `PoC.csproj`)
4. Environment variables (prefix `Telegram__`)

### Settings

| Key                          | Description                                            | Default                                    |
|------------------------------|--------------------------------------------------------|--------------------------------------------|
| `Telegram:ApiId`             | Numeric API ID from my.telegram.org                    | *(required)*                               |
| `Telegram:ApiHash`           | API hash from my.telegram.org                          | *(required)*                               |
| `Telegram:OutputDirectory`   | Where downloaded videos are saved                      | `%USERPROFILE%\TelegramDownloads`          |
| `Telegram:SessionPathname`   | Path to WTelegram session file (relative → joins with `OutputDirectory`) | `telegram_session.dat`         |

### Setting credentials

**Option A – user-secrets (recommended for dev):**

```powershell
cd PoC
dotnet user-secrets set "Telegram:ApiId"   "1234567"
dotnet user-secrets set "Telegram:ApiHash" "your-api-hash"
```

**Option B – environment variables:**

```powershell
$env:Telegram__ApiId   = "1234567"
$env:Telegram__ApiHash = "your-api-hash"
```

**Option C – `PoC/appsettings.Local.json`** (already git-ignored):

```json
{
  "Telegram": {
    "ApiId": 1234567,
    "ApiHash": "your-api-hash"
  }
}
```

If `ApiId` / `ApiHash` are missing the app throws a clear `InvalidOperationException` on startup.

---

## Running

```powershell
git clone https://github.com/abbosbeck/WTelegram.git
cd WTelegram/PoC
dotnet run
```

On first launch you'll be prompted for:

1. Phone number (international format, e.g. `+998901234567`)
2. The verification code Telegram sends you
3. Your 2FA password (only if enabled)

Subsequent runs reuse the session file and skip the login prompts.

### Menu

```
1 – Download video from a chat/channel
2 – List recent chats
0 – Exit
```

**Download flow:**

1. Pick `1`.
2. Enter a chat by `@username` or numeric ID.
3. Choose how many recent messages to scan (default `50`).
4. Pick which videos to download: `2`, `1,3,7`, `1-5`, or `all`.

Videos land in `OutputDirectory`. Existing filenames are suffixed with the message ID to avoid collisions.

---

## Security notes

- **Never commit your `ApiHash`** — it's effectively a credential. Use user-secrets, env vars, or `appsettings.Local.json`.
- The session file (`telegram_session.dat` by default) contains your authentication keys. Treat it like a password:
  - `.gitignore` already excludes `*.session` and `telegram_session*`.
  - Don't share it, don't commit it, don't store it in a synced cloud folder unencrypted.
- If you previously committed a real `ApiHash`, **rotate it at my.telegram.org** and consider rewriting git history (`git filter-repo` / BFG).

---

## Tech stack

- .NET 10 / C# 14
- [WTelegramClient](https://github.com/wiz0u/WTelegramClient) 4.4.4 (MTProto)
- `Microsoft.Extensions.Hosting` (Generic Host, DI, options, logging)
- `Microsoft.Extensions.Configuration.UserSecrets`

---

## License

This is a personal proof-of-concept. Add a license of your choice (e.g. MIT) before publishing.
