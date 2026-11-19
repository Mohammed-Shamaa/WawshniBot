using Microsoft.Extensions.Options;

namespace Wawshni.Bot.Moodle;

public sealed class MoodleOptions
{
    public const string SectionName = "Moodle";
    public Uri BaseUrl { get; set; } = new("https://moodle.iugaza.edu.ps/");
    public int ScanIntervalMinutes { get; set; } = 10;
}

/// <summary>
/// Reports integration readiness without implying that a Moodle session or API exists.
/// Authentication is intentionally unavailable until a secure, supported flow is implemented.
/// </summary>
public sealed class MoodleConnectionStatus(IOptions<MoodleOptions> options)
{
    public Uri BaseUrl => options.Value.BaseUrl;
    public bool IsConnected => false;
    public DateTimeOffset? LastSuccessfulScan => null;
    public int? CourseCount => null;
}
