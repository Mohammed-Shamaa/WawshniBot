using System.Collections.Concurrent;
using Telegram.Bot.Types;
using Telegram.Bot.Types.Enums;
using Wawshni.Bot.Moodle;
using Wawshni.Bot.Security;

namespace Wawshni.Bot.Handlers;

public sealed class MessageHandler(
    TelegramUserAuthorization authorization,
    MoodleBrowserService moodleBrowser,
    MoodleSessionRepository sessions,
    ILogger<MessageHandler> logger)
{
    private readonly ConcurrentDictionary<long, LinkFlow> _flows = new();

    public async Task HandleAsync(
        Message message,
        Func<long, string, CancellationToken, Task> reply,
        Func<long, int, CancellationToken, Task> delete,
        CancellationToken cancellationToken)
    {
        if (message.From is null || message.Chat is null || string.IsNullOrWhiteSpace(message.Text)) return;
        if (!authorization.IsAuthorized(message.From.Id)) return;
        if (message.Chat.Type != ChatType.Private)
        {
            await SafeReply(message.Chat.Id, "استخدم البوت في محادثة خاصة لإدارة ربط Moodle.", reply, cancellationToken);
            return;
        }

        var messageText = message.Text.Trim();
        var command = messageText.Split(' ', 2)[0].Split('@', 2)[0].ToLowerInvariant();
        if (command == "/cancel")
        {
            _flows.TryRemove(message.From.Id, out _);
            await SafeReply(message.Chat.Id, "تم إلغاء عملية الربط وحذف بياناتها المؤقتة من ذاكرة البوت.", reply, cancellationToken);
            return;
        }
        if (command == "/link" || command == "/connect_moodle")
        {
            _flows[message.From.Id] = new LinkFlow(LinkStep.StudentId, null, DateTimeOffset.UtcNow);
            await SafeReply(message.Chat.Id, "لنبدأ ربط حساب Moodle. أرسل رقمك الجامعي في الرسالة التالية. أرسل كلمة المرور فقط بعد أن يطلبها البوت، وسأحاول حذف رسالتها فوراً.", reply, cancellationToken);
            return;
        }
        if (command == "/logout")
        {
            _flows.TryRemove(message.From.Id, out _);
            try
            {
                await sessions.DeleteAsync(message.From.Id, cancellationToken);
                await SafeReply(message.Chat.Id, "تم فصل حساب Moodle وحذف الجلسة المخزنة.", reply, cancellationToken);
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                logger.LogWarning("Moodle logout failed ({ExceptionType}).", exception.GetType().Name);
                await SafeReply(message.Chat.Id, "تعذر حذف جلسة Moodle من قاعدة البيانات الآن. حاول مرة أخرى لاحقاً.", reply, cancellationToken);
            }
            return;
        }

        if (_flows.TryGetValue(message.From.Id, out var flow))
        {
            if (DateTimeOffset.UtcNow - flow.StartedAt > TimeSpan.FromMinutes(10))
            {
                _flows.TryRemove(message.From.Id, out _);
                await SafeReply(message.Chat.Id, "انتهت مهلة الربط. أرسل /link للبدء من جديد.", reply, cancellationToken);
                return;
            }

            if (flow.Step == LinkStep.StudentId)
            {
                if (messageText.Length is < 4 or > 32 || !messageText.All(char.IsLetterOrDigit))
                {
                    await SafeReply(message.Chat.Id, "رقم الطالب غير صالح. أرسله بأحرف أو أرقام فقط، أو أرسل /cancel.", reply, cancellationToken);
                    return;
                }
                _flows[message.From.Id] = flow with { Step = LinkStep.Password, StudentId = messageText };
                await SafeReply(message.Chat.Id, "تم استلام الرقم الجامعي. أرسل كلمة مرور Moodle الآن كرسالة واحدة. سيحاول البوت حذف الرسالة فور استلامها.", reply, cancellationToken);
                return;
            }

            _flows.TryRemove(message.From.Id, out _);
            var password = messageText;
            messageText = string.Empty;
            message.Text = null;
            var deleted = false;
            try
            {
                try { await delete(message.Chat.Id, message.MessageId, cancellationToken); deleted = true; }
                catch (Exception exception) when (exception is not OperationCanceledException)
                {
                    logger.LogWarning("Telegram could not delete the Moodle password message ({ExceptionType}).", exception.GetType().Name);
                }

                if (!deleted)
                    await SafeReply(message.Chat.Id, "لم أتمكن من حذف رسالة كلمة المرور. احذفها يدوياً من المحادثة الآن. حذف الرسالة لا يضمن إزالتها من كل أنظمة Telegram أو النسخ الاحتياطية.", reply, cancellationToken);

                var result = await moodleBrowser.LinkAsync(message.From.Id, flow.StudentId!, password, cancellationToken);
                await SafeReply(message.Chat.Id, FormatLinkResult(result), reply, cancellationToken);
            }
            finally { password = string.Empty; }
            return;
        }

        string response;
        switch (command)
        {
            case "/status":
                var status = await moodleBrowser.GetStatusAsync(message.From.Id, cancellationToken);
                if (status.Expired)
                {
                    try { await sessions.DeleteAsync(message.From.Id, cancellationToken); }
                    catch (Exception exception) when (exception is not OperationCanceledException)
                    { logger.LogWarning("Could not remove an expired Moodle session ({ExceptionType}).", exception.GetType().Name); }
                }
                response = status.Connected
                    ? "حساب Moodle مربوط والجلسة صالحة حالياً. أرسل /my_courses لعرض المساقات."
                    : status.Expired ? "انتهت جلسة Moodle أو تعذر التحقق منها. أرسل /link لإعادة الربط." : "حساب Moodle غير مربوط. أرسل /link للبدء.";
                break;
            case "/my_courses":
            case "/courses":
                var courseResult = await moodleBrowser.GetCoursesAsync(message.From.Id, cancellationToken);
                response = FormatCourses(courseResult);
                break;
            case "/start":
                response = "أهلاً بك في WawshniBot. أرسل /link لربط حساب Moodle، أو /help لعرض الأوامر.";
                break;
            case "/help":
                response = "الأوامر المتاحة:\n/link — ربط حساب Moodle\n/status — حالة الربط\n/my_courses — مساقاتي الفعلية\n/logout — فصل الحساب وحذف الجلسة\n/cancel — إلغاء عملية الربط الحالية";
                break;
            case "/upcoming": response = "أرسل /link أولاً؛ استرجاع المواعيد غير متاح بعد."; break;
            case "/latest": response = "أرسل /link أولاً؛ لا توجد مزامنة للمستجدات بعد."; break;
            case "/scan": response = "المزامنة غير متاحة في هذه المرحلة."; break;
            case "/my_assignments":
            case "/assignments": response = "استرجاع الواجبات غير متاح بعد."; break;
            case "/my_quizzes":
            case "/quizzes": response = "استرجاع الاختبارات غير متاح بعد."; break;
            case "/updates": response = "مزامنة مستجدات المساقات غير متاحة بعد."; break;
            case "/report": response = "التقارير ستتوفر في مرحلة لاحقة."; break;
            case var _ when messageText.StartsWith('/'): response = "لم أتعرف على هذا الأمر. أرسل /help."; break;
            default: response = "وصلتني رسالتك. أرسل /help لعرض الأوامر المتاحة."; break;
        }
        await SafeReply(message.Chat.Id, response, reply, cancellationToken);
    }

    private async Task SafeReply(long chatId, string text, Func<long, string, CancellationToken, Task> reply, CancellationToken cancellationToken)
    {
        try { await reply(chatId, text, cancellationToken); }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            logger.LogError("Failed to reply to Telegram ({ExceptionType}).", exception.GetType().Name);
        }
    }

    private static string FormatLinkResult(MoodleLinkResult result) => result.Kind switch
    {
        MoodleLinkResultKind.Success when result.CoursesRetrieved => result.Courses.Count == 0
            ? "تم التحقق من حساب Moodle وحفظ الجلسة المشفّرة. لم تظهر مساقات مسجلة حالياً. أرسل /my_courses للمحاولة لاحقاً."
            : "تم ربط حساب Moodle وحفظ الجلسة المشفّرة. مساقاتك المسجلة:\n• " + string.Join("\n• ", result.Courses),
        MoodleLinkResultKind.Success => "تم التحقق من الحساب وحفظ الجلسة، لكن تعذر جلب المساقات الآن. أرسل /my_courses لإعادة المحاولة.",
        MoodleLinkResultKind.InvalidCredentials => "رفض Moodle تسجيل الدخول. تحقق من الرقم الجامعي وكلمة المرور ثم أرسل /link مجدداً.",
        MoodleLinkResultKind.ChallengeRequired => "يتطلب تسجيل الدخول تحققاً إضافياً (CAPTCHA أو MFA). لن أتجاوز هذا التحقق؛ أكمل تسجيل الدخول يدوياً ثم حاول مجدداً.",
        MoodleLinkResultKind.IdentityUnverified => "وصل تسجيل الدخول إلى صفحة Moodle، لكن تعذر التأكد من أن الحساب يطابق رقمك الجامعي. لم أحفظ الجلسة.",
        MoodleLinkResultKind.NotConfigured => "إعداد حفظ جلسة Moodle غير مكتمل. على مسؤول البوت ضبط Moodle__SessionEncryptionKey في Render أولاً.",
        MoodleLinkResultKind.Expired => "لا توجد جلسة Moodle صالحة. أرسل /link لربط الحساب.",
        _ => "تعذر إكمال تسجيل الدخول إلى Moodle حالياً. لم تُحفظ كلمة المرور. تحقق من توفر المنصة وحاول لاحقاً."
    };

    private static string FormatCourses(MoodleLinkResult result) => result.Kind switch
    {
        MoodleLinkResultKind.Expired => "الجلسة غير موجودة أو انتهت. أرسل /link لإعادة الربط.",
        MoodleLinkResultKind.Success when result.CoursesRetrieved && result.Courses.Count == 0 => "لم تظهر مساقات مسجلة في حسابك حالياً.",
        MoodleLinkResultKind.Success when result.CoursesRetrieved => "مساقاتك المسجلة في Moodle:\n• " + string.Join("\n• ", result.Courses),
        _ => "تعذر جلب المساقات من Moodle. أرسل /status للتحقق من الجلسة أو /link لإعادة الربط."
    };

    private enum LinkStep { StudentId, Password }
    private sealed record LinkFlow(LinkStep Step, string? StudentId, DateTimeOffset StartedAt);
}
