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
}
