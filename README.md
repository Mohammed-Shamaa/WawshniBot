# WawshniBot

Arabic-first Telegram assistant built on .NET 10. The current implementation runs an ASP.NET Core Web Service and Telegram polling in one process. Moodle connectivity and persistence are not implemented; no Moodle records or activity data are fabricated.

## Current state

- `/health` returns a small liveness response and does not query external services.
- Telegram uses long polling through a hosted service when `TelegramBot__Token` is configured.
- User authorization uses `TelegramBot__AuthorizedUserId`.
- Moodle commands report that integration is unavailable.
- PostgreSQL/Neon configuration is documented for a future persistence stage; EF Core, Npgsql, entities and migrations are not present.

## Local setup

Prerequisite: .NET 10 SDK. From this directory:

```powershell
dotnet restore
dotnet build
$env:TelegramBot__Token = "YOUR_ROTATED_TOKEN"
$env:TelegramBot__AuthorizedUserId = "YOUR_TELEGRAM_USER_ID"
dotnet run
```

The app uses `PORT` when provided; otherwise ASP.NET Core uses local defaults. Never commit `.env` files or credentials. Rotate any bot token shared in chat and use the replacement only in local secrets or deployment environment settings.

## Neon PostgreSQL (planned)

Create a PostgreSQL project in Neon and copy its pooled connection string from the Neon dashboard. Configure it as `ConnectionStrings__DefaultConnection` in the deployment environment, with TLS enabled (Neon connection strings normally include `sslmode=require`). Do not commit the connection string.

Database persistence has not been implemented, so no migrations exist yet. After EF Core/Npgsql and migrations are added, apply them with:

```powershell
dotnet ef database update --project Wawshni.Bot.csproj
```

Run that command only after configuring a valid connection string. No schema changes or production database operations have been performed.

## Render Web Service

1. Push the repository to GitHub yourself, then create a **Web Service** in Render from that repository.
2. Set the root directory to `WawshniAssistant/Wawshni.Bot` and choose Docker deployment.
3. Add environment variables:
   - `ASPNETCORE_ENVIRONMENT=Production`
   - `TelegramBot__Token` = a rotated Telegram token
   - `TelegramBot__AuthorizedUserId` = your numeric Telegram user ID
   - `ConnectionStrings__DefaultConnection` = Neon PostgreSQL URL (for when persistence is implemented)
   - `Moodle__BaseUrl=https://moodle.iugaza.edu.ps/`
   - `Moodle__ScanIntervalMinutes=10`
4. Deploy and check `https://YOUR-SERVICE.onrender.com/health`; expected JSON: `{"status":"ok"}`.

Render supplies `PORT`; the app binds to `0.0.0.0` on that port. Docker provides port 10000 as a local fallback. Hosted services run in the web process; no separate Render Background Worker is needed.

An external health monitor may request `/health` every 10 minutes to reduce idle spin-down. This does not guarantee 24/7 uptime and cannot prevent restarts, maintenance, or free-tier interruptions. Render's filesystem is ephemeral; durable state must live in PostgreSQL. Neon free-tier limits and availability may change.

## Telegram commands

Supported commands include `/start`, `/help`, `/courses`, `/updates`, `/quizzes`, `/assignments`, `/report`, `/connect_moodle`, `/my_courses`, `/my_assignments`, `/my_quizzes`, `/upcoming`, `/latest`, `/scan`, and `/status`. Moodle commands currently return unavailable status; they do not authenticate or scan.

## Moodle and deployment limitations

The university site uses SSO and no authorized Moodle REST token has been confirmed. Moodle authentication, course discovery, baseline/change tracking, scheduled scans, notifications, PostgreSQL schema and migration are not implemented or tested. Browser session state must not be kept on Render's ephemeral disk; use a supported API or a carefully protected external secret/session store after university-approved authentication. Playwright/Chromium has not been added, avoiding its extra image size and memory demand on a free instance.

Build and Docker deployment have not been verified because NuGet access is unavailable in this environment. `/health` is a liveness endpoint only and intentionally does not prove database, Telegram, or Moodle readiness.
