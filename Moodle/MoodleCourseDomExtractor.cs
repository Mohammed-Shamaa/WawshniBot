namespace Wawshni.Bot.Moodle;

public static class MoodleCoursePageUrl
{
    public static Uri FromBaseUrl(Uri moodleBaseUrl) => new(moodleBaseUrl, "my/courses.php");
}

/// <summary>Normalizes visible text collected from Moodle course links without treating empty data as an empty enrollment.</summary>
public static class MoodleCourseDomExtractor
{
    private static readonly System.Text.RegularExpressions.Regex CourseLabelPrefix = new(
        @"^اسم\s+المقرر(?=$|[\s:：])(?:\s*[:：]\s*|\s*)",
        System.Text.RegularExpressions.RegexOptions.CultureInvariant | System.Text.RegularExpressions.RegexOptions.Compiled);

    public static IReadOnlyList<string> NormalizeNames(IEnumerable<string> linkTexts)
    {
        var names = new List<string>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var linkText in linkTexts)
        {
            var name = NormalizeWhitespace(linkText);
            while (CourseLabelPrefix.IsMatch(name))
                name = NormalizeWhitespace(CourseLabelPrefix.Replace(name, string.Empty, count: 1));

            if (name.Length > 0 && seen.Add(name))
                names.Add(name);

            if (names.Count == 50) break;
        }

        return names;
    }

    private static string NormalizeWhitespace(string value) =>
        System.Text.RegularExpressions.Regex.Replace(value, @"\s+", " ").Trim().Normalize();
}
