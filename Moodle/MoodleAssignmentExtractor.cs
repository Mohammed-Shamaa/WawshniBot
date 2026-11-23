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
public sealed record MoodleAssignmentCourse(int Id, string Name);
public sealed record MoodleAssignmentCourseListResult(MoodleAssignmentsResultKind Kind, IReadOnlyList<MoodleAssignmentCourse> Courses);
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
    public List<string> BodyLines { get; set; } = [];
    public string Description { get; set; } = "";
    public List<MoodleAssignmentPageTime> TimeValues { get; set; } = [];
    public List<MoodleAssignmentPageRow> Rows { get; set; } = [];
}

public sealed class MoodleAssignmentPageTime { public string Label { get; set; } = ""; public string? Value { get; set; } }
public sealed class MoodleAssignmentPageRow { public string Label { get; set; } = ""; public string Value { get; set; } = ""; public string? Iso { get; set; } }

/// <summary>Maps authenticated Moodle page data to user-facing assignment records.</summary>
public static class MoodleAssignmentExtractor
{
    private static readonly string[] DueLabels =
    [
        "due date", "due", "deadline", "submission deadline", "موعد التسليم", "تاريخ التسليم",
        "تاريخ الاستحقاق", "موعد الاستحقاق", "موعد نهائي", "الموعد النهائي", "التاريخ النهائي للتسليم",
        "تاريخ الاستحقاق للتسليم"
    ];
    private static readonly string[] SubmissionStatusLabels =
        ["submission status", "حالة التسليم", "حالة الإرسال", "حالة التقديم", "حالة الواجب"];
    private static readonly string[] TimeRemainingLabels =
        ["time remaining", "الوقت المتبقي", "الوقت المتبقي للتسليم"];
    private static readonly string[] GradingStatusLabels =
        ["grading status", "حالة التقييم", "حالة التصحيح"];

    public static MoodleAssignmentPageSnapshot ParsePageSnapshot(string json) =>
        System.Text.Json.JsonSerializer.Deserialize<MoodleAssignmentPageSnapshot>(
            json, new System.Text.Json.JsonSerializerOptions(System.Text.Json.JsonSerializerDefaults.Web))
        ?? throw new System.Text.Json.JsonException("Moodle assignment page returned an empty DOM snapshot.");

    public static string NormalizeTitle(string? title) => string.IsNullOrWhiteSpace(title) ? string.Empty : Regex.Replace(title, @"\s+", " ").Trim();

    public static MoodleAssignmentStatus MapStatus(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return MoodleAssignmentStatus.Unavailable;
        var value = Normalize(text);
        if (Has(value, "not graded", "not yet graded", "لم يتم التقييم", "لم يتم التصحيح", "غير مصحح", "بانتظار التقييم"))
            return MoodleAssignmentStatus.Unavailable;
        if (Has(value, "graded", "تم التقييم", "تم تقييم", "مقيّم", "مقيم")) return MoodleAssignmentStatus.Graded;
        if (Has(value, "needs revision", "needs changes", "requires changes", "يحتاج إلى تعديل", "يحتاج تعديل", "بحاجة إلى تعديل")) return MoodleAssignmentStatus.NeedsRevision;
        if (Has(value, "no online submissions required", "does not require you to submit anything online", "offline", "لا يتطلب تسليماً", "لا يتطلب تسليم")) return MoodleAssignmentStatus.NotRequired;
        if (Has(value, "draft (not submitted)", "draft", "مسودة", "لم يتم الإرسال")) return MoodleAssignmentStatus.Draft;
        if (Has(value, "nothing submitted", "no submission", "no attempt", "not submitted", "لا يوجد تسليم", "لا توجد تسليمات", "لم يتم التسليم", "لم يتم تسليم", "لم تسلم", "لم يتم تقديم", "لم يقدم الواجب", "لم ترفع أي ملفات")) return MoodleAssignmentStatus.NotSubmitted;
        if (Has(value, "submitted", "تم التسليم", "تم تسليم", "تم الإرسال", "تم التقديم") && Has(value, "late", "متأخر", "متأخرا")) return MoodleAssignmentStatus.SubmittedLate;
        if (Has(value, "submitted late", "late submission", "تم التسليم متأخراً", "تم التسليم متأخرا", "تسليم متأخر", "بعد الموعد", "متأخر")) return MoodleAssignmentStatus.SubmittedLate;
        if (Has(value, "submitted for grading", "submitted for marking", "submitted", "تم التسليم", "تم تسليم", "تم الإرسال", "تم التقديم", "تم رفع الواجب", "قيد التقييم", "قيد التصحيح")) return MoodleAssignmentStatus.Submitted;
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

    public static MoodleAssignment? ParseAssignmentPage(MoodleAssignmentPageSnapshot snapshot, string url, string course, string timeZoneId = "Asia/Gaza")
    {
        var title = NormalizeTitle(snapshot.Title);
        if (title.Length == 0) return null;

        // In Moodle, Due date and Cut-off date are distinct. In Arabic, "آخر موعد للتسليم"
        // describes the cut-off and must never be substituted for the normal Due date.
        var dueRows = FindRows(snapshot.Rows, DueLabels);
        var dueRow = dueRows.FirstOrDefault(row => ParseDueDate(row.Value, row.Iso, timeZoneId).Knowledge != MoodleDateKnowledge.Unknown)
            ?? dueRows.FirstOrDefault();
        var dueTime = FindTime(snapshot.TimeValues, DueLabels);
        var dueBody = FindBodyValue(snapshot.BodyLines, DueLabels);
        var dueCandidate = new[] { (Text: dueRow?.Value, Iso: dueRow?.Iso), (Text: dueTime?.Label, Iso: dueTime?.Value), (Text: dueBody, Iso: (string?)null) }
            .FirstOrDefault(candidate => !string.IsNullOrWhiteSpace(candidate.Text)
                && ParseDueDate(candidate.Text, candidate.Iso, timeZoneId).Knowledge != MoodleDateKnowledge.Unknown);
        var dueText = FirstNonBlank(dueCandidate.Text, dueRow?.Value, dueTime?.Label, dueBody);
        var dueIso = dueCandidate.Iso ?? dueRow?.Iso ?? dueTime?.Value;
        var (dueAt, dueKnowledge) = ParseDueDate(dueText, dueIso, timeZoneId);
        if (dueKnowledge == MoodleDateKnowledge.Unknown && (dueRow is not null || dueTime is not null))
            dueKnowledge = MoodleDateKnowledge.ExtractionFailed;

        var submittedRow = FindRow(snapshot.Rows,
            "last modified (submission)", "last modified", "submission date", "submitted on",
            "آخر تعديل للتسليم", "آخر تعديل", "وقت التسليم", "تاريخ ووقت التسليم");
        var submittedTime = FindTime(snapshot.TimeValues,
            "last modified", "submission date", "submitted on", "آخر تعديل", "وقت التسليم", "تاريخ ووقت التسليم");
        var submittedAt = ParseDate(FirstNonBlank(submittedRow?.Value, submittedTime?.Label), submittedRow?.Iso ?? submittedTime?.Value, timeZoneId);

        var submissionRows = FindRows(snapshot.Rows, SubmissionStatusLabels);
        var submissionRow = submissionRows.FirstOrDefault(row => MapStatus(row.Value) != MoodleAssignmentStatus.Unavailable)
            ?? submissionRows.FirstOrDefault();
        var remainingRows = FindRows(snapshot.Rows, TimeRemainingLabels);
        var remainingRow = remainingRows.FirstOrDefault(row => MapStatus(row.Value) != MoodleAssignmentStatus.Unavailable)
            ?? remainingRows.FirstOrDefault();
        var bodySubmissionText = FindBodyValue(snapshot.BodyLines, SubmissionStatusLabels);
        var submissionText = FirstNonBlank(
            submissionRows.Select(row => row.Value).FirstOrDefault(value => MapStatus(value) != MoodleAssignmentStatus.Unavailable),
            bodySubmissionText is not null && MapStatus(bodySubmissionText) != MoodleAssignmentStatus.Unavailable ? bodySubmissionText : null,
            submissionRow?.Value,
            bodySubmissionText);
        var remainingText = FirstNonBlank(remainingRow?.Value,
            FindBodyValue(snapshot.BodyLines, TimeRemainingLabels));
        var statusText = string.Join(" ", new[] { submissionText, remainingText }.Where(value => !string.IsNullOrWhiteSpace(value)));
        var status = MapStatus(statusText);
        var gradingRows = FindRows(snapshot.Rows, GradingStatusLabels);
        var gradingRow = gradingRows.FirstOrDefault(row => MapGradingStatus(row.Value) != MoodleGradingStatus.Unavailable)
            ?? gradingRows.FirstOrDefault();
        var gradingText = FirstNonBlank(
            gradingRows.Select(row => row.Value).FirstOrDefault(value => MapGradingStatus(value) != MoodleGradingStatus.Unavailable),
            gradingRow?.Value,
            FindBodyValue(snapshot.BodyLines, GradingStatusLabels));
        var gradingStatus = MapGradingStatus(gradingText);
        if (status == MoodleAssignmentStatus.Unavailable && gradingStatus == MoodleGradingStatus.NeedsChanges)
            status = MoodleAssignmentStatus.NeedsRevision;
        var acceptanceText = string.Join(" ", new[] { remainingRow?.Value, snapshot.Body }.Where(value => !string.IsNullOrWhiteSpace(value)));
        var acceptance = MapSubmissionAcceptance(acceptanceText);

        var availableRow = FindRow(snapshot.Rows, "allow submissions from", "available from", "opens", "opened", "السماح بالتسليم من", "متاح ابتداء", "متاح من", "يفتح");
        var availableTime = FindTime(snapshot.TimeValues, "allow submissions from", "available from", "opens", "opened", "السماح بالتسليم من", "متاح ابتداء", "متاح من", "يفتح");
        var available = ParseDate(FirstNonBlank(availableRow?.Value, availableTime?.Label), availableRow?.Iso ?? availableTime?.Value, timeZoneId);

        var cutoffRow = FindRow(snapshot.Rows, "cut-off date", "cutoff date", "cut off date", "آخر موعد للتسليم", "آخر موعد", "تاريخ الإغلاق", "موعد الإغلاق");
        var cutoffTime = FindTime(snapshot.TimeValues, "cut-off date", "cutoff date", "آخر موعد للتسليم", "آخر موعد", "تاريخ الإغلاق", "موعد الإغلاق");
        var cutoff = ParseDate(FirstNonBlank(cutoffRow?.Value, cutoffTime?.Label), cutoffRow?.Iso ?? cutoffTime?.Value, timeZoneId);

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
        FindRows(rows, labels).FirstOrDefault(row => !string.IsNullOrWhiteSpace(row.Value)) ?? FindRows(rows, labels).FirstOrDefault();

    private static IEnumerable<MoodleAssignmentPageRow> FindRows(IEnumerable<MoodleAssignmentPageRow> rows, params string[] labels) =>
        rows.Where(row => labels.Any(label => LabelMatches(row.Label, label)));

    private static MoodleAssignmentPageTime? FindTime(IEnumerable<MoodleAssignmentPageTime> values, params string[] labels) =>
        values.FirstOrDefault(value => labels.Any(label => LabelMatches(value.Label, label)));

    private static string? FindBodyValue(IReadOnlyList<string> lines, params string[] labels)
    {
        for (var index = 0; index < lines.Count; index++)
        {
            var line = Normalize(lines[index]);
            foreach (var label in labels.OrderByDescending(value => value.Length))
            {
                var target = Normalize(label);
                if (line.Equals(target, StringComparison.OrdinalIgnoreCase))
                {
                    if (index + 1 < lines.Count && !IsKnownFieldLabel(lines[index + 1])) return lines[index + 1].Trim();
                    continue;
                }

                if (line.StartsWith(target + ":", StringComparison.OrdinalIgnoreCase)
                    || line.StartsWith(target + " ", StringComparison.OrdinalIgnoreCase))
                    return lines[index][label.Length..].Trim().TrimStart(':', '：', '-', '–').Trim();
            }
        }
        return null;
    }

    private static string? FirstNonBlank(params string?[] values) => values
        .FirstOrDefault(value => !string.IsNullOrWhiteSpace(value)
            && value.Trim() is not ("-" or "–" or "—" or "N/A" or "غير متاح" or "غير متاحة"))?.Trim();

    private static bool IsKnownFieldLabel(string value) =>
        SubmissionStatusLabels.Concat(GradingStatusLabels).Concat(TimeRemainingLabels)
            .Concat(["last modified", "آخر تعديل"])
            .Any(label => Normalize(value).Equals(Normalize(label), StringComparison.OrdinalIgnoreCase));

    private static bool LabelMatches(string? actual, string expected)
    {
        var normalized = Normalize(actual ?? "");
        var target = Normalize(expected);
        return normalized.Equals(target, StringComparison.OrdinalIgnoreCase)
            || normalized.StartsWith(target + ":", StringComparison.OrdinalIgnoreCase)
            || normalized.StartsWith(target + " ", StringComparison.OrdinalIgnoreCase);
    }

    public static (DateTimeOffset? Value, MoodleDateKnowledge Knowledge) ParseDueDate(string? text, string? isoValue = null, string timeZoneId = "Asia/Gaza")
    {
        var source = string.IsNullOrWhiteSpace(isoValue) ? text : isoValue;
        if (long.TryParse(source, NumberStyles.Integer, CultureInfo.InvariantCulture, out var unsetTimestamp) && unsetTimestamp == 0)
            return (null, MoodleDateKnowledge.NoDeadline);
        if (TryDate(source, timeZoneId, out var date)) return (date, MoodleDateKnowledge.Known);
        if (!string.IsNullOrWhiteSpace(text) && Has(Normalize(text), "no due date", "no deadline", "بدون موعد", "بدون موعد نهائي", "لا يوجد موعد", "لا يوجد موعد نهائي", "لم يحدد موعد التسليم", "لم يتم تحديد موعد التسليم", "لا يوجد تاريخ استحقاق"))
            return (null, MoodleDateKnowledge.NoDeadline);
        return (null, MoodleDateKnowledge.Unknown);
    }

    public static DateTimeOffset? ParseDate(string? text, string? isoValue = null, string timeZoneId = "Asia/Gaza")
    {
        var source = string.IsNullOrWhiteSpace(isoValue) ? text : isoValue;
        if (long.TryParse(source, NumberStyles.Integer, CultureInfo.InvariantCulture, out var unsetTimestamp) && unsetTimestamp == 0) return null;
        return TryDate(source, timeZoneId, out var value) ? value : null;
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
        .OrderBy(a => SortGroup(a, now))
        .ThenBy(a => a.DueAt ?? DateTimeOffset.MaxValue)
        .ThenBy(a => a.Course, StringComparer.CurrentCulture)
        .ToList();

    public static bool IsConfirmedSubmitted(MoodleAssignment assignment) =>
        assignment.Status is MoodleAssignmentStatus.Submitted or MoodleAssignmentStatus.SubmittedLate or MoodleAssignmentStatus.Graded;

    private static int SortGroup(MoodleAssignment assignment, DateTimeOffset now)
    {
        if (IsConfirmedSubmitted(assignment)) return 0;
        if (assignment.DueDateKnowledge == MoodleDateKnowledge.Known && assignment.DueAt.HasValue &&
            assignment.Status is MoodleAssignmentStatus.NotSubmitted or MoodleAssignmentStatus.Draft)
            return assignment.DueAt.Value >= now ? 1 : 2;
        if (assignment.DueDateKnowledge is MoodleDateKnowledge.Unknown or MoodleDateKnowledge.ExtractionFailed) return 3;
        if (assignment.DueDateKnowledge == MoodleDateKnowledge.NoDeadline) return 4;
        return 3;
    }

    public static bool IsOverdue(MoodleAssignment assignment, DateTimeOffset now) =>
        assignment.DueDateKnowledge == MoodleDateKnowledge.Known && assignment.DueAt < now &&
        assignment.Status is MoodleAssignmentStatus.NotSubmitted or MoodleAssignmentStatus.Draft;

    private static bool TryDate(string? input, string timeZoneId, out DateTimeOffset date)
    {
        input = input?.Trim();
        if (string.IsNullOrEmpty(input)) { date = default; return false; }

        // Moodle can expose Unix timestamps through data-timestamp (seconds or milliseconds).
        if (long.TryParse(input, NumberStyles.Integer, CultureInfo.InvariantCulture, out var unix))
        {
            try
            {
                date = input.Length >= 13
                    ? DateTimeOffset.FromUnixTimeMilliseconds(unix)
                    : DateTimeOffset.FromUnixTimeSeconds(unix);
                return true;
            }
            catch (ArgumentOutOfRangeException) { }
        }

        input = NormalizeDigits(input).Replace('\u200e', ' ').Replace('\u200f', ' ').Replace('،', ',').Trim();
        var hasExplicitOffset = Regex.IsMatch(input, @"(?:Z|[+-]\d{2}:?\d{2})$", RegexOptions.IgnoreCase);
        if (hasExplicitOffset && DateTimeOffset.TryParse(input, CultureInfo.InvariantCulture, DateTimeStyles.AllowWhiteSpaces, out date)) return true;

        // Moodle's human-readable dates have no offset. Interpret them in the configured
        // Moodle account timezone, rather than silently assigning the server's UTC timezone.
        foreach (var culture in new[] { CultureInfo.GetCultureInfo("en"), CultureInfo.GetCultureInfo("ar"), CultureInfo.GetCultureInfo("ar-PS") })
        {
            if (!DateTime.TryParse(input, culture, DateTimeStyles.AllowWhiteSpaces, out var localDate)) continue;
            try
            {
                var moodleZone = TimeZoneInfo.FindSystemTimeZoneById(timeZoneId);
                var unspecified = DateTime.SpecifyKind(localDate, DateTimeKind.Unspecified);
                date = new DateTimeOffset(unspecified, moodleZone.GetUtcOffset(unspecified));
                return true;
            }
            catch (TimeZoneNotFoundException) { }
            catch (InvalidTimeZoneException) { }
        }
        date = default;
        return false;
    }

    private static string NormalizeDigits(string value)
    {
        var builder = new System.Text.StringBuilder(value.Length);
        foreach (var character in value)
        {
            if (character is >= '\u0660' and <= '\u0669') builder.Append((char)('0' + character - '\u0660'));
            else if (character is >= '\u06f0' and <= '\u06f9') builder.Append((char)('0' + character - '\u06f0'));
            else builder.Append(character);
        }
        return builder.ToString();
    }

    private static string CanonicalUrl(string value)
    {
        var uri = new Uri(value);
        var query = System.Web.HttpUtility.ParseQueryString(uri.Query);
        var id = query.Get("id");
        var builder = new UriBuilder(uri) { Query = string.IsNullOrWhiteSpace(id) ? string.Empty : "id=" + Uri.EscapeDataString(id), Fragment = string.Empty };
        return builder.Uri.AbsoluteUri.TrimEnd('/');
    }

    private static string Normalize(string value)
    {
        var normalized = value.Normalize(System.Text.NormalizationForm.FormKC);
        normalized = Regex.Replace(normalized, @"[\u064B-\u065F\u0670\u0640]", string.Empty);
        return Regex.Replace(normalized, @"\s+", " ").Trim().ToLowerInvariant();
    }
    private static bool Has(string value, params string[] options) => options.Any(option => value.Contains(option, StringComparison.OrdinalIgnoreCase));
}
