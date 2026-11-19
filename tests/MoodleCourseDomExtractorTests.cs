using Wawshni.Bot.Moodle;
using Xunit;

namespace Wawshni.Bot.Tests;

public sealed class MoodleCourseDomExtractorTests
{
    [Fact]
    public void Normalizes_visible_course_link_names_and_removes_duplicates()
    {
        var names = MoodleCourseDomExtractor.NormalizeNames(
        [
            "  Algorithms\r\n and Data Structures ",
            "Algorithms and Data Structures",
            "  ",
            "Networks"
        ]);

        Assert.Equal(new[] { "Algorithms and Data Structures", "Networks" }, names);
    }

    [Fact]
    public void Empty_dom_results_stay_empty_without_implying_no_enrollment()
    {
        Assert.Empty(MoodleCourseDomExtractor.NormalizeNames([]));
    }

    [Fact]
    public void Courses_destination_uses_configured_moodle_base_not_blank_page_url()
    {
        var destination = MoodleCoursePageUrl.FromBaseUrl(new Uri("https://moodle.iugaza.edu.ps/"));

        Assert.Equal("https://moodle.iugaza.edu.ps/my/courses.php", destination.AbsoluteUri);
        Assert.NotEqual("about:/my/courses.php", destination.AbsoluteUri);
    }
}
