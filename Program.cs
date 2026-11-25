using Wawshni.Bot;
using Wawshni.Bot.Handlers;
using Wawshni.Bot.Security;
using Wawshni.Bot.Services;
using Wawshni.Bot.Moodle;
using Wawshni.Bot.Users;
using Microsoft.Extensions.Options;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.DataProtection.KeyManagement;
using Microsoft.AspNetCore.DataProtection.Repositories;
using Microsoft.AspNetCore.DataProtection.XmlEncryption;
using System.Threading.RateLimiting;
using System.Text.Json.Serialization;

var builder = WebApplication.CreateBuilder(args);
var port = Environment.GetEnvironmentVariable("PORT");
if (int.TryParse(port, out var renderPort) && renderPort is > 0 and <= 65535)
    builder.WebHost.UseUrls($"http://0.0.0.0:{renderPort}");
builder.Services.AddOptions<TelegramOptions>()
    .Bind(builder.Configuration.GetSection(TelegramOptions.SectionName));
builder.Services.AddOptions<MoodleOptions>()
    .Bind(builder.Configuration.GetSection(MoodleOptions.SectionName))
    .Validate(options => options.BaseUrl.IsAbsoluteUri && options.BaseUrl.Scheme == Uri.UriSchemeHttps, "Moodle:BaseUrl must be an absolute HTTPS URL.")
    .Validate(options => options.LoginTimeoutSeconds is >= 15 and <= 120, "Moodle:LoginTimeoutSeconds must be between 15 and 120 seconds.")
    .Validate(options => options.ScanIntervalMinutes >= 1, "Moodle:ScanIntervalMinutes must be at least one minute.")
    .ValidateOnStart();
builder.Services.AddOptions<GoogleAiOptions>()
    .Bind(builder.Configuration.GetSection(GoogleAiOptions.SectionName));
builder.Services.ConfigureHttpJsonOptions(options => options.SerializerOptions.Converters.Add(new JsonStringEnumConverter()));
builder.Services.AddDataProtection().SetApplicationName("WawshniBot");
if (!builder.Environment.IsDevelopment())
{
    builder.Services.AddSingleton<NeonDataProtectionXmlRepository>();
    builder.Services.AddSingleton<IXmlRepository>(services => services.GetRequiredService<NeonDataProtectionXmlRepository>());
    builder.Services.AddSingleton<IXmlEncryptor, NeonDataProtectionXmlEncryptor>();
    builder.Services.AddOptions<KeyManagementOptions>()
        .Configure<IXmlRepository, IXmlEncryptor>((options, repository, encryptor) =>
        {
            options.XmlRepository = repository;
            options.XmlEncryptor = encryptor;
        });
}
builder.Services.AddAuthentication(CookieAuthenticationDefaults.AuthenticationScheme).AddCookie(options =>
{
    options.Cookie.Name = "Wawshni.Admin";
    options.Cookie.HttpOnly = true;
    options.Cookie.SecurePolicy = CookieSecurePolicy.Always;
    options.Cookie.SameSite = SameSiteMode.Strict;
    options.SlidingExpiration = true;
    options.ExpireTimeSpan = TimeSpan.FromDays(30);
    options.Events.OnRedirectToLogin = context =>
    {
        if (context.Request.Path.StartsWithSegments("/admin/api")) { context.Response.StatusCode = StatusCodes.Status401Unauthorized; return Task.CompletedTask; }
        context.Response.Redirect(context.RedirectUri);
        return Task.CompletedTask;
    };
    options.Events.OnRedirectToAccessDenied = context => { context.Response.StatusCode = StatusCodes.Status403Forbidden; return Task.CompletedTask; };
});
builder.Services.AddAuthorization();
builder.Services.AddAntiforgery(options =>
{
    options.HeaderName = "X-CSRF-TOKEN";
    options.Cookie.Name = "Wawshni.Antiforgery";
    options.Cookie.HttpOnly = true;
    options.Cookie.SecurePolicy = CookieSecurePolicy.Always;
    options.Cookie.SameSite = SameSiteMode.Strict;
});
builder.Services.AddRateLimiter(options =>
{
    options.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
    options.AddPolicy("dashboard-login", context => RateLimitPartition.GetFixedWindowLimiter(
        context.Connection.RemoteIpAddress?.ToString() ?? "unknown", _ => new FixedWindowRateLimiterOptions
    {
        PermitLimit = 5,
        Window = TimeSpan.FromMinutes(10),
        QueueLimit = 0,
        AutoReplenishment = true
    }));
});
builder.Services.AddHttpClient<GeminiChatService>(client => client.Timeout = TimeSpan.FromSeconds(45));
builder.Services.AddSingleton<TelegramUserAuthorization>();
builder.Services.AddSingleton<TelegramUserRepository>();
builder.Services.AddSingleton<MoodleConnectionStatus>();
builder.Services.AddSingleton<MoodleSessionCrypto>();
builder.Services.AddSingleton<MoodleSessionRepository>();
builder.Services.AddSingleton<MoodleBrowserService>();
builder.Services.AddSingleton<MessageHandler>();
builder.Services.AddSingleton<TelegramBotService>();
builder.Services.AddSingleton<NeonDatabaseReadiness>();
builder.Services.AddHostedService<Worker>();
builder.Services.AddHostedService<SelfKeepAliveService>();

var app = builder.Build();
var telegramOptions = app.Services.GetRequiredService<IOptions<TelegramOptions>>().Value;
if (telegramOptions.AuthorizedUserId is not long administratorId)
    throw new InvalidOperationException("TelegramBot:AuthorizedUserId must be configured with the administrator's numeric Telegram user ID.");
await app.Services.GetRequiredService<TelegramUserRepository>()
    .EnsureSchemaAsync(administratorId, app.Lifetime.ApplicationStopping);
await app.Services.GetRequiredService<MoodleSessionRepository>()
    .EnsureSchemaAsync(app.Lifetime.ApplicationStopping);
if (!app.Environment.IsDevelopment())
{
    await app.Services.GetRequiredService<NeonDataProtectionXmlRepository>()
        .EnsureSchemaAsync(app.Lifetime.ApplicationStopping);
    _ = app.Services.GetRequiredService<IXmlEncryptor>(); // Fail startup if the persistent key-encryption secret is missing or invalid.
    var forwardedHeaders = new ForwardedHeadersOptions
    {
        ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto,
        ForwardLimit = 1
    };
    // Render's ingress proxy addresses are dynamic; the service is reachable through that proxy.
    forwardedHeaders.KnownIPNetworks.Clear();
    forwardedHeaders.KnownProxies.Clear();
    app.UseForwardedHeaders(forwardedHeaders);
}
app.UseRouting();
app.UseRateLimiter();
app.UseAuthentication();
app.UseAuthorization();
app.Use(async (context, next) =>
{
    if (context.Request.Path.StartsWithSegments("/admin"))
    {
        context.Response.Headers["X-Content-Type-Options"] = "nosniff";
        context.Response.Headers["X-Frame-Options"] = "DENY";
        context.Response.Headers["Referrer-Policy"] = "no-referrer";
        context.Response.Headers["Permissions-Policy"] = "camera=(), microphone=(), geolocation=()";
        context.Response.Headers.CacheControl = "no-store";
    }
    await next();
});
app.MapGet("/health", () => Results.Json(new { status = "ok" }));
app.MapGet("/health/ready", async (NeonDatabaseReadiness database, CancellationToken cancellationToken) =>
    await database.CanConnectAsync(cancellationToken)
        ? Results.Json(new { status = "ready", postgres = "ok" })
        : Results.Json(new { status = "not_ready" }, statusCode: StatusCodes.Status503ServiceUnavailable));
app.MapAdminDashboard();
await app.RunAsync();
