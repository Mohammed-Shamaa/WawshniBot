using Wawshni.Bot;
using Wawshni.Bot.Handlers;
using Wawshni.Bot.Security;
using Wawshni.Bot.Services;
using Wawshni.Bot.Moodle;

var builder = WebApplication.CreateBuilder(args);
var port = Environment.GetEnvironmentVariable("PORT");
if (int.TryParse(port, out var renderPort) && renderPort is > 0 and <= 65535)
    builder.WebHost.UseUrls($"http://0.0.0.0:{renderPort}");
builder.Services.AddOptions<TelegramOptions>()
    .Bind(builder.Configuration.GetSection(TelegramOptions.SectionName));
builder.Services.AddOptions<MoodleOptions>()
    .Bind(builder.Configuration.GetSection(MoodleOptions.SectionName))
    .Validate(options => options.BaseUrl.IsAbsoluteUri && options.BaseUrl.Scheme == Uri.UriSchemeHttps, "Moodle:BaseUrl must be an absolute HTTPS URL.")
    .Validate(options => options.ScanIntervalMinutes >= 1, "Moodle:ScanIntervalMinutes must be at least one minute.")
    .ValidateOnStart();
builder.Services.AddSingleton<TelegramUserAuthorization>();
builder.Services.AddSingleton<MoodleConnectionStatus>();
builder.Services.AddSingleton<MessageHandler>();
builder.Services.AddSingleton<TelegramBotService>();
builder.Services.AddHostedService<Worker>();

var app = builder.Build();
app.MapGet("/health", () => Results.Json(new { status = "ok" }));
await app.RunAsync();

namespace Wawshni.Bot;
public sealed class TelegramOptions
{
    public const string SectionName = "TelegramBot";
    public string Token { get; set; } = string.Empty;
    public long? AuthorizedUserId { get; set; }
}
