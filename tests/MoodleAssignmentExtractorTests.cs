using Wawshni.Bot.Moodle;
using Xunit;

namespace Wawshni.Bot.Tests;

public sealed class MoodleAssignmentExtractorTests
{
    [Theory]
    [InlineData("مشروع التخرج النهائي", "مشروع التخرج النهائي")]
    [InlineData("  واجب   برمجة الأجهزة  ", "واجب برمجة الأجهزة")]
    public void Arabic_titles_are_preserved_and_whitespace_is_normalized(string input, string expected) => Assert.Equal(expected, MoodleAssignmentExtractor.NormalizeTitle(input));

    [Fact]
    public void Duplicate_activity_urls_are_removed_but_distinct_titles_are_preserved()
    {
        var items = new[]
        {
            Assignment("واجب 1", "https://moodle.iugaza.edu.ps/mod/assign/view.php?id=12"),
            Assignment("واجب بديل", "https://moodle.iugaza.edu.ps/mod/assign/view.php?id=12&forceview=1"),
            Assignment("واجب 1 عملي", "https://moodle.iugaza.edu.ps/mod/assign/view.php?id=13")
        };
        var actual = MoodleAssignmentExtractor.Deduplicate(items);
        Assert.Equal(2, actual.Count);
        Assert.Equal("واجب 1 عملي", actual[1].Title);
    }

    [Fact]
    public void Due_date_separates_explicit_none_from_unknown_and_parses_iso()
    {
        Assert.Equal(MoodleDateKnowledge.NoDeadline, MoodleAssignmentExtractor.ParseDueDate("No due date").Knowledge);
        Assert.Equal(MoodleDateKnowledge.NoDeadline, MoodleAssignmentExtractor.ParseDueDate("لا يوجد موعد نهائي").Knowledge);
        Assert.Equal(MoodleDateKnowledge.Unknown, MoodleAssignmentExtractor.ParseDueDate(null).Knowledge);
        var parsed = MoodleAssignmentExtractor.ParseDueDate(null, "2026-11-25T10:30:00+02:00");
        Assert.Equal(MoodleDateKnowledge.Known, parsed.Knowledge);
        Assert.Equal(TimeSpan.FromHours(2), parsed.Value!.Value.Offset);
    }

    [Theory]
    [InlineData("Nothing submitted for this assignment", MoodleAssignmentStatus.NotSubmitted)]
    [InlineData("Draft (not submitted)", MoodleAssignmentStatus.Draft)]
    [InlineData("Submitted for grading", MoodleAssignmentStatus.Submitted)]
    [InlineData("Submitted late", MoodleAssignmentStatus.SubmittedLate)]
    [InlineData("Graded", MoodleAssignmentStatus.Graded)]
    [InlineData("يحتاج إلى تعديل", MoodleAssignmentStatus.NeedsRevision)]
    [InlineData("غير معروف", MoodleAssignmentStatus.Unavailable)]
    public void Status_mapping_requires_actual_text(string text, MoodleAssignmentStatus expected) => Assert.Equal(expected, MoodleAssignmentExtractor.MapStatus(text));

    [Theory]
    [InlineData("Submitted for grading / submitted 2 days late", MoodleAssignmentStatus.SubmittedLate)]
    [InlineData("Submitted for grading", MoodleAssignmentStatus.Submitted)]
    [InlineData("No attempt", MoodleAssignmentStatus.NotSubmitted)]
    [InlineData("No submission", MoodleAssignmentStatus.NotSubmitted)]
    [InlineData("No online submissions required", MoodleAssignmentStatus.NotRequired)]
    [InlineData("تم التسليم، متأخر عن الموعد", MoodleAssignmentStatus.SubmittedLate)]
    public void Submission_status_reads_actual_moodle_status_and_lateness(string text, MoodleAssignmentStatus expected) =>
        Assert.Equal(expected, MoodleAssignmentExtractor.MapStatus(text));

    [Theory]
    [InlineData("Not graded", MoodleGradingStatus.NotGraded)]
    [InlineData("Graded", MoodleGradingStatus.Graded)]
    [InlineData("Needs changes", MoodleGradingStatus.NeedsChanges)]
    [InlineData("غير معروف", MoodleGradingStatus.Unavailable)]
    public void Grading_status_is_extracted_separately(string text, MoodleGradingStatus expected) =>
        Assert.Equal(expected, MoodleAssignmentExtractor.MapGradingStatus(text));

    [Theory]
    [InlineData("Submissions are accepted", MoodleSubmissionAcceptance.Accepted)]
    [InlineData("Submissions are no longer accepted", MoodleSubmissionAcceptance.NotAccepted)]
    [InlineData("Add submission", MoodleSubmissionAcceptance.Unknown)]
    public void Submission_acceptance_requires_explicit_page_text(string text, MoodleSubmissionAcceptance expected) =>
        Assert.Equal(expected, MoodleAssignmentExtractor.MapSubmissionAcceptance(text));

    [Fact]
    public void Parses_english_assignment_page_deadlines_submission_time_and_grading_status()
    {
        var snapshot = new MoodleAssignmentPageSnapshot
        {
            Title = "Research assignment",
            Rows =
            [
                new() { Label = "Due date", Value = "25 November 2026, 11:59 PM", Iso = "2026-11-25T21:59:00+02:00" },
                new() { Label = "Allow submissions from", Value = "20 November 2026", Iso = "2026-11-20T00:00:00+02:00" },
                new() { Label = "Cut-off date", Value = "27 November 2026", Iso = "2026-11-27T23:59:00+02:00" },
                new() { Label = "Submission status", Value = "Submitted for grading" },
                new() { Label = "Time remaining", Value = "Submissions are accepted" },
                new() { Label = "Last modified (submission)", Value = "24 November 2026, 1:15 PM", Iso = "2026-11-24T13:15:00+02:00" },
                new() { Label = "Grading status", Value = "Not graded" }
            ]
        };

        var result = MoodleAssignmentExtractor.ParseAssignmentPage(snapshot, "https://moodle.iugaza.edu.ps/mod/assign/view.php?id=10", "Research Methods");

        Assert.NotNull(result);
        Assert.Equal("Research Methods", result.Course);
        Assert.Equal(DateTimeOffset.Parse("2026-11-25T21:59:00+02:00"), result.DueAt);
        Assert.Equal(MoodleDateKnowledge.Known, result.DueDateKnowledge);
        Assert.Equal(DateTimeOffset.Parse("2026-11-20T00:00:00+02:00"), result.AvailableFrom);
        Assert.Equal(DateTimeOffset.Parse("2026-11-27T23:59:00+02:00"), result.CutoffAt);
        Assert.Equal(MoodleAssignmentStatus.Submitted, result.Status);
        Assert.Equal(DateTimeOffset.Parse("2026-11-24T13:15:00+02:00"), result.SubmittedAt);
        Assert.Equal(MoodleGradingStatus.NotGraded, result.GradingStatus);
        Assert.Equal(MoodleSubmissionAcceptance.Accepted, result.SubmissionAcceptance);
    }

    [Fact]
    public void Parses_arabic_assignment_page_and_does_not_infer_missing_status()
    {
        var snapshot = new MoodleAssignmentPageSnapshot
        {
            Title = "واجب تطبيقي",
            Rows =
            [
                new() { Label = "تاريخ الاستحقاق", Value = "25 نوفمبر 2026", Iso = "2026-11-25T10:00:00+02:00" },
                new() { Label = "حالة التسليم", Value = "تم التسليم" },
                new() { Label = "وقت التسليم", Value = "24 نوفمبر 2026", Iso = "2026-11-24T09:00:00+02:00" },
                new() { Label = "حالة التقييم", Value = "تم التصحيح" }
            ]
        };

        var result = MoodleAssignmentExtractor.ParseAssignmentPage(snapshot, "https://moodle.iugaza.edu.ps/mod/assign/view.php?id=11", "مساق عربي");

        Assert.NotNull(result);
        Assert.Equal(MoodleAssignmentStatus.Submitted, result.Status);
        Assert.Equal(MoodleGradingStatus.Graded, result.GradingStatus);
        Assert.Equal(MoodleSubmissionAcceptance.Unknown, result.SubmissionAcceptance);
        Assert.Equal(DateTimeOffset.Parse("2026-11-25T10:00:00+02:00"), result.DueAt);
        Assert.Equal(DateTimeOffset.Parse("2026-11-24T09:00:00+02:00"), result.SubmittedAt);
    }

    [Fact]
    public void Distinguishes_no_deadline_from_unavailable_and_failed_date_extraction()
    {
        var noDeadline = MoodleAssignmentExtractor.ParseAssignmentPage(new MoodleAssignmentPageSnapshot
        {
            Title = "No deadline assignment",
            Rows = [new() { Label = "Due date", Value = "No due date" }]
        }, "https://moodle.iugaza.edu.ps/mod/assign/view.php?id=12", "Course");
        var brokenDate = MoodleAssignmentExtractor.ParseAssignmentPage(new MoodleAssignmentPageSnapshot
        {
            Title = "Unreadable date assignment",
            Rows = [new() { Label = "Due date", Value = "some unknown date format" }]
        }, "https://moodle.iugaza.edu.ps/mod/assign/view.php?id=13", "Course");
        var absent = MoodleAssignmentExtractor.ParseAssignmentPage(new MoodleAssignmentPageSnapshot { Title = "Page without date fields" },
            "https://moodle.iugaza.edu.ps/mod/assign/view.php?id=14", "Course");

        Assert.Equal(MoodleDateKnowledge.NoDeadline, noDeadline!.DueDateKnowledge);
        Assert.Equal(MoodleDateKnowledge.ExtractionFailed, brokenDate!.DueDateKnowledge);
        Assert.Equal(MoodleDateKnowledge.Unknown, absent!.DueDateKnowledge);
        Assert.Equal(MoodleAssignmentStatus.Unavailable, absent.Status);
        Assert.Equal(MoodleGradingStatus.Unavailable, absent.GradingStatus);
        Assert.Equal(MoodleSubmissionAcceptance.Unknown, absent.SubmissionAcceptance);
        Assert.Null(absent.SubmittedAt);
    }

    [Fact]
    public void Sorts_upcoming_then_overdue_and_unknown_deadlines_last()
    {
        var now = DateTimeOffset.Parse("2026-11-19T12:00:00Z");
        var assignments = new[]
        {
            Assignment("unknown", "https://x.test/3", null, MoodleDateKnowledge.Unknown),
            Assignment("later", "https://x.test/2", now.AddDays(2)),
            Assignment("overdue", "https://x.test/1", now.AddDays(-1))
        };
        Assert.Equal(new[] { "later", "overdue", "unknown" }, MoodleAssignmentExtractor.Sort(assignments, now).Select(a => a.Title));
        Assert.True(MoodleAssignmentExtractor.IsOverdue(assignments[2], now));
        Assert.False(MoodleAssignmentExtractor.IsOverdue(assignments[0], now));
    }

    [Fact]
    public void Empty_and_malformed_data_do_not_create_fake_assignments()
    {
        Assert.Empty(MoodleAssignmentExtractor.Deduplicate([]));
        Assert.Empty(MoodleAssignmentExtractor.Deduplicate([Assignment("", "not a URL")]));
        Assert.Equal(MoodleAssignmentStatus.Unavailable, MoodleAssignmentExtractor.MapStatus(null));
    }

    [Fact]
    public void Recognized_empty_course_pages_mean_no_assignments_but_unrecognized_pages_are_extraction_failures()
    {
        var empty = MoodleAssignmentExtractor.CreateResult([], failureCount: 0, recognizedCoursePages: true);
        var unknown = MoodleAssignmentExtractor.CreateResult([], failureCount: 0, recognizedCoursePages: false);

        Assert.Equal(MoodleAssignmentsResultKind.Success, empty.Kind);
        Assert.Empty(empty.Assignments);
        Assert.Equal(MoodleAssignmentsResultKind.ExtractionFailed, unknown.Kind);
    }

    [Fact]
    public void Partial_course_failures_keep_real_assignments_from_successful_courses()
    {
        var real = Assignment("واجب اختباري", "https://moodle.iugaza.edu.ps/mod/assign/view.php?id=91");
        var result = MoodleAssignmentExtractor.CreateResult([real], failureCount: 1, recognizedCoursePages: true);

        Assert.Equal(MoodleAssignmentsResultKind.Partial, result.Kind);
        Assert.Single(result.Assignments);
        Assert.Equal(real.Title, result.Assignments[0].Title);
    }

    [Fact]
    public void Retrieval_summary_reports_partial_courses_and_failed_assignment_detail_pages()
    {
        var summary = new MoodleAssignmentRetrievalSummary(7, true, 7, 3, 0, 7, 6, 1);
        var assignment = Assignment("واجب تم استخراجه", "https://moodle.iugaza.edu.ps/mod/assign/view.php?id=101");
        var result = MoodleAssignmentExtractor.CreateResult([assignment], failureCount: 1, recognizedCoursePages: true) with { Summary = summary };

        Assert.Equal(MoodleAssignmentsResultKind.Partial, result.Kind);
        Assert.Equal(7, result.Summary!.TotalCourses);
        Assert.True(result.Summary.CourseListComplete);
        Assert.Equal(3, result.Summary.CoursesWithoutAssignments);
        Assert.Equal(1, result.Summary.AssignmentDetailsFailed);
        Assert.Single(result.Assignments);
    }

    [Fact]
    public void Moodle_assign_and_quiz_urls_are_classified_by_their_actual_activity_paths()
    {
        var links = MoodleAssignmentExtractor.NormalizeLinks(
        [
            new MoodleAssignmentLink("https://moodle.iugaza.edu.ps/mod/assign/view.php?id=412159", "واجب برمجة"),
            new MoodleAssignmentLink("https://moodle.iugaza.edu.ps/mod/assign/view.php?id=311562", ""),
            new MoodleAssignmentLink("https://moodle.iugaza.edu.ps/mod/assign/view.php?id=412159&forceview=1", "اسم بديل"),
            new MoodleAssignmentLink("https://moodle.iugaza.edu.ps/mod/quiz/view.php?id=411990", "امتحان"),
            new MoodleAssignmentLink("https://example.com/mod/assign/view.php?id=99", "خارجي"),
            new MoodleAssignmentLink("https://moodle.iugaza.edu.ps/mod/assign/view.php", "بدون id")
        ], new Uri("https://moodle.iugaza.edu.ps/"));

        Assert.Equal(2, links.Count);
        Assert.Contains(links, link => link.Href == "https://moodle.iugaza.edu.ps/mod/assign/view.php?id=412159" && link.Title == "واجب برمجة");
        Assert.Contains(links, link => link.Href == "https://moodle.iugaza.edu.ps/mod/assign/view.php?id=311562" && link.Title == "");
        Assert.DoesNotContain(links, link => link.Href.Contains("/mod/quiz/", StringComparison.Ordinal));
    }

    [Fact]
    public void Assignment_dom_snapshot_accepts_missing_dates_without_failing_conversion()
    {
        const string json = """{"title":"واجب تجريبي","body":"","description":"تفاصيل","timeValues":[{"label":"Due date","value":null}],"rows":[{"label":"Submission status","value":"Nothing submitted","iso":null}]}""";

        var snapshot = MoodleAssignmentExtractor.ParsePageSnapshot(json);

        Assert.Equal("واجب تجريبي", snapshot.Title);
        Assert.Null(snapshot.TimeValues[0].Value);
        Assert.Null(snapshot.Rows[0].Iso);
        Assert.Equal("Nothing submitted", snapshot.Rows[0].Value);
    }

    [Fact]
    public void Malformed_assignment_dom_snapshot_fails_explicitly()
    {
        Assert.Throws<System.Text.Json.JsonException>(() => MoodleAssignmentExtractor.ParsePageSnapshot("{malformed"));
        Assert.Throws<System.Text.Json.JsonException>(() => MoodleAssignmentExtractor.ParsePageSnapshot("null"));
    }

    private static MoodleAssignment Assignment(string title, string url, DateTimeOffset? due = null, MoodleDateKnowledge? knowledge = null) =>
        new(title, "مساق", url, null, due, knowledge ?? (due.HasValue ? MoodleDateKnowledge.Known : MoodleDateKnowledge.NoDeadline), null, null, MoodleAssignmentStatus.NotSubmitted, null);
}
