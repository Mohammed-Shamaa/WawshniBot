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

    /// <summary>Retains validated course URLs even when a theme renders the link without visible text.</summary>
    public static IReadOnlyList<MoodleCourseLink> NormalizeLinks(IEnumerable<MoodleCourseLink> links, Uri moodleBaseUrl)
    {
        var normalized = new Dictionary<string, MoodleCourseLink>(StringComparer.OrdinalIgnoreCase);
        foreach (var link in links)
        {
            if (!Uri.TryCreate(link.Href, UriKind.Absolute, out var uri)
                || !uri.Scheme.Equals(moodleBaseUrl.Scheme, StringComparison.OrdinalIgnoreCase)
                || !uri.Host.Equals(moodleBaseUrl.Host, StringComparison.OrdinalIgnoreCase)
                || !uri.AbsolutePath.Contains("/course/view.php", StringComparison.OrdinalIgnoreCase)) continue;

            var id = System.Web.HttpUtility.ParseQueryString(uri.Query).Get("id");
            if (!int.TryParse(id, out var courseId)) continue;
            var key = courseId.ToString(System.Globalization.CultureInfo.InvariantCulture);
            var name = NormalizeNames([link.Name]).FirstOrDefault() ?? string.Empty;
            if (!normalized.ContainsKey(key) || (normalized[key].Name.Length == 0 && name.Length > 0))
                normalized[key] = new MoodleCourseLink(name, uri.ToString());
        }
        return normalized.Values.ToList();
    }

    private static string NormalizeWhitespace(string value) =>
        System.Text.RegularExpressions.Regex.Replace(value, @"\s+", " ").Trim().Normalize();
}

public sealed record MoodleCourseLink(string Name, string Href);
