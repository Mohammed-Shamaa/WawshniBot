namespace Wawshni.Bot.Moodle;

public static class MoodleCoursePageUrl
{
    public static Uri FromBaseUrl(Uri moodleBaseUrl) => new(moodleBaseUrl, "my/courses.php");
}

/// <summary>Normalizes visible text collected from Moodle course links without treating empty data as an empty enrollment.</summary>
public static class MoodleCourseDomExtractor
{
    public static IReadOnlyList<string> NormalizeNames(IEnumerable<string> linkTexts) => linkTexts
        .Select(x => System.Text.RegularExpressions.Regex.Replace(x, @"\s+", " ").Trim())
        .Where(x => x.Length > 0)
        .Distinct(StringComparer.Ordinal)
        .Take(50)
        .ToArray();
}
