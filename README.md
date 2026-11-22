# WawshniBot

Arabic-first Telegram assistant built on .NET 10. ASP.NET Core, Telegram long polling, Neon PostgreSQL, and Playwright for short-lived Moodle browser sessions.

## Local setup

Prerequisite: .NET 10 SDK.

```powershell
dotnet restore
dotnet build -c Release
$env:TelegramBot__Token = "YOUR_ROTATED_TOKEN"
$env:TelegramBot__AuthorizedUserId = "YOUR_TELEGRAM_USER_ID"
dotnet run
```

Never commit `.env` files or credentials. Rotate any Telegram token shared in chat and configure the replacement only in local secrets or the hosting environment.

## Neon database

The project is linked to the Neon project and `production` branch with the Neon CLI. `neon.ts` and `package.json` declare the Neon configuration; `.env.local` contains connection variables pulled by the CLI and is ignored by Git.

The .NET app accepts either `ConnectionStrings__DefaultConnection` or `DATABASE_URL`. It uses Npgsql's pooled `NpgsqlDataSource`, requires TLS for PostgreSQL URLs, and runs a read-only `SELECT 1` check at `/health/ready`. There was no EF Core setup in the existing app, so Moodle sessions use Npgsql directly. `Moodle/001_create_moodle_sessions.sql` is an additive, idempotent schema migration; the app also runs the same `CREATE TABLE IF NOT EXISTS` before session operations.

To configure Render, copy the full PostgreSQL connection URI from Neon into:

```text
ConnectionStrings__DefaultConnection
```

Do not use the Neon Data API REST URL here. Keep the URI private. Locally, the Neon CLI can refresh `.env.local` with `neon env pull`; make `DATABASE_URL` available to the process or set `ConnectionStrings__DefaultConnection` in the shell.

## Render Web Service

The GitHub repository root is the project root. Create a Docker Web Service from the repository and leave the root directory empty (repository root). The `Dockerfile` is at that root. Add:

- `ASPNETCORE_ENVIRONMENT=Production`
- `TelegramBot__Token` = rotated Telegram token
- `TelegramBot__AuthorizedUserId` = your numeric Telegram user ID
- `ConnectionStrings__DefaultConnection` = full Neon PostgreSQL connection URI
- `Moodle__BaseUrl=https://moodle.iugaza.edu.ps/`
- `Moodle__LoginTimeoutSeconds=45`
- `Moodle__SessionEncryptionKey` = Base64 encoding of 32 random bytes, generated for example with `openssl rand -base64 32`; `Moodle__SessionEncryptionKeyVersion=1`. Store keys only in Render and back them up securely. For rotation, increment the version and retain old values as `Moodle__PreviousSessionEncryptionKeys__1` until old session rows are removed or re-encrypted.
- `Moodle__ScanIntervalMinutes=10`
- `KeepAlive__Url=https://wawshnibot.onrender.com/health`
- `KeepAlive__IntervalMinutes=10` (optional; clamped to 5–14 minutes)

Render supplies `PORT`; the app binds to `0.0.0.0` on that port. Verify the deployment with `/health` (liveness) and `/health/ready` (database connection). The readiness response never includes a connection string or exception details.

The optional self keep-alive requests the configured public `/health` URL every 10 minutes while the process is running. This may reduce idle spin-down on plans that allow it, but cannot wake an already stopped service or override Render's free-plan suspension policy, maintenance, or restarts; it does not guarantee 24/7 uptime. Render's filesystem is ephemeral; `.env.local` is for local development only. Persistent application data must be stored in the database.

## Telegram commands

Commands: `/link` (also `/connect_moodle`) starts an Arabic student ID/password flow; `/status` checks the saved session against Moodle; `/my_courses` returns course names read from the authenticated Moodle page; `/logout` deletes the session; `/cancel` cancels the current linking flow. These commands are private-chat-only and restricted to `TelegramBot__AuthorizedUserId`.

The password message is immediately passed to Telegram's delete API and the bot clears its local message and credential references as soon as the attempt finishes. If deletion fails, the bot asks for manual deletion. Telegram's delete API only removes a message from the chat and cannot guarantee removal from all Telegram systems or backups. The password is not stored in Postgres or browser storage. The saved Playwright storage state is AES-256-GCM encrypted; ciphertext, nonce, authentication tag, and key version are stored in Postgres. Chromium is launched only for link, status, or course commands and disposed after each operation.

To apply the schema manually in Neon, run `Moodle/001_create_moodle_sessions.sql` in the SQL Editor. This is safe to rerun and does not delete existing data. The app can create the table on first `/link`, `/status`, or `/logout` without a separate migration step.

## Deployment and validation limits

The Docker image uses the Playwright .NET image pinned to the same Playwright version as the NuGet package and installs the .NET 10 ASP.NET runtime. Playwright's official guidance recommends `--init` and shared IPC memory for Chromium; Render's managed Docker service may not expose all Docker run flags. Headless Chromium increases image size and memory use; choose a Render instance with enough memory and monitor restarts. The browser process is short-lived, not kept alive between Telegram updates.

The Moodle SSO site must be tested with the real authorized account. The bot deliberately stops on CAPTCHA/MFA, unexpected identity, unknown page structure, or redirects outside `iugaza.edu.ps`; it never bypasses those checks. No live Moodle credentials were used during implementation, so real authentication and actual course names have not been verified here. Assignment monitoring and notifications remain a later phase.

## Moodle course retrieval diagnostics

`/my_courses` restores the encrypted Playwright storage state into a new browser context and navigates to `Moodle__BaseUrl` + `my/courses.php` (the blank page URL is never used to construct the destination). It waits for visible course links and logs only sanitized URLs, HTTP status, cookie/origin counts, and candidate DOM element counts. Query strings, cookies, storage values, credentials, and page text are not logged. An unknown DOM layout is reported as “courses not detected,” never as an empty enrollment. Run `dotnet test tests/Wawshni.Bot.Tests.csproj` for unit tests. The opt-in real-session test is `dotnet test tests/Integration/Wawshni.Bot.IntegrationTests.csproj`; it requires Neon, the encryption key, authorized Telegram user ID, a saved session, and a matching Playwright browser installation in the test environment.
