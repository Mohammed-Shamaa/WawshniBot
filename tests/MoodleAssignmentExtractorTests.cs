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

    [Fact]
    public void Sorts_overdue_then_upcoming_and_unknown_deadlines_last()
    {
        var now = DateTimeOffset.Parse("2026-11-19T12:00:00Z");
        var assignments = new[]
        {
            Assignment("unknown", "https://x.test/3", null, MoodleDateKnowledge.Unknown),
            Assignment("later", "https://x.test/2", now.AddDays(2)),
            Assignment("overdue", "https://x.test/1", now.AddDays(-1))
        };
        Assert.Equal(new[] { "overdue", "later", "unknown" }, MoodleAssignmentExtractor.Sort(assignments, now).Select(a => a.Title));
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
