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

    private static MoodleAssignment Assignment(string title, string url, DateTimeOffset? due = null, MoodleDateKnowledge? knowledge = null) =>
        new(title, "مساق", url, null, due, knowledge ?? (due.HasValue ? MoodleDateKnowledge.Known : MoodleDateKnowledge.NoDeadline), null, null, MoodleAssignmentStatus.NotSubmitted, null);
}
