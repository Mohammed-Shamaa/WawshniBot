using System.Globalization;
using System.Text.RegularExpressions;

namespace Wawshni.Bot.Moodle;

public enum MoodleAssignmentsResultKind { Success, Partial, Expired, SessionRestoreFailed, Unavailable, ExtractionFailed }
public enum MoodleAssignmentStatus { NotSubmitted, Draft, Submitted, SubmittedLate, NeedsRevision, Graded, Unavailable }
public enum MoodleDateKnowledge { Known, NoDeadline, Unknown }
public sealed record MoodleAssignment(
    string Title,
    string Course,
    string Url,
    string? Description,
    DateTimeOffset? DueAt,
    MoodleDateKnowledge DueDateKnowledge,
    DateTimeOffset? AvailableFrom,
    DateTimeOffset? CutoffAt,
    MoodleAssignmentStatus Status,
    DateTimeOffset? SubmittedAt);
public sealed record MoodleAssignmentLink(string Href, string Title);
public sealed record MoodleAssignmentsResult(MoodleAssignmentsResultKind Kind, IReadOnlyList<MoodleAssignment> Assignments);

public sealed class MoodleAssignmentPageSnapshot
{
    public string Title { get; set; } = "";
    public string Body { get; set; } = "";
    public string Description { get; set; } = "";
    public List<MoodleAssignmentPageTime> TimeValues { get; set; } = [];
    public List<MoodleAssignmentPageRow> Rows { get; set; } = [];
}

public sealed class MoodleAssignmentPageTime { public string Label { get; set; } = ""; public string? Value { get; set; } }
public sealed class MoodleAssignmentPageRow { public string Label { get; set; } = ""; public string Value { get; set; } = ""; public string? Iso { get; set; } }

/// <summary>Maps authenticated Moodle page data to user-facing assignment records.</summary>
public static class MoodleAssignmentExtractor
{
    public static MoodleAssignmentPageSnapshot ParsePageSnapshot(string json) =>
        System.Text.Json.JsonSerializer.Deserialize<MoodleAssignmentPageSnapshot>(
            json, new System.Text.Json.JsonSerializerOptions(System.Text.Json.JsonSerializerDefaults.Web))
        ?? throw new System.Text.Json.JsonException("Moodle assignment page returned an empty DOM snapshot.");

    public static string NormalizeTitle(string? title) => string.IsNullOrWhiteSpace(title) ? string.Empty : Regex.Replace(title, @"\s+", " ").Trim();

    public static MoodleAssignmentStatus MapStatus(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return MoodleAssignmentStatus.Unavailable;
        var value = Normalize(text);
        if (Has(value, "graded", "تم التقييم", "تم تقييم", "مقيّم", "مقيم")) return MoodleAssignmentStatus.Graded;
        if (Has(value, "needs revision", "needs changes", "requires changes", "يحتاج إلى تعديل", "يحتاج تعديل", "بحاجة إلى تعديل")) return MoodleAssignmentStatus.NeedsRevision;
        if (Has(value, "draft (not submitted)", "draft", "مسودة", "لم يتم الإرسال")) return MoodleAssignmentStatus.Draft;
        if (Has(value, "nothing submitted", "not submitted", "لا يوجد تسليم", "لم يتم التسليم", "لم تسلم")) return MoodleAssignmentStatus.NotSubmitted;
        if (Has(value, "submitted late", "late submission", "تم التسليم متأخراً", "تم التسليم متأخرا", "تسليم متأخر")) return MoodleAssignmentStatus.SubmittedLate;
        if (Has(value, "submitted for grading", "submitted", "تم التسليم", "تم الإرسال")) return MoodleAssignmentStatus.Submitted;
        return MoodleAssignmentStatus.Unavailable;
    }

    public static (DateTimeOffset? Value, MoodleDateKnowledge Knowledge) ParseDueDate(string? text, string? isoValue = null)
    {
        var source = string.IsNullOrWhiteSpace(isoValue) ? text : isoValue;
        if (TryDate(source, out var date)) return (date, MoodleDateKnowledge.Known);
        if (!string.IsNullOrWhiteSpace(text) && Has(Normalize(text), "no due date", "no deadline", "بدون موعد نهائي", "لا يوجد موعد نهائي", "لا يوجد تاريخ استحقاق"))
            return (null, MoodleDateKnowledge.NoDeadline);
        return (null, MoodleDateKnowledge.Unknown);
    }

    public static DateTimeOffset? ParseDate(string? text, string? isoValue = null)
    {
        var source = string.IsNullOrWhiteSpace(isoValue) ? text : isoValue;
        return TryDate(source, out var value) ? value : null;
    }

    public static IReadOnlyList<MoodleAssignment> Deduplicate(IEnumerable<MoodleAssignment> assignments) => assignments
        .Where(a => !string.IsNullOrWhiteSpace(a.Title) && Uri.TryCreate(a.Url, UriKind.Absolute, out _))
        .GroupBy(a => ActivityKey(a.Url), StringComparer.OrdinalIgnoreCase)
        .Select(group => group.First())
        .ToList();

    public static MoodleAssignmentsResult CreateResult(IEnumerable<MoodleAssignment> assignments, int failureCount, bool recognizedCoursePages)
    {
        var items = Deduplicate(assignments);
        if (!recognizedCoursePages && items.Count == 0)
            return new(MoodleAssignmentsResultKind.ExtractionFailed, []);
        return failureCount > 0
            ? new(MoodleAssignmentsResultKind.Partial, items)
            : new(MoodleAssignmentsResultKind.Success, items);
    }

    public static string ActivityKey(string value) => CanonicalUrl(value);

    public static IReadOnlyList<MoodleAssignmentLink> NormalizeLinks(IEnumerable<MoodleAssignmentLink> links, Uri moodleBaseUrl)
    {
        var normalized = new Dictionary<string, MoodleAssignmentLink>(StringComparer.OrdinalIgnoreCase);
        foreach (var link in links)
        {
            if (!Uri.TryCreate(link.Href, UriKind.Absolute, out var uri)
                || !uri.Scheme.Equals(moodleBaseUrl.Scheme, StringComparison.OrdinalIgnoreCase)
                || !uri.Host.Equals(moodleBaseUrl.Host, StringComparison.OrdinalIgnoreCase)
                || !uri.AbsolutePath.Contains("/mod/assign/view.php", StringComparison.OrdinalIgnoreCase)
                || !int.TryParse(System.Web.HttpUtility.ParseQueryString(uri.Query).Get("id"), out _)) continue;

            var key = ActivityKey(uri.ToString());
            var title = NormalizeTitle(link.Title);
            if (!normalized.ContainsKey(key) || (normalized[key].Title.Length == 0 && title.Length > 0))
                normalized[key] = new MoodleAssignmentLink(uri.ToString(), title);
        }
        return normalized.Values.ToList();
    }

    public static IReadOnlyList<MoodleAssignment> Sort(IReadOnlyList<MoodleAssignment> assignments, DateTimeOffset now) => assignments
        .OrderBy(a => a.DueDateKnowledge == MoodleDateKnowledge.Known && a.DueAt < now ? 0 : 1)
        .ThenBy(a => a.DueDateKnowledge == MoodleDateKnowledge.Known && a.DueAt >= now ? 0 : 1)
        .ThenBy(a => a.DueAt ?? DateTimeOffset.MaxValue)
        .ThenBy(a => a.Course, StringComparer.CurrentCulture)
        .ToList();

    public static bool IsOverdue(MoodleAssignment assignment, DateTimeOffset now) =>
        assignment.DueDateKnowledge == MoodleDateKnowledge.Known && assignment.DueAt < now &&
        assignment.Status is MoodleAssignmentStatus.NotSubmitted or MoodleAssignmentStatus.Draft or MoodleAssignmentStatus.Unavailable;

    private static bool TryDate(string? input, out DateTimeOffset date)
    {
        input = input?.Trim();
        if (!string.IsNullOrEmpty(input) && DateTimeOffset.TryParse(input, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out date)) return true;
        date = default;
        return false;
    }

    private static string CanonicalUrl(string value)
    {
        var uri = new Uri(value);
        var query = System.Web.HttpUtility.ParseQueryString(uri.Query);
        var id = query.Get("id");
        var builder = new UriBuilder(uri) { Query = string.IsNullOrWhiteSpace(id) ? string.Empty : "id=" + Uri.EscapeDataString(id), Fragment = string.Empty };
        return builder.Uri.AbsoluteUri.TrimEnd('/');
    }

    private static string Normalize(string value) => Regex.Replace(value, @"\s+", " ").Trim().ToLowerInvariant();
    private static bool Has(string value, params string[] options) => options.Any(option => value.Contains(option, StringComparison.OrdinalIgnoreCase));
}
