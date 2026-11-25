using System.Text.RegularExpressions;

namespace Wawshni.Bot.Services;

/// <summary>Maps clear, common user requests to existing safe Telegram commands without an extra AI call.</summary>
public static partial class NaturalLanguageCommandRouter
{
    public static string? MapToCommand(string message)
    {
        var text = Normalize(message);
        if (string.IsNullOrWhiteSpace(text) || IsNegated(text)) return null;
        if (IsAskingAboutCommands(text)) return null;
        if (IsAskingForLearningHelp(text)) return null;

        if (ContainsAny(text, "كل الامتحانات", "جميع الامتحانات", "كل امتحانات", "جميع امتحانات", "كل الاختبارات", "جميع الاختبارات", "كل كويزات", "كل الكويزات", "كل امتحاناتي", "كل اختباراتي", "all exams", "all the exams", "all my exams", "all quizzes", "all my quizzes", "all tests"))
            return "/all_exams";

        if (IsLinkRequest(text)) return "/link";
        if (IsAssignmentRequest(text)) return "/my_assignments";
        if (IsExamRequest(text)) return "/my_exams";
        return null;
    }

    private static bool IsLinkRequest(string text) => ContainsAny(text,
        "اربط المودل", "اربط حساب", "ربط المودل", "ربط حساب", "اوصل المودل", "بدي اوصل المودل", "وصل حساب", "بدي اربط", "بدي ربط", "اريد اربط", "اريد ربط", "حاب اربط", "حابب اربط", "كيف اربط", "كيف اوصل",
        "connect my moodle", "connect moodle", "want to connect moodle", "want to link moodle", "link to moodle", "link my moodle", "link moodle", "link my account", "connect my account", "sign in to moodle", "log in to moodle", "login to moodle");

    private static bool IsAssignmentRequest(string text) =>
        IsStandaloneRequest(text, "واجباتي", "شو واجباتي", "اسايمنتاتي", "my assignments", "my homework")
        || (ContainsAny(text, "واجب", "الواجب", "واجبات", "الواجبات", "اسايمنت", "assignments", "assignment", "homework", "coursework")
            && IsRetrievalRequest(text));

    private static bool IsExamRequest(string text) =>
        IsStandaloneRequest(text, "امتحاناتي", "شو امتحاناتي", "اختباراتي", "كويزاتي", "my exams", "my quizzes", "my tests")
        || (ContainsAny(text, "امتحان", "الامتحان", "امتحانات", "الامتحانات", "اختبار", "الاختبار", "اختبارات", "الاختبارات", "كويزات", "كويز", "exams", "exam", "quizzes", "quiz", "tests", "test")
            && IsRetrievalRequest(text));

    private static bool IsRetrievalRequest(string text) => ContainsAny(text,
        "بدي", "اريد", "اعرض", "ورجيني", "وريني", "جيب", "جيبلي", "اعطيني", "اعطني", "هات", "طلع", "شو عندي", "متى", "هل في", "وين", "قائمة", "المطلوب مني", "اللي علي", "اعرف", "معرفة",
        "show", "get", "fetch", "list", "view", "what", "when", "find", "need", "please", "do i have", "send");

    private static bool IsStandaloneRequest(string text, params string[] phrases) =>
        phrases.Contains(text.Trim(' ', '.', ',', '؟', '?', '!', '،'), StringComparer.Ordinal);

    private static bool IsAskingAboutCommands(string text) => ContainsAny(text,
        "شو الاوامر", "ما هي الاوامر", "الاوامر اللي", "امر عرض", "اي امر", "اوامر البوت", "commands", "which command", "what command", "how to use");

    private static bool IsAskingForLearningHelp(string text) => ContainsAny(text,
        "اشرح", "شرح", "ساعدني", "ساعدني في", "كيف احل", "كيف ادرس", "فهمني", "حل الواجب", "اشرحلي", "explain", "help me with", "solve", "study for");

    private static bool IsNegated(string text) => ContainsAny(text,
        "ما بدي", "مش بدي", "لا اريد", "مش حاب", "مش حابة", "ما بديش", "لا تربط", "لا تجيب", "لا تعرض",
        "don't", "dont", "do not", "not now", "stop", "cancel");

    private static bool ContainsAny(string text, params string[] phrases) =>
        phrases.Any(phrase => text.Contains(phrase, StringComparison.Ordinal));

    private static string Normalize(string value)
    {
        var normalized = value.Normalize().ToLowerInvariant();
        normalized = normalized.Replace('أ', 'ا').Replace('إ', 'ا').Replace('آ', 'ا');
        normalized = ArabicDiacriticsRegex().Replace(normalized, "");
        normalized = WhitespaceRegex().Replace(normalized, " ").Trim();
        return normalized;
    }

    [GeneratedRegex("[\\u064B-\\u065F\\u0670\\u0640]")]
    private static partial Regex ArabicDiacriticsRegex();

    [GeneratedRegex("\\s+")]
    private static partial Regex WhitespaceRegex();
}
