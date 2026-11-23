using Wawshni.Bot.Handlers;
using Wawshni.Bot.Moodle;
using Xunit;

namespace Wawshni.Bot.Tests;

public sealed class MoodleAssignmentMessageFormatterTests
{
    private static readonly DateTimeOffset Now = DateTimeOffset.Parse("2026-11-20T12:00:00+02:00");

    [Theory]
    [InlineData(MoodleAssignmentStatus.Submitted, "✓ تم التسليم")]
    [InlineData(MoodleAssignmentStatus.SubmittedLate, "✓ تم التسليم متأخراً")]
    [InlineData(MoodleAssignmentStatus.Graded, "✓ تم التسليم")]
    public void Confirmed_submissions_show_only_course_name_assignment_name_and_status(MoodleAssignmentStatus status, string expectedStatus)
    {
        var result = MoodleAssignmentMessageFormatter.Format(new MoodleAssignmentsResult(MoodleAssignmentsResultKind.Success,
        [
            Assignment("تم رفع البحث", status, Now.AddDays(-1)) with
            {
                SubmittedAt = Now.AddHours(-3), GradingStatus = MoodleGradingStatus.Graded,
                SubmissionAcceptance = MoodleSubmissionAcceptance.NotAccepted, CutoffAt = Now.AddDays(-2)
            }
        ]), Now);

        Assert.Contains("📖 مشروع التخرج", result);
        Assert.Contains("• تم رفع البحث", result);
        Assert.Contains(expectedStatus, result);
        Assert.DoesNotContain("موعد التسليم", result);
        Assert.DoesNotContain("وقت التسليم", result);
        Assert.DoesNotContain("التقييم", result);
        Assert.DoesNotContain("قبول التسليم", result);
        Assert.DoesNotContain("view.php", result);
    }

    [Fact]
    public void Unsubmitted_assignment_shows_arabic_due_date_and_status_only()
    {
        var result = MoodleAssignmentMessageFormatter.Format(new MoodleAssignmentsResult(MoodleAssignmentsResultKind.Success,
        [Assignment("تسليم المقترح", MoodleAssignmentStatus.NotSubmitted, DateTimeOffset.Parse("2026-11-25T21:59:00Z"))]), Now);

        Assert.Contains("📖 مشروع التخرج", result);
        Assert.Contains("• تسليم المقترح", result);
        Assert.Contains("موعد التسليم: 25 نوفمبر 2026 - 11:59 م", result);
        Assert.Contains("الحالة: لم يتم التسليم", result);
        Assert.DoesNotContain("وقت التسليم", result);
        Assert.DoesNotContain("حالة التقييم", result);
    }

    [Fact]
    public void Missing_deadline_and_failed_extraction_have_different_messages()
    {
        var noDeadline = Assignment("بلا موعد", MoodleAssignmentStatus.NotSubmitted, null) with { DueDateKnowledge = MoodleDateKnowledge.NoDeadline };
        var failed = Assignment("موعد غير مقروء", MoodleAssignmentStatus.NotSubmitted, null) with { DueDateKnowledge = MoodleDateKnowledge.ExtractionFailed };
        var result = MoodleAssignmentMessageFormatter.Format(new MoodleAssignmentsResult(MoodleAssignmentsResultKind.Success, [failed, noDeadline]), Now);

        Assert.Contains("موعد التسليم: تعذر استخراج الموعد", result);
        Assert.Contains("موعد التسليم: لم يحدد الدكتور موعداً للتسليم", result);
        Assert.True(result.IndexOf("موعد غير مقروء", StringComparison.Ordinal) < result.IndexOf("بلا موعد", StringComparison.Ordinal));
    }

    [Fact]
    public void Partial_course_diagnostics_remain_in_output()
    {
        var summary = new MoodleAssignmentRetrievalSummary(7, true, 4, 0, 3, 7, 7, 0);
        var result = MoodleAssignmentMessageFormatter.Format(new MoodleAssignmentsResult(MoodleAssignmentsResultKind.Partial,
            [Assignment("واجب جزئي", MoodleAssignmentStatus.NotSubmitted, null)], summary), Now);

        Assert.Contains("نتائج جزئية", result);
        Assert.Contains("المساقات المسجلة: 7", result);
        Assert.Contains("فُحص 4", result);
        Assert.Contains("تعذر فحص 3", result);
    }

    private static MoodleAssignment Assignment(string title, MoodleAssignmentStatus status, DateTimeOffset? due) =>
        new(title, "مشروع التخرج", "https://moodle.iugaza.edu.ps/mod/assign/view.php?id=312", null, due,
            due.HasValue ? MoodleDateKnowledge.Known : MoodleDateKnowledge.Unknown, null, null, status, null);
}
