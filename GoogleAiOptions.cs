namespace Wawshni.Bot;

public sealed class GoogleAiOptions
{
    public const string SectionName = "GoogleAI";
    public bool Enabled { get; set; }
    public string ApiKey { get; set; } = "";
    public string GeminiModel { get; set; } = "gemini-3.8-flash";
}
