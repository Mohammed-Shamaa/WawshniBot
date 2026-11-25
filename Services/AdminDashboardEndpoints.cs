using System.Security.Claims;
using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.Extensions.Options;
using Wawshni.Bot.Moodle;
using Wawshni.Bot.Security;
using Wawshni.Bot.Users;

namespace Wawshni.Bot.Services;

public static class AdminDashboardEndpoints
{
    private const string RejectedAccessNotice = "هههههههههههههههههههههههههههههههه يا هلفوت فش استخدام للبوت بكفي , روح افتح المودل\nحسابك حالياً غير معتمد لاستخدام وشوشني ارسل /my_access لمعرفة الحالة، وإذا عندك استفسار أو بتعتقد أنه صار خطأ، تواصل مع المهندس محمد: mohammedmomen177@gmail.com";

    public static void MapAdminDashboard(this WebApplication app)
    {
        app.MapGet("/admin", () => Results.Content(AdminDashboardHtml.Content, "text/html; charset=utf-8"));
        app.MapGet("/admin/api/csrf", (HttpContext context, IAntiforgery antiforgery) =>
        {
            var tokens = antiforgery.GetAndStoreTokens(context);
            return Results.Ok(new { requestToken = tokens.RequestToken });
        });
        app.MapPost("/admin/api/login", async (DashboardLoginRequest request, HttpContext context, IConfiguration configuration, IAntiforgery antiforgery) =>
        {
            if (!await HasValidCsrfAsync(context, antiforgery)) return Results.BadRequest(new { error = "csrf" });
            var configuredUser = configuration["Dashboard:Username"];
            var passwordHash = configuration["Dashboard:PasswordHash"];
            if (string.IsNullOrWhiteSpace(configuredUser) || string.IsNullOrWhiteSpace(passwordHash))
                return Results.Problem("لوحة الإدارة غير مهيأة.", statusCode: StatusCodes.Status503ServiceUnavailable);
            if (!string.Equals(request.Username?.Trim(), configuredUser, StringComparison.Ordinal)
                || !DashboardPasswordHasher.Verify(request.Password ?? "", passwordHash))
                return Results.Unauthorized();

            var principal = new ClaimsPrincipal(new ClaimsIdentity(
                [new Claim(ClaimTypes.Name, configuredUser), new Claim(ClaimTypes.Role, "Administrator")],
                CookieAuthenticationDefaults.AuthenticationScheme));
            await context.SignInAsync(CookieAuthenticationDefaults.AuthenticationScheme, principal,
                new AuthenticationProperties { IsPersistent = true, AllowRefresh = true, ExpiresUtc = DateTimeOffset.UtcNow.AddDays(30) });
            return Results.Ok(new { username = configuredUser });
        }).RequireRateLimiting("dashboard-login");

        var api = app.MapGroup("/admin/api").RequireAuthorization();
        api.MapGet("/me", (ClaimsPrincipal user) => Results.Ok(new { username = user.Identity?.Name }));
        api.MapPost("/logout", async (HttpContext context, IAntiforgery antiforgery) =>
        {
            if (!await HasValidCsrfAsync(context, antiforgery)) return Results.BadRequest(new { error = "csrf" });
            await context.SignOutAsync(CookieAuthenticationDefaults.AuthenticationScheme);
            return Results.Ok();
        });
        api.MapGet("/overview", async (TelegramUserRepository users, NeonDatabaseReadiness database, TelegramBotService bot,
            IOptions<GoogleAiOptions> google, IOptions<MoodleOptions> moodle, CancellationToken cancellationToken) =>
        {
            var stats = await users.GetDashboardStatisticsAsync(cancellationToken);
            var trend = await users.GetRegistrationTrendAsync(cancellationToken);
            var ai = google.Value;
            var moodleConfigured = moodle.Value.BaseUrl.IsAbsoluteUri && !string.IsNullOrWhiteSpace(moodle.Value.SessionEncryptionKey);
            return Results.Ok(new
            {
                statistics = stats,
                registrations = trend,
                system = new
                {
                    application = "running",
                    database = await database.CanConnectAsync(cancellationToken) ? "connected" : "unavailable",
                    telegramPolling = bot.PollingActive ? "running" : "stopped",
                    lastTelegramUpdate = bot.LastSuccessfulUpdate,
                    gemini = ai.Enabled && !string.IsNullOrWhiteSpace(ai.ApiKey) ? "configured" : "disabled_or_unconfigured",
                    moodle = moodleConfigured ? "configured" : "incomplete"
                }
            });
        });
        api.MapGet("/users", async (string? search, string? status, bool? blocked, string? sort, int? page, int? pageSize,
            TelegramUserRepository users, CancellationToken cancellationToken) =>
        {
            var safePage = Math.Clamp(page ?? 1, 1, 1_000_000);
            var safeSize = Math.Clamp(pageSize ?? 20, 5, 100);
            var result = await users.GetDashboardUsersAsync(search, status, blocked, sort ?? "newest", safePage, safeSize, cancellationToken);
            return Results.Ok(result);
        });
        api.MapGet("/users/{userId:long}", async (long userId, TelegramUserRepository users, CancellationToken cancellationToken) =>
        {
            var page = await users.GetDashboardUsersAsync(userId.ToString(), null, null, "newest", 1, 100, cancellationToken);
            var found = page.Users.FirstOrDefault(user => user.Registration.TelegramUserId == userId);
            return found is null ? Results.NotFound() : Results.Ok(found);
        });
        api.MapGet("/audit", async (string? action, long? target, DateTimeOffset? from, DateTimeOffset? to, int? page, int? pageSize,
            TelegramUserRepository users, CancellationToken cancellationToken) => Results.Ok(await users.GetAuditLogAsync(
                action, target, from, to, Math.Clamp(page ?? 1, 1, 1_000_000), Math.Clamp(pageSize ?? 50, 5, 100), cancellationToken)));

        api.MapPost("/users/{userId:long}/status", async (long userId, DashboardStatusRequest request, ClaimsPrincipal actor,
            TelegramUserRepository users, TelegramBotService bot, ILoggerFactory loggerFactory, HttpContext context, IAntiforgery antiforgery,
            CancellationToken cancellationToken) =>
        {
            if (!await HasValidCsrfAsync(context, antiforgery)) return Results.BadRequest(new { error = "csrf" });
            if (!Enum.TryParse<TelegramRegistrationStatus>(request.Status, true, out var target)
                || target == TelegramRegistrationStatus.Pending) return Results.BadRequest(new { error = "invalid_status" });
            var current = await users.GetByTelegramIdAsync(userId, cancellationToken);
            if (current is null) return Results.NotFound();
            var allowed = current.Status switch
            {
                TelegramRegistrationStatus.Pending when target is TelegramRegistrationStatus.Approved or TelegramRegistrationStatus.Rejected
                    => new[] { TelegramRegistrationStatus.Pending },
                TelegramRegistrationStatus.Rejected when target == TelegramRegistrationStatus.Approved
                    => new[] { TelegramRegistrationStatus.Rejected },
                _ => []
            };
            if (allowed.Length == 0 || !TelegramRegistrationPolicy.CanTransition(current.Status, target))
                return Results.Conflict(new { error = "transition_not_allowed" });
            var updated = await users.SetStatusAsync(userId, target, null, allowed, cancellationToken,
                SafeReason(request.Reason), actor.Identity?.Name);
            if (updated is null) return Results.Conflict(new { error = "state_changed" });
            var message = target == TelegramRegistrationStatus.Approved
                ? current.Status == TelegramRegistrationStatus.Rejected
                    ? AccessNotificationMessages.Reapproved
                    : "🎉 تمت الموافقة على طلبك. أصبح بإمكانك استخدام وشوشني. أرسل /link لربط Moodle."
                : "تم تحديث حالة حسابك إلى غير معتمد. تواصل مع مسؤول وشوشني إذا كنت ترى أن ذلك غير صحيح.";
            var notificationSent = await NotifySafelyAsync(bot, userId, message, loggerFactory, cancellationToken);
            return Results.Ok(new { user = updated, notificationSent });
        });
        api.MapPost("/users/{userId:long}/revoke", async (long userId, DashboardStatusRequest request, ClaimsPrincipal actor,
            TelegramUserRepository users, TelegramBotService bot, ILoggerFactory loggerFactory, HttpContext context, IAntiforgery antiforgery,
            CancellationToken cancellationToken) =>
        {
            if (!await HasValidCsrfAsync(context, antiforgery)) return Results.BadRequest(new { error = "csrf" });
            var updated = await users.SetStatusAsync(userId, TelegramRegistrationStatus.Rejected, null,
                [TelegramRegistrationStatus.Approved], cancellationToken, SafeReason(request.Reason), actor.Identity?.Name);
            if (updated is null) return Results.Conflict(new { error = "user_missing_or_not_approved" });
            var notificationSent = await NotifySafelyAsync(bot, userId,
                RejectedAccessNotice,
                loggerFactory, cancellationToken);
            return Results.Ok(new { user = updated, notificationSent });
        });
        api.MapPost("/users/{userId:long}/block", async (long userId, DashboardBlockRequest request, ClaimsPrincipal actor,
            TelegramUserRepository users, TelegramBotService bot, ILoggerFactory loggerFactory, HttpContext context, IAntiforgery antiforgery,
            CancellationToken cancellationToken) =>
        {
            if (!await HasValidCsrfAsync(context, antiforgery)) return Results.BadRequest(new { error = "csrf" });
            var updated = await users.SetBlockedAsync(userId, true, actor.Identity?.Name ?? "dashboard-admin", SafeReason(request.Reason), cancellationToken);
            if (updated is null) return Results.Conflict(new { error = "user_missing_or_already_blocked" });
            var notificationSent = await NotifySafelyAsync(bot, userId, "تم تعليق إمكانية استخدامك للبوت. تواصل مع مسؤول وشوشني إذا كنت تعتقد أن هذا خطأ.", loggerFactory, cancellationToken);
            return Results.Ok(new { user = updated, notificationSent });
        });
        api.MapPost("/users/{userId:long}/unblock", async (long userId, ClaimsPrincipal actor,
            TelegramUserRepository users, TelegramBotService bot, ILoggerFactory loggerFactory, HttpContext context, IAntiforgery antiforgery,
            CancellationToken cancellationToken) =>
        {
            if (!await HasValidCsrfAsync(context, antiforgery)) return Results.BadRequest(new { error = "csrf" });
            var updated = await users.SetBlockedAsync(userId, false, actor.Identity?.Name ?? "dashboard-admin", null, cancellationToken);
            if (updated is null) return Results.Conflict(new { error = "user_missing_or_not_blocked" });
            var notify = updated.Status == TelegramRegistrationStatus.Approved
                ? "تمت استعادة إمكانية استخدامك للبوت. أهلاً بعودتك إلى وشوشني."
                : "تم رفع الحظر، لكن حالة طلبك ما زالت " + ArabicStatus(updated.Status) + ".";
            var notificationSent = await NotifySafelyAsync(bot, userId, notify, loggerFactory, cancellationToken);
            return Results.Ok(new { user = updated, notificationSent });
        });
    }

    private static async Task<bool> HasValidCsrfAsync(HttpContext context, IAntiforgery antiforgery)
    {
        try { await antiforgery.ValidateRequestAsync(context); return true; }
        catch (AntiforgeryValidationException) { return false; }
    }

    private static string? SafeReason(string? reason)
    {
        if (string.IsNullOrWhiteSpace(reason)) return null;
        var value = reason.Trim();
        return value.Length <= 500 ? value : value[..500];
    }

    private static string ArabicStatus(TelegramRegistrationStatus status) => status switch
    {
        TelegramRegistrationStatus.Approved => "معتمد",
        TelegramRegistrationStatus.Pending => "قيد المراجعة",
        _ => "مرفوض"
    };

    private static async Task<bool> NotifySafelyAsync(TelegramBotService bot, long userId, string text, ILoggerFactory loggerFactory, CancellationToken cancellationToken)
    {
        try
        {
            var sent = await bot.TrySendMessageAsync(userId, text, cancellationToken);
            if (!sent)
                loggerFactory.CreateLogger("AdminDashboard").LogWarning("Telegram notification could not be delivered for user {TelegramUserId}; database state remains saved.", userId);
            return sent;
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            loggerFactory.CreateLogger("AdminDashboard").LogWarning("Telegram notification failed ({ExceptionType}); database state remains saved.", exception.GetType().Name);
            return false;
        }
    }

    public sealed record DashboardLoginRequest(string? Username, string? Password);
    public sealed record DashboardStatusRequest(string? Status, string? Reason);
    public sealed record DashboardBlockRequest(string? Reason);
}
