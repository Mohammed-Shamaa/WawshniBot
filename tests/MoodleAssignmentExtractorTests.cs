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

    [Fact]
    public void Cutoff_is_not_used_as_due_date_and_submission_time_is_kept_separate()
    {
        var result = MoodleAssignmentExtractor.ParseAssignmentPage(new MoodleAssignmentPageSnapshot
        {
            Title = "Assignment page",
            Rows =
            [
                new() { Label = "تاريخ التسليم", Value = "25 نوفمبر 2026، 11:59 م", Iso = "2026-11-25T21:59:00Z" },
                new() { Label = "آخر موعد للتسليم", Value = "27 نوفمبر 2026، 11:59 م", Iso = "2026-11-27T21:59:00Z" },
                new() { Label = "Last modified (submission)", Value = "24 November 2026, 1:15 PM", Iso = "2026-11-24T11:15:00Z" },
                new() { Label = "Submission status", Value = "Submitted for grading" }
            ]
        }, "https://moodle.iugaza.edu.ps/mod/assign/view.php?id=212", "Research", "Asia/Gaza");

        Assert.NotNull(result);
        Assert.Equal(DateTimeOffset.Parse("2026-11-25T21:59:00Z"), result.DueAt);
        Assert.Equal(DateTimeOffset.Parse("2026-11-27T21:59:00Z"), result.CutoffAt);
        Assert.Equal(DateTimeOffset.Parse("2026-11-24T11:15:00Z"), result.SubmittedAt);
    }

    [Fact]
    public void Submission_timestamp_never_becomes_due_date()
    {
        var result = MoodleAssignmentExtractor.ParseAssignmentPage(new MoodleAssignmentPageSnapshot
        {
            Title = "Submitted assignment",
            Rows = [new() { Label = "Last modified (submission)", Value = "24 November 2026, 1:15 PM", Iso = "2026-11-24T11:15:00Z" }]
        }, "https://moodle.iugaza.edu.ps/mod/assign/view.php?id=213", "Research");

        Assert.NotNull(result);
        Assert.Null(result.DueAt);
        Assert.Equal(MoodleDateKnowledge.Unknown, result.DueDateKnowledge);
        Assert.Equal(DateTimeOffset.Parse("2026-11-24T11:15:00Z"), result.SubmittedAt);
    }

    [Fact]
    public void Parses_the_due_and_submission_rows_shown_on_the_supplied_moodle_assignment_screenshots()
    {
        var notSubmitted = MoodleAssignmentExtractor.ParseAssignmentPage(new MoodleAssignmentPageSnapshot
        {
            Title = "Assignment #1",
            Rows =
            [
                new() { Label = "Due", Value = "Tuesday, 1 December 2026, 11:59 PM", Iso = "2026-12-01T21:59:00Z" },
                new() { Label = "Submission status", Value = "No submissions have been made yet" },
                new() { Label = "Grading status", Value = "Not graded" },
                new() { Label = "Time remaining", Value = "8 days 12 hours remaining" },
                new() { Label = "Last modified", Value = "-" }
            ]
        }, "https://moodle.iugaza.edu.ps/mod/assign/view.php?id=411990", "برمجة الألعاب للأجهزة الذكية");
        var submitted = MoodleAssignmentExtractor.ParseAssignmentPage(new MoodleAssignmentPageSnapshot
        {
            Title = "Assignment 1 - Search about Alexa Smart Home",
            Rows =
            [
                new() { Label = "Due", Value = "Friday, 23 October 2026, 11:59 PM", Iso = "2026-10-23T21:59:00Z" },
                new() { Label = "Submission status", Value = "Submitted for grading" },
                new() { Label = "Grading status", Value = "Not graded" },
                new() { Label = "Time remaining", Value = "Assignment was submitted 5 days 8 hours early" },
                new() { Label = "Last modified", Value = "Sunday, 18 October 2026, 3:10 PM", Iso = "2026-10-18T13:10:00Z" }
            ]
        }, "https://moodle.iugaza.edu.ps/mod/assign/view.php?id=311562", "الحوسبة المنتشرة | م. محمد أحمد الأغا");

        Assert.NotNull(notSubmitted);
        Assert.Equal(DateTimeOffset.Parse("2026-12-01T21:59:00Z"), notSubmitted.DueAt);
        Assert.Equal(MoodleAssignmentStatus.NotSubmitted, notSubmitted.Status);
        Assert.NotNull(submitted);
        Assert.Equal(MoodleAssignmentStatus.Submitted, submitted.Status);
        Assert.Equal(DateTimeOffset.Parse("2026-10-18T13:10:00Z"), submitted.SubmittedAt);
        Assert.Equal(DateTimeOffset.Parse("2026-10-23T21:59:00Z"), submitted.DueAt);
    }

    [Theory]
    [InlineData("Submission status", "Submitted for grading", MoodleAssignmentStatus.Submitted)]
    [InlineData("حالة التسليم", "تم التسليم", MoodleAssignmentStatus.Submitted)]
    [InlineData("Submission status Submitted late", "", MoodleAssignmentStatus.SubmittedLate)]
    public void Reads_submission_state_from_visible_page_text_when_theme_does_not_expose_table_rows(
        string statusLine, string nextLine, MoodleAssignmentStatus expected)
    {
        var lines = string.IsNullOrEmpty(nextLine) ? new List<string> { statusLine } : new List<string> { statusLine, nextLine };
        var result = MoodleAssignmentExtractor.ParseAssignmentPage(new MoodleAssignmentPageSnapshot
        {
            Title = "Assignment",
            BodyLines = lines
        }, "https://moodle.iugaza.edu.ps/mod/assign/view.php?id=81234", "الحوسبة المنتشرة - عملي");

        Assert.NotNull(result);
        Assert.Equal(expected, result.Status);
    }

    [Fact]
    public void Falls_back_to_visible_text_when_a_matching_table_row_has_an_empty_value()
    {
        var result = MoodleAssignmentExtractor.ParseAssignmentPage(new MoodleAssignmentPageSnapshot
        {
            Title = "Assignment 1",
            Rows =
            [
                new() { Label = "Due", Value = "" },
                new() { Label = "Submission status", Value = "" }
            ],
            BodyLines =
            [
                "Due",
                "Tuesday, 1 December 2026, 11:59 PM",
                "Submission status",
                "Submitted for grading"
            ]
        }, "https://moodle.iugaza.edu.ps/mod/assign/view.php?id=81235", "نظم التشغيل - عملي");

        Assert.NotNull(result);
        Assert.Equal(DateTimeOffset.Parse("2026-12-01T21:59:00Z"), result.DueAt);
        Assert.Equal(MoodleDateKnowledge.Known, result.DueDateKnowledge);
        Assert.Equal(MoodleAssignmentStatus.Submitted, result.Status);
    }

    [Fact]
    public void Prefers_recognized_values_when_the_dom_contains_duplicate_assignment_rows()
    {
        var result = MoodleAssignmentExtractor.ParseAssignmentPage(new MoodleAssignmentPageSnapshot
        {
            Title = "Assignment",
            Rows =
            [
                new() { Label = "Due", Value = "-" },
                new() { Label = "Due date", Value = "Tuesday, 1 December 2026, 11:59 PM", Iso = "2026-12-01T21:59:00Z" },
                new() { Label = "Submission status", Value = "-" },
                new() { Label = "Submission status", Value = "Submitted for grading" }
            ]
        }, "https://moodle.iugaza.edu.ps/mod/assign/view.php?id=81236", "نظم التشغيل - عملي");

        Assert.NotNull(result);
        Assert.Equal(DateTimeOffset.Parse("2026-12-01T21:59:00Z"), result.DueAt);
        Assert.Equal(MoodleAssignmentStatus.Submitted, result.Status);
    }

    [Theory]
    [InlineData("25 November 2026, 11:59 PM", "Asia/Gaza", 2)]
    [InlineData("٢٥ نوفمبر ٢٠٢٦، ١١:٥٩ م", "Asia/Gaza", 2)]
    [InlineData("25 November 2026, 11:59 PM", "UTC", 0)]
    public void Localized_wall_clock_dates_use_the_configured_moodle_timezone(string value, string timezone, int expectedOffsetHours)
    {
        var parsed = MoodleAssignmentExtractor.ParseDueDate(value, timeZoneId: timezone);
        Assert.Equal(MoodleDateKnowledge.Known, parsed.Knowledge);
        Assert.Equal(expectedOffsetHours, parsed.Value!.Value.Offset.TotalHours);
        Assert.Equal(25, parsed.Value.Value.Day);
        Assert.Equal(23, parsed.Value.Value.Hour);
        Assert.Equal(59, parsed.Value.Value.Minute);
    }

    [Fact]
    public void Moodle_zero_timestamp_means_unconfigured_deadline_not_unix_epoch()
    {
        Assert.Equal(MoodleDateKnowledge.NoDeadline, MoodleAssignmentExtractor.ParseDueDate(null, "0").Knowledge);
        Assert.Null(MoodleAssignmentExtractor.ParseDate(null, "0"));
    }

    [Theory]
    [InlineData("Nothing submitted for this assignment", MoodleAssignmentStatus.NotSubmitted)]
    [InlineData("Draft (not submitted)", MoodleAssignmentStatus.Draft)]
    [InlineData("Submitted for grading", MoodleAssignmentStatus.Submitted)]
    [InlineData("Submitted late", MoodleAssignmentStatus.SubmittedLate)]
    [InlineData("Graded", MoodleAssignmentStatus.Graded)]
    [InlineData("Not graded", MoodleAssignmentStatus.Unavailable)]
    [InlineData("لم يتم التقييم", MoodleAssignmentStatus.Unavailable)]
    [InlineData("لم يتم تسليم", MoodleAssignmentStatus.NotSubmitted)]
    [InlineData("تم تسليم الواجب", MoodleAssignmentStatus.Submitted)]
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
    public void Sorts_submitted_then_nearest_future_then_overdue_then_unknown_then_no_deadline()
    {
        var now = DateTimeOffset.Parse("2026-11-25T12:00:00+02:00");
        var assignments = new[]
        {
            Assignment("no deadline", "https://x.test/1", null, MoodleDateKnowledge.NoDeadline),
            Assignment("overdue", "https://x.test/2", now.AddDays(-1)) with { Status = MoodleAssignmentStatus.NotSubmitted },
            Assignment("far future", "https://x.test/3", now.AddDays(4)),
            Assignment("submitted", "https://x.test/4", now.AddDays(-2)) with { Status = MoodleAssignmentStatus.Submitted },
            Assignment("unknown", "https://x.test/5", null, MoodleDateKnowledge.ExtractionFailed),
            Assignment("near future", "https://x.test/6", now.AddHours(1))
        };

        Assert.Equal(new[] { "submitted", "near future", "far future", "overdue", "unknown", "no deadline" },
            MoodleAssignmentExtractor.Sort(assignments, now).Select(a => a.Title));
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
