using System.Globalization;
using Wawshni.Bot.Moodle;
using Wawshni.Bot.Services;

namespace Wawshni.Bot.Handlers;

public static class MoodleAssignmentMessageFormatter
{
    public static string Format(MoodleAssignmentsResult result, DateTimeOffset now, string timeZoneId = "Asia/Gaza")
    {
        if (result.Kind == MoodleAssignmentsResultKind.Expired) return "جلسة Moodle غير موجودة أو انتهت. أرسل /link لإعادة الربط.";
        if (result.Kind == MoodleAssignmentsResultKind.SessionRestoreFailed) return "تعذر استرجاع جلسة Moodle المحفوظة. أرسل /link لإعادة ربط الحساب.";
        if (result.Kind == MoodleAssignmentsResultKind.Unavailable) return MoodleAvailabilityMessages.TemporarilyUnavailable;
        if (result.Kind == MoodleAssignmentsResultKind.ExtractionFailed) return "تم فتح Moodle، لكن تعذر التعرف على قائمة المساقات أو الواجبات. لم أعتبر ذلك دليلاً على عدم وجود واجبات.";

        var ordered = MoodleAssignmentExtractor.Sort(result.Assignments, now);
        var lines = new List<string> { result.Kind == MoodleAssignmentsResultKind.Partial ? "📚 واجباتك الجامعية (نتائج جزئية):" : "📚 واجباتك الجامعية:" };
        if (result.Summary is { } summary)
        {
            var coursesText = summary.CourseListComplete
                ? $"المساقات المسجلة: {summary.TotalCourses}"
                : $"المساقات التي عُثر عليها: {summary.TotalCourses} على الأقل (تعذر إكمال القائمة)";
            lines.Add($"{coursesText}؛ فُحص {summary.CoursesInspected}، بلا واجبات {summary.CoursesWithoutAssignments}، وتعذر فحص {summary.CoursesFailed}.");
            lines.Add($"الواجبات: اكتُشف {summary.AssignmentsDiscovered}، وفُحصت تفاصيل {summary.AssignmentDetailsInspected}، وتعذر فحص تفاصيل {summary.AssignmentDetailsFailed}.");
        }
        if (ordered.Count == 0)
        {
            lines.Add(result.Kind == MoodleAssignmentsResultKind.Partial
                ? "تعذر التأكد من وجود واجبات بسبب تعذر قراءة بعض صفحات Moodle. أعد المحاولة لاحقاً."
                : "لم تظهر واجبات مسجلة في صفحات Moodle للمساقات المسجل بها حسابك.");
            return string.Join("\n", lines);
        }

        AppendCategory(lines, ordered.Where(MoodleAssignmentExtractor.IsConfirmedSubmitted), "✅ تم تسليمها", submittedCompact: true, timeZoneId);
        AppendCategory(lines, ordered.Where(a => IsUpcoming(a, now)), "⏰ واجبات قادمة", submittedCompact: false, timeZoneId);
        AppendCategory(lines, ordered.Where(a => MoodleAssignmentExtractor.IsOverdue(a, now)), "⚠️ واجبات متأخرة", submittedCompact: false, timeZoneId);
        AppendCategory(lines, ordered.Where(a => !MoodleAssignmentExtractor.IsConfirmedSubmitted(a)
            && a.DueDateKnowledge is MoodleDateKnowledge.Unknown or MoodleDateKnowledge.ExtractionFailed
            || (!MoodleAssignmentExtractor.IsConfirmedSubmitted(a) && a.DueDateKnowledge == MoodleDateKnowledge.Known
                && !IsNotSubmitted(a))), "📌 تعذر تحديد موعد التسليم", submittedCompact: false, timeZoneId);
        AppendCategory(lines, ordered.Where(a => !MoodleAssignmentExtractor.IsConfirmedSubmitted(a)
            && a.DueDateKnowledge == MoodleDateKnowledge.NoDeadline), "📌 واجبات بلا موعد محدد", submittedCompact: false, timeZoneId);

        return string.Join("\n", lines);
    }

    private static void AppendCategory(List<string> lines, IEnumerable<MoodleAssignment> assignments, string heading, bool submittedCompact, string timeZoneId)
    {
        var groups = assignments.GroupBy(a => a.Course).ToArray();
        if (groups.Length == 0) return;
        lines.Add("\n" + heading);
        foreach (var course in groups)
        {
            lines.Add($"\n📖 {course.Key}");
            foreach (var assignment in course)
            {
                if (submittedCompact)
                {
                    lines.Add($"• {assignment.Title}\n  ✓ {SubmissionStatusText(assignment.Status)}");
                    continue;
                }

                var due = assignment.DueDateKnowledge switch
                {
                    MoodleDateKnowledge.Known when assignment.DueAt.HasValue => FormatLocalDate(assignment.DueAt.Value, timeZoneId),
                    MoodleDateKnowledge.NoDeadline => "لم يحدد الدكتور موعداً للتسليم",
                    MoodleDateKnowledge.ExtractionFailed => "تعذر استخراج الموعد",
                    _ => "معلومات الموعد غير متاحة"
                };
                var status = SubmissionStatusText(assignment.Status);
                lines.Add($"• {assignment.Title}\n  موعد التسليم: {due}\n  الحالة: {status}");
            }
        }
    }

    private static bool IsNotSubmitted(MoodleAssignment assignment) =>
        assignment.Status is MoodleAssignmentStatus.NotSubmitted or MoodleAssignmentStatus.Draft;

    private static bool IsUpcoming(MoodleAssignment assignment, DateTimeOffset now) =>
        IsNotSubmitted(assignment) && assignment.DueDateKnowledge == MoodleDateKnowledge.Known
        && assignment.DueAt.HasValue && assignment.DueAt.Value >= now;

    private static string FormatLocalDate(DateTimeOffset date, string timeZoneId)
    {
        try { date = TimeZoneInfo.ConvertTime(date, TimeZoneInfo.FindSystemTimeZoneById(timeZoneId)); }
        catch (TimeZoneNotFoundException) { }
        catch (InvalidTimeZoneException) { }
        return date.ToString("dd MMMM yyyy - hh:mm tt", CultureInfo.GetCultureInfo("ar"));
    }

    private static string SubmissionStatusText(MoodleAssignmentStatus status) => status switch
    {
        MoodleAssignmentStatus.NotSubmitted => "لم يتم التسليم",
        MoodleAssignmentStatus.NotRequired => "لا يتطلب تسليماً إلكترونياً",
        MoodleAssignmentStatus.Draft => "مسودة غير مسلّمة",
        MoodleAssignmentStatus.Submitted => "تم التسليم",
        MoodleAssignmentStatus.SubmittedLate => "تم التسليم متأخراً",
        MoodleAssignmentStatus.NeedsRevision => "يحتاج إلى تعديل",
        MoodleAssignmentStatus.Graded => "تم التسليم",
        _ => "حالة التسليم غير متاحة"
    };
}
