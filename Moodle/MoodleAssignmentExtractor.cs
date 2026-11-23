using System.Globalization;
using System.Text.RegularExpressions;

namespace Wawshni.Bot.Moodle;

public enum MoodleAssignmentsResultKind { Success, Partial, Expired, SessionRestoreFailed, Unavailable, ExtractionFailed }
public enum MoodleAssignmentStatus { NotSubmitted, NotRequired, Draft, Submitted, SubmittedLate, NeedsRevision, Graded, Unavailable }
public enum MoodleGradingStatus { NotGraded, Graded, NeedsChanges, Unavailable }
public enum MoodleSubmissionAcceptance { Accepted, NotAccepted, Unknown }
public enum MoodleDateKnowledge { Known, NoDeadline, Unknown, ExtractionFailed }
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
    DateTimeOffset? SubmittedAt,
    MoodleGradingStatus GradingStatus = MoodleGradingStatus.Unavailable,
    MoodleSubmissionAcceptance SubmissionAcceptance = MoodleSubmissionAcceptance.Unknown);
public sealed record MoodleAssignmentLink(string Href, string Title);
public sealed record MoodleAssignmentRetrievalSummary(
    int TotalCourses,
    bool CourseListComplete,
    int CoursesInspected,
    int CoursesWithoutAssignments,
    int CoursesFailed,
    int AssignmentsDiscovered,
    int AssignmentDetailsInspected,
    int AssignmentDetailsFailed);
public sealed record MoodleAssignmentsResult(
    MoodleAssignmentsResultKind Kind,
    IReadOnlyList<MoodleAssignment> Assignments,
    MoodleAssignmentRetrievalSummary? Summary = null);

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
        if (Has(value, "no online submissions required", "does not require you to submit anything online", "offline", "لا يتطلب تسليماً", "لا يتطلب تسليم")) return MoodleAssignmentStatus.NotRequired;
        if (Has(value, "draft (not submitted)", "draft", "مسودة", "لم يتم الإرسال")) return MoodleAssignmentStatus.Draft;
        if (Has(value, "nothing submitted", "no submission", "no attempt", "not submitted", "لا يوجد تسليم", "لم يتم التسليم", "لم تسلم", "لم يتم تقديم")) return MoodleAssignmentStatus.NotSubmitted;
        if (Has(value, "submitted", "تم التسليم", "تم الإرسال", "تم التقديم") && Has(value, "late", "متأخر", "متأخرا")) return MoodleAssignmentStatus.SubmittedLate;
        if (Has(value, "submitted late", "late submission", "تم التسليم متأخراً", "تم التسليم متأخرا", "تسليم متأخر", "بعد الموعد", "متأخر")) return MoodleAssignmentStatus.SubmittedLate;
        if (Has(value, "submitted for grading", "submitted", "تم التسليم", "تم الإرسال")) return MoodleAssignmentStatus.Submitted;
        return MoodleAssignmentStatus.Unavailable;
    }

    public static MoodleGradingStatus MapGradingStatus(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return MoodleGradingStatus.Unavailable;
        var value = Normalize(text);
        if (Has(value, "needs revision", "needs changes", "requires changes", "يحتاج إلى تعديل", "يحتاج تعديل", "بحاجة إلى تعديل")) return MoodleGradingStatus.NeedsChanges;
        if (Has(value, "not graded", "not yet graded", "لم يتم التقييم", "لم يتم التصحيح", "غير مصحح", "بانتظار التقييم")) return MoodleGradingStatus.NotGraded;
        if (Has(value, "graded", "تم التقييم", "تم تقييم", "تم التصحيح", "مقيّم", "مقيم", "مصحح")) return MoodleGradingStatus.Graded;
        return MoodleGradingStatus.Unavailable;
    }

    public static MoodleAssignment? ParseAssignmentPage(MoodleAssignmentPageSnapshot snapshot, string url, string course)
    {
        var title = NormalizeTitle(snapshot.Title);
        if (title.Length == 0) return null;

        var dueRow = FindRow(snapshot.Rows, "due date", "due", "موعد التسليم", "تاريخ التسليم", "تاريخ الاستحقاق", "آخر موعد للتسليم");
        var dueTime = FindTime(snapshot.TimeValues, "due date", "due", "موعد التسليم", "تاريخ التسليم", "تاريخ الاستحقاق", "آخر موعد للتسليم");
        var dueText = dueRow?.Value ?? dueTime?.Label;
        var dueIso = dueRow?.Iso ?? dueTime?.Value;
        var (dueAt, dueKnowledge) = ParseDueDate(dueText, dueIso);
        if (dueKnowledge == MoodleDateKnowledge.Unknown && (dueRow is not null || dueTime is not null))
            dueKnowledge = MoodleDateKnowledge.ExtractionFailed;

        var submittedRow = FindRow(snapshot.Rows,
            "last modified (submission)", "last modified", "submission date", "submitted on",
            "آخر تعديل للتسليم", "آخر تعديل", "وقت التسليم", "تاريخ ووقت التسليم");
        var submittedTime = FindTime(snapshot.TimeValues,
            "last modified", "submission date", "submitted on", "آخر تعديل", "وقت التسليم", "تاريخ ووقت التسليم");
        var submittedAt = ParseDate(submittedRow?.Value ?? submittedTime?.Label, submittedRow?.Iso ?? submittedTime?.Value);

        var submissionRow = FindRow(snapshot.Rows, "submission status", "حالة التسليم", "حالة الإرسال", "حالة التقديم");
        var remainingRow = FindRow(snapshot.Rows, "time remaining", "الوقت المتبقي", "الوقت المتبقي للتسليم");
        var statusText = string.Join(" ", new[] { submissionRow?.Value, remainingRow?.Value }.Where(value => !string.IsNullOrWhiteSpace(value)));
        var status = MapStatus(statusText);
        var gradingRow = FindRow(snapshot.Rows, "grading status", "حالة التقييم", "حالة التصحيح");
        var gradingStatus = MapGradingStatus(gradingRow?.Value);
        if (status == MoodleAssignmentStatus.Unavailable && gradingStatus == MoodleGradingStatus.NeedsChanges)
            status = MoodleAssignmentStatus.NeedsRevision;
        var acceptanceText = string.Join(" ", new[] { remainingRow?.Value, snapshot.Body }.Where(value => !string.IsNullOrWhiteSpace(value)));
        var acceptance = MapSubmissionAcceptance(acceptanceText);

        var availableRow = FindRow(snapshot.Rows, "allow submissions from", "available from", "opens", "opened", "السماح بالتسليم من", "متاح ابتداء", "متاح من", "يفتح");
        var availableTime = FindTime(snapshot.TimeValues, "allow submissions from", "available from", "opens", "opened", "السماح بالتسليم من", "متاح ابتداء", "متاح من", "يفتح");
        var available = ParseDate(availableRow?.Value ?? availableTime?.Label, availableRow?.Iso ?? availableTime?.Value);

        var cutoffRow = FindRow(snapshot.Rows, "cut-off date", "cutoff date", "cut off date", "آخر موعد", "تاريخ الإغلاق", "موعد الإغلاق");
        var cutoffTime = FindTime(snapshot.TimeValues, "cut-off date", "cutoff date", "cut off date", "آخر موعد", "تاريخ الإغلاق", "موعد الإغلاق");
        var cutoff = ParseDate(cutoffRow?.Value ?? cutoffTime?.Label, cutoffRow?.Iso ?? cutoffTime?.Value);

        return new MoodleAssignment(title, course, url, string.IsNullOrWhiteSpace(snapshot.Description) ? null : snapshot.Description,
            dueAt, dueKnowledge, available, cutoff, status, submittedAt, gradingStatus, acceptance);
    }

    public static MoodleSubmissionAcceptance MapSubmissionAcceptance(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return MoodleSubmissionAcceptance.Unknown;
        var value = Normalize(text);
        if (Has(value, "submissions are no longer accepted", "submissions are not accepted", "not accepting submissions", "no more submissions", "submission period has ended", "لم يعد التسليم متاح", "لا يمكن التسليم", "انتهى وقت التسليم"))
            return MoodleSubmissionAcceptance.NotAccepted;
        if (Has(value, "submissions are accepted", "submission is accepted", "submissions accepted", "التسليم متاح", "يمكن التسليم", "ما زال التسليم متاح"))
            return MoodleSubmissionAcceptance.Accepted;
        return MoodleSubmissionAcceptance.Unknown;
    }

    private static MoodleAssignmentPageRow? FindRow(IEnumerable<MoodleAssignmentPageRow> rows, params string[] labels) =>
        rows.FirstOrDefault(row => labels.Any(label => LabelMatches(row.Label, label)));

    private static MoodleAssignmentPageTime? FindTime(IEnumerable<MoodleAssignmentPageTime> values, params string[] labels) =>
        values.FirstOrDefault(value => labels.Any(label => LabelMatches(value.Label, label)));

    private static bool LabelMatches(string? actual, string expected)
    {
        var normalized = Normalize(actual ?? "");
        var target = Normalize(expected);
        return normalized.Equals(target, StringComparison.OrdinalIgnoreCase)
            || normalized.StartsWith(target + ":", StringComparison.OrdinalIgnoreCase)
            || normalized.StartsWith(target + " ", StringComparison.OrdinalIgnoreCase);
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
        .OrderBy(a => a.DueDateKnowledge == MoodleDateKnowledge.Known && a.DueAt >= now ? 0 : IsOverdue(a, now) ? 1 : a.DueDateKnowledge == MoodleDateKnowledge.Known ? 2 : 3)
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
        if (!string.IsNullOrEmpty(input) && DateTimeOffset.TryParse(input, CultureInfo.GetCultureInfo("ar"), DateTimeStyles.AssumeUniversal, out date)) return true;
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
