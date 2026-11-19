# WawshniBot

Arabic-first Telegram assistant built on .NET 10. ASP.NET Core, Telegram long polling, and Neon PostgreSQL connectivity run in one process. Moodle access and monitoring are not implemented; the bot does not fabricate course data.

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

The .NET app accepts either `ConnectionStrings__DefaultConnection` or `DATABASE_URL`. It uses Npgsql's pooled `NpgsqlDataSource`, requires TLS for PostgreSQL URLs, and runs only a read-only `SELECT 1` check at `/health/ready`. The app currently has no schema, EF Core entities, or migrations, and does not write to the database.

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
- `Moodle__ScanIntervalMinutes=10`

Render supplies `PORT`; the app binds to `0.0.0.0` on that port. Verify the deployment with `/health` (liveness) and `/health/ready` (database connection). The readiness response never includes a connection string or exception details.

An external health monitor may request `/health` every 10 minutes to reduce idle spin-down. This cannot prevent Render restarts or guarantee 24/7 uptime. Render's filesystem is ephemeral; `.env.local` is for local development only. Persistent application data must be stored in the database.

## Telegram commands

Commands include `/start`, `/help`, `/courses`, `/updates`, `/quizzes`, `/assignments`, `/report`, `/connect_moodle`, `/my_courses`, `/my_assignments`, `/my_quizzes`, `/upcoming`, `/latest`, `/scan`, and `/status`. Moodle-specific commands report that integration is unavailable and do not return invented data.

## Limitations

The IUG Moodle SSO login, course discovery, baseline/change tracking, scheduled scans, notifications, database schema, and migrations remain unimplemented. No Playwright/Chromium dependency is included. Neon connectivity has been verified from the local app, but Render deployment and Telegram connectivity still require their own environment configuration and deployment check.
