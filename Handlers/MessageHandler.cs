using Telegram.Bot.Types;
using Wawshni.Bot.Security;
using Wawshni.Bot.Moodle;
namespace Wawshni.Bot.Handlers;
public sealed class MessageHandler(TelegramUserAuthorization authorization, MoodleConnectionStatus moodle, ILogger<MessageHandler> logger)
{
    public async Task HandleAsync(Message message, Func<long, string, CancellationToken, Task> reply, CancellationToken cancellationToken)
    {
        if (message.From is null || message.Chat is null || string.IsNullOrWhiteSpace(message.Text)) return;
        if (!authorization.IsAuthorized(message.From.Id)) return;
        var text = message.Text.Trim();
        var command = text.Split(' ', 2)[0].Split('@', 2)[0].ToLowerInvariant();
        var response = command switch
        {
            "/start" => "أهلاً! أنا WawshniBot، مساعدك الجامعي. أستطيع حالياً استقبال رسائلك وعرض الأوامر، وسيُضاف ربط Moodle لاحقاً.",
            "/help" => "الأوامر المتاحة:\n/start — بدء الاستخدام\n/help — عرض المساعدة\n/connect_moodle — إعداد ربط Moodle (غير متاح بعد)\n/my_courses — مساقاتي\n/my_assignments — واجباتي\n/my_quizzes — اختباراتي\n/upcoming — المواعيد القادمة\n/latest — آخر المستجدات\n/scan — مزامنة يدوية (غير متاحة بعد)\n/status — حالة الخدمات\n/courses /updates /quizzes /assignments /report — معلومات Moodle\n\nمثال: متى أقرب اختبار عندي؟ (يتطلب ربط Moodle)",
            "/connect_moodle" => $"لم يُنفذ تسجيل الدخول إلى Moodle بعد. لا ترسل كلمة المرور هنا. رابط المنصة: {moodle.BaseUrl}",
            "/upcoming" => "لا يمكن التحقق من المواعيد القادمة قبل ربط Moodle.",
            "/latest" => "لا توجد مزامنة Moodle حتى الآن، لذلك لا أستطيع تأكيد محتوى جديد.",
            "/scan" => "المسح اليدوي غير متاح قبل تنفيذ تسجيل الدخول ومزامنة Moodle.",
            "/status" => $"خدمة Telegram تستقبل الرسائل. حالة Moodle: {(moodle.IsConnected ? "متصل" : "غير مربوط")}. آخر مسح ناجح: غير متاح. عدد المساقات: غير متاح.",
            "/courses" or "/my_courses" => "ربط Moodle غير متاح بعد، لذلك لا أستطيع عرض مساقاتك حالياً.",
            "/updates" => "مزامنة Moodle غير متاحة بعد، ولا توجد لدي تحديثات حقيقية لعرضها.",
            "/quizzes" or "/my_quizzes" => "استرجاع اختبارات Moodle غير متاح بعد.",
            "/assignments" or "/my_assignments" => "استرجاع واجبات Moodle غير متاح بعد.",
            "/report" => "تقارير المساقات ستتوفر بعد تنفيذ ربط Moodle.",
            _ when text.StartsWith('/') => "لم أتعرف على هذا الأمر. أرسل /help لعرض الأوامر المتاحة.",
            _ => "وصلتني رسالتك. ربط Moodle غير متاح بعد، لذلك لا أستطيع جلب معلومات المساقات أو الاختبارات حالياً. ستُتاح هذه الطلبات بعد ربط المنصة."
        };
        try { await reply(message.Chat.Id, response, cancellationToken); }
        catch (Exception ex) when (ex is not OperationCanceledException) { logger.LogError(ex, "Failed to reply to Telegram message {MessageId}.", message.MessageId); throw; }
    }
}
