using Wawshni.Bot.Moodle;
using Xunit;

namespace Wawshni.Bot.Tests;

public sealed class MoodleCourseDomExtractorTests
{
    [Fact]
    public void Removes_course_label_and_deduplicates_whitespace_and_label_variants()
    {
        var names = MoodleCourseDomExtractor.NormalizeNames(
        [
            "  الحوسبة المنتشرة   - عملي ",
            "اسم المقرر الحوسبة المنتشرة - عملي",
            "اسم المقرر: الحوسبة المنتشرة - عملي",
            "اسم المقرر\r\n الحوسبة المنتشرة - عملي",
            "الحوسبة المنتشرة | م. محمد أحمد الأغا",
            "اسم المقرر الحوسبة المنتشرة | م. محمد أحمد الأغا",
            "  ",
            "Networks",
            " networks "
        ]);

        Assert.Equal(new[]
        {
            "الحوسبة المنتشرة - عملي",
            "الحوسبة المنتشرة | م. محمد أحمد الأغا",
            "Networks"
        }, names);
    }

    [Fact]
    public void Keeps_similar_but_distinct_course_names_and_does_not_remove_embedded_phrase()
    {
        var names = MoodleCourseDomExtractor.NormalizeNames(
        [
            "برمجة تطبيقات الأجهزة الذكية (3)",
            "اسم المقرر برمجة تطبيقات الأجهزة الذكية (3) -عملي",
            "مشروع تخرج",
            "مشروع تخرج - تصميم",
            "مقدمة: اسم المقرر وكيفية التسجيل"
        ]);

        Assert.Equal(new[]
        {
            "برمجة تطبيقات الأجهزة الذكية (3)",
            "برمجة تطبيقات الأجهزة الذكية (3) -عملي",
            "مشروع تخرج",
            "مشروع تخرج - تصميم",
            "مقدمة: اسم المقرر وكيفية التسجيل"
        }, names);
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

    [Fact]
    public void Course_link_discovery_keeps_valid_urls_deduplicates_by_course_id_and_does_not_require_link_text()
    {
        var links = MoodleCourseDomExtractor.NormalizeLinks(
        [
            new MoodleCourseLink("اسم المقرر الحوسبة المنتشرة", "https://moodle.iugaza.edu.ps/course/view.php?id=42"),
            new MoodleCourseLink("", "https://moodle.iugaza.edu.ps/course/view.php?id=42&section=2"),
            new MoodleCourseLink("برمجة الألعاب", "https://moodle.iugaza.edu.ps/course/view.php?id=43"),
            new MoodleCourseLink("خارجي", "https://example.com/course/view.php?id=44"),
            new MoodleCourseLink("بدون معرف", "https://moodle.iugaza.edu.ps/course/view.php")
        ], new Uri("https://moodle.iugaza.edu.ps/"));

        Assert.Equal(2, links.Count);
        Assert.Contains(links, link => link.Name == "الحوسبة المنتشرة" && link.Href.Contains("id=42", StringComparison.Ordinal));
        Assert.Contains(links, link => link.Name == "برمجة الألعاب" && link.Href.Contains("id=43", StringComparison.Ordinal));
    }

    [Fact]
    public void Course_link_discovery_returns_empty_for_malformed_or_untrusted_links()
    {
        var links = MoodleCourseDomExtractor.NormalizeLinks(
        [
            new MoodleCourseLink("", "not a url"),
            new MoodleCourseLink("خارج المنصة", "https://example.com/course/view.php?id=1"),
            new MoodleCourseLink("بلا معرف", "https://moodle.iugaza.edu.ps/course/view.php")
        ], new Uri("https://moodle.iugaza.edu.ps/"));

        Assert.Empty(links);
    }
}
