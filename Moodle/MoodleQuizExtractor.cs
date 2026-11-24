using System.Globalization;
using System.Text.RegularExpressions;

namespace Wawshni.Bot.Moodle;

public enum MoodleQuizResultKind { Success, Partial, Expired, SessionRestoreFailed, Unavailable, ExtractionFailed }
public sealed record MoodleQuiz(string Title, string Course, string Url, DateTimeOffset? OpensAt,
    DateTimeOffset? ClosesAt, string AttemptsAllowed, string TimeLimit, string Availability,
    string AttemptStatus, string? StartedAt, string? CompletedAt, string? Duration, string? Grade);
public sealed record MoodleQuizResult(MoodleQuizResultKind Kind, IReadOnlyList<MoodleQuiz> Quizzes,
    int TotalCourses, int CoursesInspected, int CoursesWithoutQuizzes, int CoursesFailed,
    int QuizPagesInspected, int QuizPagesFailed);
public sealed class MoodleQuizPageSnapshot
{
    public string Title { get; set; } = "";
    public string Body { get; set; } = "";
    public string Url { get; set; } = "";
    public List<MoodleQuizPageTime> Times { get; set; } = [];
}
public sealed class MoodleQuizPageTime { public string Label { get; set; } = ""; public string Value { get; set; } = ""; }

public static class MoodleQuizExtractor
{
    public static MoodleQuiz? Parse(MoodleQuizPageSnapshot page, string course, string timeZoneId = "Asia/Gaza")
    {
        var title = Clean(page.Title);
        if (title.Length == 0) return null;
        var opens = FindTime(page.Times, page.Body, timeZoneId, "open", "opened", "opens", "يبدأ", "يفتح", "مفتوح", "تاريخ البدء");
        var closes = FindTime(page.Times, page.Body, timeZoneId, "close", "closed", "closes", "end", "يغلق", "مغلق", "ينتهي", "تاريخ الإغلاق");
        var attempts = FindValue(page.Body, "attempts allowed", "عدد المحاولات المسموح بها", "المحاولات المسموح بها", "المحاولات المسموح بها:");
        var limit = FindValue(page.Body, "time limit", "الحد الزمني", "المدة الزمنية");
        var availability = FindPhrase(page.Body,
            "This quiz is currently not available", "This quiz is not available", "This quiz is available", "You can no longer attempt this quiz",
            "هذا الاختبار غير متاح حالياً", "هذا الاختبار غير متاح حاليا", "الاختبار غير متاح حالياً", "الاختبار متاح حالياً",
            "انتهى وقت الاختبار", "لم يعد بإمكانك محاولة هذا الاختبار");
        var attemptStatus = FindPhrase(page.Body, "Finished", "In progress", "Overdue", "Abandoned", "لم تبدأ", "قيد التنفيذ", "مكتملة", "مكتمل", "منتهية", "منتهي");
        var started = FindLineValue(page.Body, "Started", "بدأت", "وقت البدء");
        var completed = FindLineValue(page.Body, "Completed", "اكتملت", "وقت الإكمال", "وقت الانتهاء");
        var duration = FindLineValue(page.Body, "Duration", "المدة");
        var grade = FindLineValue(page.Body, "Grade", "الدرجة", "العلامة")
            ?? Regex.Match(page.Body, @"(?i)(?:your final grade for this quiz is|درجتك النهائية في هذا الاختبار هي)\s*([^\r\n.]+)").Groups[1].Value.Trim();
        if (string.IsNullOrWhiteSpace(grade)) grade = null;
        var url = CanonicalUrl(page.Url);
        return new(title, course, url, opens, closes, attempts, limit, availability, attemptStatus, started, completed, duration, grade);
    }

    public static IReadOnlyList<MoodleQuiz> Deduplicate(IEnumerable<MoodleQuiz> quizzes) => quizzes
        .GroupBy(q => q.Url, StringComparer.OrdinalIgnoreCase).Select(g => g.First()).ToArray();

    private static DateTimeOffset? FindTime(IEnumerable<MoodleQuizPageTime> times, string body, string zone,
        params string[] labels)
    {
        var entry = times.FirstOrDefault(t => labels.Any(l => Normalize(t.Label).Contains(Normalize(l), StringComparison.Ordinal)));
        if (entry is not null && ParseDate(entry.Value, zone, out var date)) return date;
        foreach (var line in body.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var normalized = Normalize(line);
            if (!labels.Any(l => normalized.Contains(Normalize(l), StringComparison.Ordinal))) continue;
            var value = line.Contains(':') ? line[(line.IndexOf(':') + 1)..].Trim() : line;
            if (ParseDate(value, zone, out date)) return date;
        }
        return null;
    }
    private static bool ParseDate(string value, string zone, out DateTimeOffset date)
    {
        value = NormalizeDigits(value).Replace("Opened", "", StringComparison.OrdinalIgnoreCase).Replace("Opens", "", StringComparison.OrdinalIgnoreCase)
            .Replace("Closed", "", StringComparison.OrdinalIgnoreCase).Replace("Closes", "", StringComparison.OrdinalIgnoreCase)
            .Replace("يفتح", "").Replace("يفتح:", "").Replace("يغلق", "").Replace("يغلق:", "").Trim(' ', ':', '،');
        foreach (var culture in new[] { CultureInfo.GetCultureInfo("en"), CultureInfo.GetCultureInfo("ar"), CultureInfo.GetCultureInfo("ar-PS") })
        {
            if (!DateTime.TryParse(value, culture, DateTimeStyles.AllowWhiteSpaces, out var parsed)) continue;
            try { var tz = TimeZoneInfo.FindSystemTimeZoneById(zone); date = new DateTimeOffset(DateTime.SpecifyKind(parsed, DateTimeKind.Unspecified), tz.GetUtcOffset(parsed)); return true; }
            catch (TimeZoneNotFoundException) { date = new DateTimeOffset(DateTime.SpecifyKind(parsed, DateTimeKind.Local)); return true; }
        }
        date = default; return false;
    }
    private static string FindValue(string body, params string[] labels)
    {
        foreach (var line in body.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            if (labels.Any(label => Normalize(line).StartsWith(Normalize(label), StringComparison.Ordinal)))
                return Regex.Replace(line[(line.IndexOf(':') >= 0 ? line.IndexOf(':') + 1 : 0)..].Trim(), @"\s+", " ");
        return "غير متاح";
    }
    private static string FindPhrase(string body, params string[] phrases) =>
        phrases.FirstOrDefault(p => Normalize(body).Contains(Normalize(p), StringComparison.Ordinal)) ?? "غير متاح";
    private static string? FindLineValue(string body, params string[] labels)
    {
        foreach (var line in body.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            if (labels.Any(label => Normalize(line).StartsWith(Normalize(label), StringComparison.Ordinal)))
                return Regex.Replace(line[(line.IndexOf(':') >= 0 ? line.IndexOf(':') + 1 : 0)..].Trim(), @"\s+", " ");
        return null;
    }
    private static string CanonicalUrl(string value)
    {
        if (!Uri.TryCreate(value, UriKind.Absolute, out var uri)) return value;
        var id = System.Web.HttpUtility.ParseQueryString(uri.Query)["id"];
        return new UriBuilder(uri) { Query = string.IsNullOrWhiteSpace(id) ? "" : "id=" + Uri.EscapeDataString(id), Fragment = "" }.Uri.AbsoluteUri;
    }
    private static string Clean(string value) => Regex.Replace(value ?? "", @"\s+", " ").Trim();
    private static string Normalize(string value) => Regex.Replace(Regex.Replace(value.Normalize(), @"[\u064B-\u065F\u0670\u0640]", ""), @"\s+", " ").Trim().ToLowerInvariant();
    private static string NormalizeDigits(string value) => new(value.Select(c => c is >= '\u0660' and <= '\u0669' ? (char)('0' + c - '\u0660') : c is >= '\u06f0' and <= '\u06f9' ? (char)('0' + c - '\u06f0') : c).ToArray());
}

public static class MoodleQuizMessageFormatter
{
    public static string Format(MoodleQuizResult result, string timeZoneId)
    {
        var text = new System.Text.StringBuilder("📝 اختباراتك وامتحاناتك في Moodle");
        if (result.Kind == MoodleQuizResultKind.Expired) return "انتهت جلسة Moodle. أرسل /link لإعادة الربط.";
        if (result.Kind == MoodleQuizResultKind.SessionRestoreFailed) return "تعذر استرجاع جلسة Moodle المحفوظة. أرسل /link لإعادة الربط.";
        if (result.Kind == MoodleQuizResultKind.Unavailable) return "تعذر الاتصال بـ Moodle لقراءة الاختبارات حالياً. حاول لاحقاً.";
        if (result.Kind == MoodleQuizResultKind.ExtractionFailed) return "تعذر استخراج الاختبارات من Moodle. لم أعتبر ذلك دليلاً على عدم وجود اختبارات.";
        text.AppendLine(result.Kind == MoodleQuizResultKind.Partial ? " (نتائج جزئية)" : "");
        text.AppendLine($"المساقات: {result.TotalCourses}؛ فُحص {result.CoursesInspected}، بلا اختبارات {result.CoursesWithoutQuizzes}، وتعذر فحص {result.CoursesFailed}.");
        text.AppendLine($"الاختبارات: فُحصت تفاصيل {result.QuizPagesInspected}، وتعذر فحص {result.QuizPagesFailed}.");
        if (result.Quizzes.Count == 0)
        {
            text.Append(result.Kind == MoodleQuizResultKind.Partial ? "تعذر العثور على اختبارات مؤكدة؛ قد تكون بعض المساقات لم تُفحص." : "لا توجد اختبارات ظاهرة في المساقات المفحوصة.");
            return text.ToString();
        }
        var now = DateTimeOffset.UtcNow;
        var ordered = result.Quizzes.OrderBy(q => q.ClosesAt.HasValue && q.ClosesAt.Value >= now ? 0 : q.ClosesAt.HasValue ? 1 : 2)
            .ThenBy(q => q.ClosesAt ?? DateTimeOffset.MaxValue).ThenBy(q => q.Course, StringComparer.OrdinalIgnoreCase);
        foreach (var group in ordered.GroupBy(q => q.Course))
        {
            text.AppendLine().AppendLine($"📖 {group.Key}");
            foreach (var quiz in group)
            {
                text.Append("• ").AppendLine(quiz.Title);
                if (quiz.OpensAt.HasValue) text.Append("  يفتح: ").AppendLine(Date(quiz.OpensAt.Value, timeZoneId));
                if (quiz.ClosesAt.HasValue) text.Append("  يغلق: ").AppendLine(Date(quiz.ClosesAt.Value, timeZoneId));
                else text.AppendLine("  موعد الإغلاق: غير متاح");
                text.Append("  المحاولات المسموحة: ").AppendLine(quiz.AttemptsAllowed);
                text.Append("  المدة: ").AppendLine(quiz.TimeLimit);
                text.Append("  الحالة: ").AppendLine(ArabicStatus(quiz.Availability));
                if (quiz.AttemptStatus != "غير متاح") text.Append("  حالة المحاولة: ").AppendLine(ArabicStatus(quiz.AttemptStatus));
                if (!string.IsNullOrWhiteSpace(quiz.StartedAt)) text.Append("  بدأت: ").AppendLine(quiz.StartedAt);
                if (!string.IsNullOrWhiteSpace(quiz.CompletedAt)) text.Append("  اكتملت: ").AppendLine(quiz.CompletedAt);
                if (!string.IsNullOrWhiteSpace(quiz.Duration)) text.Append("  مدة المحاولة: ").AppendLine(quiz.Duration);
                if (!string.IsNullOrWhiteSpace(quiz.Grade)) text.Append("  الدرجة: ").AppendLine(quiz.Grade);
                text.Append("  الرابط: ").AppendLine(quiz.Url);
            }
        }
        return text.ToString().TrimEnd();
    }
    private static string Date(DateTimeOffset value, string zone)
    {
        try { return TimeZoneInfo.ConvertTime(value, TimeZoneInfo.FindSystemTimeZoneById(zone)).ToString("dd MMM yyyy - hh:mm tt", CultureInfo.GetCultureInfo("ar")); }
        catch (TimeZoneNotFoundException) { return value.ToString("dd MMM yyyy - hh:mm tt", CultureInfo.GetCultureInfo("ar")); }
    }
    private static string ArabicStatus(string value) => value switch
    {
        "غير متاح" => value, "This quiz is currently not available" or "This quiz is not available" => "غير متاح حالياً",
        "This quiz is available" => "متاح حالياً", "You can no longer attempt this quiz" => "انتهت إمكانية المحاولة",
        "Finished" => "مكتملة", "In progress" => "قيد التنفيذ", "Overdue" => "انتهى الوقت", "Abandoned" => "متروكة",
        "لم تبدأ" or "قيد التنفيذ" or "مكتملة" or "مكتمل" or "منتهية" or "منتهي" or "هذا الاختبار غير متاح حالياً" or "هذا الاختبار غير متاح حاليا" or "الاختبار غير متاح حالياً" => value,
        _ => value
    };
}
