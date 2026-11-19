using Microsoft.Extensions.Options;

namespace Wawshni.Bot.Moodle;

public sealed class MoodleOptions
{
    public const string SectionName = "Moodle";
    public Uri BaseUrl { get; set; } = new("https://moodle.iugaza.edu.ps/");
    public int ScanIntervalMinutes { get; set; } = 10;
    public int LoginTimeoutSeconds { get; set; } = 45;
    public string? SessionEncryptionKey { get; set; }
    public short SessionEncryptionKeyVersion { get; set; } = 1;
    public Dictionary<string, string> PreviousSessionEncryptionKeys { get; set; } = [];
}

/// <summary>
/// Legacy read-only Moodle configuration status. Session validation is handled by MoodleBrowserService.
/// </summary>
public sealed class MoodleConnectionStatus(IOptions<MoodleOptions> options)
{
    public Uri BaseUrl => options.Value.BaseUrl;
    public bool IsConnected => false;
    public DateTimeOffset? LastSuccessfulScan => null;
    public int? CourseCount => null;
}
