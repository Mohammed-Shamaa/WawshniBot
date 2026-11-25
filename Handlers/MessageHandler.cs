using System.Collections.Concurrent;
using Telegram.Bot.Types;
using Telegram.Bot.Types.Enums;
using Wawshni.Bot.Moodle;
using Wawshni.Bot.Security;
using Wawshni.Bot.Users;
using Wawshni.Bot.Services;
using Microsoft.Extensions.Options;
using Telegram.Bot.Types.ReplyMarkups;

namespace Wawshni.Bot.Handlers;

public sealed class MessageHandler(
    TelegramUserAuthorization authorization,
    TelegramUserRepository users,
    IOptions<MoodleOptions> moodleOptions,
    MoodleBrowserService moodleBrowser,
    GeminiChatService geminiChat,
    MoodleSessionRepository sessions,
    ILogger<MessageHandler> logger)
{
    private const string RejectedAccessNotice = "هههههههههههههههههههههههههههههههه يا هلفوت فش استخدام للبوت بكفي , روح افتح المودل\nحسابك حالياً غير معتمد لاستخدام وشوشني ارسل /my_access لمعرفة الحالة، وإذا عندك استفسار أو بتعتقد أنه صار خطأ، تواصل مع المهندس محمد: mohammedmomen177@gmail.com";
    private readonly ConcurrentDictionary<long, LinkFlow> _flows = new();
    private readonly ConcurrentDictionary<long, AssignmentCoursePicker> _assignmentCoursePickers = new();
    private readonly ConcurrentDictionary<long, AssignmentCoursePicker> _examCoursePickers = new();

    public async Task HandleAsync(
        Message message,
        Func<long, string, CancellationToken, Task> reply,
        Func<long, int, CancellationToken, Task> delete,
        Func<long, string, Guid, CancellationToken, Task> adminRequest,
        Func<long, string, IReadOnlyList<(string Text, string CallbackData)>, CancellationToken, Task> coursePicker,
        CancellationToken cancellationToken)
    {
        if (message.From is null || message.Chat is null) return;
        var userId = message.From.Id;
        if (await authorization.IsBlockedAsync(userId, cancellationToken))
        {
            if (message.Chat.Type == ChatType.Private)
                await SafeReply(message.Chat.Id, "تم تعليق إمكانية استخدامك للبوت. تواصل مع مسؤول وشوشني إذا كنت تعتقد أن هذا خطأ.", reply, cancellationToken);
            return;
        }
        try { await users.RecordActivityAsync(userId, cancellationToken); }
        catch (Exception exception) when (exception is not OperationCanceledException)
        { logger.LogDebug("Could not update dashboard last-activity time ({ExceptionType}).", exception.GetType().Name); }
        var isAdmin = await authorization.IsAdminAsync(userId, cancellationToken);
        if (string.IsNullOrWhiteSpace(message.Text))
        {
            if (!isAdmin)
            {
                var status = await authorization.GetStatusAsync(userId, cancellationToken);
                await SafeReply(message.Chat.Id, status is null
                    ? "لإنشاء طلب انضمامك إلى وشوشني، أرسل /start أولاً."
                    : status == TelegramRegistrationStatus.Pending
                        ? "طلبك قيد مراجعة المسؤول. أرسل /my_access لمعرفة الحالة."
                        : status == TelegramRegistrationStatus.Rejected
                            ? RejectedAccessNotice
                            : "أرسل /help لعرض الأوامر المتاحة.", reply, cancellationToken);
            }
            return;
        }
        if (message.Chat.Type != ChatType.Private)
        {
            await SafeReply(message.Chat.Id, "استخدم البوت في محادثة خاصة لإدارة حسابك.", reply, cancellationToken);
            return;
        }

        var messageText = message.Text.Trim();
        var command = messageText.Split(' ', 2)[0].Split('@', 2)[0].ToLowerInvariant();
        if (isAdmin)
        {
            if (await HandleAdminCommandAsync(command, messageText, userId, message.Chat.Id, reply, adminRequest, cancellationToken))
                return;
        }

        if (command == "/start")
        {
            var registration = await users.RegisterPendingAsync(userId, message.From.FirstName, message.From.LastName, message.From.Username, cancellationToken);
            switch (registration.User.Status)
            {
                case TelegramRegistrationStatus.Pending:
                    await SafeReply(message.Chat.Id, registration.Created
                        ? "أهلاً بك في وشوشني! 🎓\n\nمساعدك الجامعي الذكي المتصل بمنصة Moodle.\n\nتم استلام طلب انضمامك بنجاح، وطلبك الآن قيد مراجعة المسؤول.\n\nسيتم إشعارك فور اتخاذ قرار بشأن طلبك.\n\nشكراً لاهتمامك باستخدام وشوشني ❤️"
                        : "طلبك قيد مراجعة المسؤول.", reply, cancellationToken);
                    if (registration.Created && authorization.AdministratorId is long adminId)
                    {
                        var name = string.Join(" ", new[] { registration.User.FirstName, registration.User.LastName }.Where(s => !string.IsNullOrWhiteSpace(s)));
                        if (string.IsNullOrWhiteSpace(name)) name = "غير متاح";
                        var username = string.IsNullOrWhiteSpace(registration.User.Username) ? "غير متاح" : "@" + registration.User.Username;
                        var notification = $"🔔 طلب انضمام جديد إلى وشوشني\n\n👤 الاسم: {name}\n🔹 Username: {username}\n🆔 Telegram ID: {userId}\n📅 تاريخ الطلب: {registration.User.RegisteredAt:yyyy-MM-dd HH:mm} UTC\n\nيرجى مراجعة الطلب واتخاذ القرار.";
                        try { await adminRequest(adminId, notification, registration.User.RequestId, cancellationToken); }
                        catch (Exception exception) when (exception is not OperationCanceledException)
                        { logger.LogWarning("Could not notify administrator about pending registration ({ExceptionType}); request remains stored.", exception.GetType().Name); }
                    }
                    return;
                case TelegramRegistrationStatus.Approved:
                    await SafeReply(message.Chat.Id, "أهلاً بك في وشوشني. أرسل /link لربط حساب Moodle، أو /help لعرض الأوامر.", reply, cancellationToken);
                    return;
                case TelegramRegistrationStatus.Rejected:
                    await SafeReply(message.Chat.Id, RejectedAccessNotice, reply, cancellationToken);
                    return;
            }
        }

        var registrationStatus = await authorization.GetStatusAsync(userId, cancellationToken);
        if (command == "/my_access")
        {
            await SafeReply(message.Chat.Id, AccessStatusText(registrationStatus), reply, cancellationToken);
            return;
        }
        if (command == "/help")
        {
            var help = registrationStatus == TelegramRegistrationStatus.Approved
                ? "الأوامر المتاحة:\n/link — ربط حساب Moodle\n/status — حالة الربط\n/my_courses — مساقاتي الفعلية\n/my_assignments — اختر مساقاً لعرض واجباته ومواعيده\n/my_exams — اختر مساقاً لعرض اختباراته\n/all_exams — عرض اختبارات جميع المساقات\n/reset_chat — مسح سياق المحادثة مع المساعد\n/logout — فصل الحساب وحذف الجلسة\n/cancel — إلغاء العملية الحالية\n/my_access — حالة طلب الانضمام\n\nلإجابة الأسئلة العادية، اكتب سؤالك مباشرة. لا ترسل كلمات مرور أو بيانات حساسة؛ تُرسل رسائلك إلى خدمة المساعد لتوليد الرد."
                : "يمكنك استخدام /my_access لمعرفة حالة طلبك. لإرسال طلب انضمام أو إعادة عرضه أرسل /start.";
            await SafeReply(message.Chat.Id, help, reply, cancellationToken);
            return;
        }
        if (registrationStatus != TelegramRegistrationStatus.Approved)
        {
            _flows.TryRemove(userId, out _);
            var notice = registrationStatus is null
                ? "لإنشاء طلب انضمامك إلى وشوشني، أرسل /start أولاً."
                : registrationStatus == TelegramRegistrationStatus.Pending
                    ? "طلبك قيد مراجعة المسؤول. أرسل /my_access لمعرفة الحالة."
                    : RejectedAccessNotice;
            await SafeReply(message.Chat.Id, notice, reply, cancellationToken);
            return;
        }
        if (command == "/cancel")
        {
            _flows.TryRemove(message.From.Id, out _);
            _assignmentCoursePickers.TryRemove(userId, out _);
            _examCoursePickers.TryRemove(userId, out _);
            await SafeReply(message.Chat.Id, "تم إلغاء العملية الحالية.", reply, cancellationToken);
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
                    : status.Expired ? "انتهت جلسة Moodle. أرسل /link لإعادة الربط."
                    : status.Unavailable ? MoodleAvailabilityMessages.TemporarilyUnavailable
                    : "حساب Moodle غير مربوط. أرسل /link للبدء.";
                break;
            case "/my_courses":
            case "/courses":
                var courseResult = await moodleBrowser.GetCoursesAsync(message.From.Id, cancellationToken);
                response = FormatCourses(courseResult);
                break;
            case "/my_assignments":
            case "/assignments":
                await SafeReply(message.Chat.Id, "⏳ جاري تحميل قائمة مساقاتك من Moodle...", reply, cancellationToken);
                var courseList = await moodleBrowser.GetAssignmentCoursesAsync(userId, cancellationToken);
                if (courseList.Kind is MoodleAssignmentsResultKind.Expired or MoodleAssignmentsResultKind.SessionRestoreFailed
                    or MoodleAssignmentsResultKind.Unavailable or MoodleAssignmentsResultKind.ExtractionFailed)
                {
                    response = FormatAssignmentCourseListError(courseList.Kind);
                    break;
                }
                if (courseList.Courses.Count == 0)
                {
                    response = "لم أجد مساقات مسجلة في حساب Moodle.";
                    break;
                }
                var pickerRequestId = Guid.NewGuid();
                var picker = new AssignmentCoursePicker(pickerRequestId,
                    courseList.Courses.ToDictionary(course => course.Id, course => course.Name), DateTimeOffset.UtcNow.AddMinutes(10));
                _assignmentCoursePickers[userId] = picker;
                var prompt = courseList.Kind == MoodleAssignmentsResultKind.Partial
                    ? "اختر مساقاً لعرض واجباته. تنبيه: تعذر تحميل بعض صفحات قائمة المساقات، وقد تكون القائمة غير مكتملة."
                    : "اختر المساق الذي تريد عرض واجباته ومواعيد التسليم الخاصة به:";
                var choices = courseList.Courses.Select(course =>
                    (Text: TruncateButtonLabel(course.Name), CallbackData: $"course:{pickerRequestId:N}:{course.Id}"))
                    .ToArray();
                try { await coursePicker(message.Chat.Id, prompt, choices, cancellationToken); }
                catch (Exception exception) when (exception is not OperationCanceledException)
                {
                    _assignmentCoursePickers.TryRemove(userId, out _);
                    logger.LogWarning("Could not send Moodle course picker ({ExceptionType}).", exception.GetType().Name);
                    await SafeReply(message.Chat.Id, "تعذر عرض قائمة المساقات حالياً. حاول مرة أخرى.", reply, cancellationToken);
                }
                return;
            case "/my_exams":
                await SafeReply(message.Chat.Id, "⏳ جاري تحميل قائمة مساقاتك من Moodle...", reply, cancellationToken);
                var examCourses = await moodleBrowser.GetAssignmentCoursesAsync(userId, cancellationToken);
                if (examCourses.Kind is MoodleAssignmentsResultKind.Expired or MoodleAssignmentsResultKind.SessionRestoreFailed
                    or MoodleAssignmentsResultKind.Unavailable or MoodleAssignmentsResultKind.ExtractionFailed)
                { response = FormatAssignmentCourseListError(examCourses.Kind); break; }
                if (examCourses.Courses.Count == 0) { response = "لم أجد مساقات مسجلة في حساب Moodle."; break; }
                var examRequestId = Guid.NewGuid();
                _examCoursePickers[userId] = new AssignmentCoursePicker(examRequestId,
                    examCourses.Courses.ToDictionary(course => course.Id, course => course.Name), DateTimeOffset.UtcNow.AddMinutes(10));
                var examPrompt = examCourses.Kind == MoodleAssignmentsResultKind.Partial
                    ? "اختر مساقاً لعرض اختباراته. قد تكون قائمة المساقات غير مكتملة."
                    : "اختر المساق الذي تريد عرض اختباراته وامتحاناته:";
                var examChoices = examCourses.Courses.Select(course =>
                    (Text: TruncateButtonLabel(course.Name), CallbackData: $"examcourse:{examRequestId:N}:{course.Id}")).ToArray();
                try { await coursePicker(message.Chat.Id, examPrompt, examChoices, cancellationToken); }
                catch (Exception exception) when (exception is not OperationCanceledException)
                {
                    _examCoursePickers.TryRemove(userId, out _);
                    logger.LogWarning("Could not send Moodle exam course picker ({ExceptionType}).", exception.GetType().Name);
                    await SafeReply(message.Chat.Id, "تعذر عرض قائمة المساقات حالياً. حاول مرة أخرى.", reply, cancellationToken);
                }
                return;
            case "/all_exams":
                await SafeReply(message.Chat.Id, "⏳ جاري جلب الاختبارات من جميع مساقاتك في Moodle...", reply, cancellationToken);
                var allExams = await moodleBrowser.GetQuizzesAsync(userId, null, cancellationToken);
                response = MoodleQuizMessageFormatter.Format(allExams, moodleOptions.Value.TimeZoneId);
                break;
            case "/reset_chat":
                await geminiChat.ResetAsync(userId, cancellationToken);
                response = "تم مسح سياق المحادثة مع المساعد الذكي.";
                break;
            case "/start":
                response = "أهلاً بك في وشوشني. أرسل /link لربط حساب Moodle، أو /help لعرض الأوامر.";
                break;
            case "/help":
                response = "الأوامر المتاحة:\n/link — ربط حساب Moodle\n/status — حالة الربط\n/my_courses — مساقاتي الفعلية\n/my_assignments — اختر مساقاً لعرض واجباته ومواعيده\n/my_exams — اختر مساقاً لعرض اختباراته\n/all_exams — عرض اختبارات جميع المساقات\n/reset_chat — مسح سياق المحادثة مع المساعد\n/logout — فصل الحساب وحذف الجلسة\n/cancel — إلغاء العملية الحالية\n\nاكتب سؤالك مباشرة للمساعد. لا ترسل كلمات مرور أو بيانات حساسة؛ تُرسل الرسائل إلى خدمة المساعد.";
                break;
            case "/upcoming": response = "أرسل /link أولاً؛ استرجاع المواعيد غير متاح بعد."; break;
            case "/latest": response = "أرسل /link أولاً؛ لا توجد مزامنة للمستجدات بعد."; break;
            case "/scan": response = "المزامنة غير متاحة في هذه المرحلة."; break;
            case "/my_quizzes":
            case "/quizzes": response = "استخدم /my_exams لاختيار مساق أو /all_exams لعرض اختبارات جميع المساقات."; break;
            case "/updates": response = "مزامنة مستجدات المساقات غير متاحة بعد."; break;
            case "/report": response = "التقارير ستتوفر في مرحلة لاحقة."; break;
            case var _ when messageText.StartsWith('/'): response = "لم أتعرف على هذا الأمر. أرسل /help."; break;
            default:
                var thinkingMessage = messageText.Any(character => character is >= '\u0600' and <= '\u06FF')
                    ? "🤔 لحظة بفكر..." : "🤔 One sec, thinking...";
                await SafeReply(message.Chat.Id, thinkingMessage, reply, cancellationToken);
                var geminiReply = await geminiChat.ReplyAsync(userId, messageText, cancellationToken);
                response = geminiReply.Text;
                break;
        }
        if (!await authorization.IsApprovedAsync(userId, cancellationToken))
        {
            _flows.TryRemove(userId, out _);
            await SafeReply(message.Chat.Id, "تم تغيير صلاحية حسابك أثناء تنفيذ الطلب؛ لم أرسل نتائج Moodle. تواصل مع مسؤول البوت.", reply, cancellationToken);
            return;
        }
        await SafeReply(message.Chat.Id, response, reply, cancellationToken);
    }

    public async Task HandleCallbackAsync(
        CallbackQuery callback,
        Func<long, string, CancellationToken, Task> reply,
        Func<string, string, CancellationToken, Task> answer,
        Func<long, int, string, CancellationToken, Task> edit,
        CancellationToken cancellationToken)
    {
        var answered = false;
        try
        {
            if (callback.From is null)
            {
                await answer(callback.Id, "تعذر التحقق من هوية المرسل.", cancellationToken);
                answered = true;
                return;
            }
            if (await authorization.IsBlockedAsync(callback.From.Id, cancellationToken))
            {
                if (callback.Message?.Chat.Type == ChatType.Private)
                    await reply(callback.Message.Chat.Id, "تم تعليق إمكانية استخدامك للبوت. تواصل مع مسؤول وشوشني إذا كنت تعتقد أن هذا خطأ.", cancellationToken);
                await answer(callback.Id, "تم تعليق إمكانية استخدامك للبوت.", cancellationToken);
                answered = true;
                return;
            }
            try { await users.RecordActivityAsync(callback.From.Id, cancellationToken); }
            catch (Exception exception) when (exception is not OperationCanceledException)
            { logger.LogDebug("Could not update dashboard last-activity time ({ExceptionType}).", exception.GetType().Name); }
            if (callback.Message is null || callback.Data is null)
            {
                await answer(callback.Id, "تعذر قراءة طلب المراجعة.", cancellationToken);
                answered = true;
                return;
            }
            if (callback.Data.StartsWith("course:", StringComparison.Ordinal))
            {
                await HandleAssignmentCourseCallbackAsync(callback, reply, answer, edit, cancellationToken, () => answered = true);
                return;
            }
            if (callback.Data.StartsWith("examcourse:", StringComparison.Ordinal))
            {
                await HandleExamCourseCallbackAsync(callback, reply, answer, edit, cancellationToken, () => answered = true);
                return;
            }
            if (!await authorization.IsAdminAsync(callback.From.Id, cancellationToken))
            {
                await answer(callback.Id, "هذا الإجراء مخصص لمسؤول البوت.", cancellationToken);
                answered = true;
                return;
            }
            var parts = callback.Data.Split(':', 3);
            if (parts.Length != 3 || parts[0] != "reg" || !Guid.TryParse(parts[2], out var requestId) ||
                (parts[1] != "approve" && parts[1] != "reject"))
            {
                await answer(callback.Id, "بيانات الطلب غير صالحة.", cancellationToken);
                answered = true;
                return;
            }
            var target = parts[1] == "approve" ? TelegramRegistrationStatus.Approved : TelegramRegistrationStatus.Rejected;
            var registration = await users.TransitionPendingAsync(requestId, target, callback.From.Id, cancellationToken);
            if (registration is null)
            {
                await answer(callback.Id, "الطلب غير موجود أو سبق اتخاذ قرار بشأنه.", cancellationToken);
                answered = true;
                var existing = await users.GetByRequestIdAsync(requestId, cancellationToken);
                if (existing is not null)
                    await edit(callback.Message.Chat.Id, callback.Message.MessageId,
                        $"تمت معالجة الطلب مسبقاً: {StatusArabic(existing.Status)}.", cancellationToken);
                return;
            }
            await answer(callback.Id, target == TelegramRegistrationStatus.Approved ? "تم قبول الطلب." : "تم رفض الطلب.", cancellationToken);
            answered = true;
            await NotifyApplicantAsync(registration, reply, cancellationToken);
            await edit(callback.Message.Chat.Id, callback.Message.MessageId,
                target == TelegramRegistrationStatus.Approved ? "تم قبول الطلب ✅" : "تم رفض الطلب ❌", cancellationToken);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            logger.LogWarning("Registration callback failed ({ExceptionType}).", exception.GetType().Name);
            if (!answered)
            {
                try { await answer(callback.Id, "تعذر معالجة الطلب حالياً.", cancellationToken); }
                catch (Exception answerException) when (answerException is not OperationCanceledException)
                { logger.LogDebug("Could not acknowledge registration callback ({ExceptionType}).", answerException.GetType().Name); }
            }
        }
    }

    private async Task<bool> HandleAdminCommandAsync(string command, string messageText, long adminId, long chatId,
        Func<long, string, CancellationToken, Task> reply,
        Func<long, string, Guid, CancellationToken, Task> adminRequest,
        CancellationToken cancellationToken)
    {
        var pieces = messageText.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        switch (command)
        {
            case "/start":
                await SafeReply(chatId, "أهلاً بك في وشوشني.\n/help لعرض الأوامر\n/admin لفتح لوحة الإدارة\n/pending_users لطلبات الانضمام\n/users لإحصاءات المستخدمين\n/approve <id> أو /reject <id> للطلبات، و/revoke <id> لسحب اعتماد مستخدم معتمد.", reply, cancellationToken);
                return true;
            case "/admin":
                await SafeReply(chatId, "لوحة إدارة وشوشني: https://wawshnibot.onrender.com/admin\nسجّل الدخول باسم المستخدم وكلمة المرور اللذين أعددتهما في إعدادات Render.", reply, cancellationToken);
                return true;
            case "/help":
                await SafeReply(chatId, "أوامر المسؤول:\n/start — ترحيب\n/admin — فتح لوحة الإدارة\n/link — ربط Moodle\n/status — حالة الربط\n/my_courses — المساقات\n/my_assignments — اختيار مساق لعرض الواجبات\n/my_exams — اختيار مساق لعرض الاختبارات\n/all_exams — عرض اختبارات جميع المساقات\n/reset_chat — مسح سياق المحادثة مع المساعد\n/logout — فصل Moodle\n/pending_users — الطلبات المعلقة\n/users — إحصاءات المستخدمين\n/approve <telegram_user_id> — قبول طلب معلق أو إعادة اعتماد مرفوض\n/reject <telegram_user_id> — رفض طلب معلق فقط\n/revoke <telegram_user_id> — سحب اعتماد مستخدم معتمد\n\nاكتب سؤالك مباشرة للمساعد. لا ترسل كلمات مرور أو بيانات حساسة؛ تُرسل الرسائل إلى خدمة المساعد. ", reply, cancellationToken);
                return true;
            case "/pending_users":
                var pending = await users.GetPendingAsync(cancellationToken);
                if (pending.Count == 0) { await SafeReply(chatId, "لا توجد طلبات انضمام معلّقة حالياً.", reply, cancellationToken); return true; }
                foreach (var registration in pending)
                {
                    var name = string.Join(" ", new[] { registration.FirstName, registration.LastName }.Where(s => !string.IsNullOrWhiteSpace(s)));
                    if (string.IsNullOrWhiteSpace(name)) name = "غير متاح";
                    var username = string.IsNullOrWhiteSpace(registration.Username) ? "غير متاح" : "@" + registration.Username;
                    await adminRequest(chatId, $"🔔 طلب انضمام معلّق\n👤 {name}\n🔹 Username: {username}\n🆔 {registration.TelegramUserId}\n📅 {registration.RegisteredAt:yyyy-MM-dd HH:mm} UTC", registration.RequestId, cancellationToken);
                }
                return true;
            case "/users":
                var counts = await users.GetCountsAsync(cancellationToken);
                await SafeReply(chatId, $"إحصاءات المستخدمين:\nالإجمالي: {counts.Total}\nالمعتمدون: {counts.Approved}\nقيد المراجعة: {counts.Pending}\nالمرفوضون/المسحوب اعتمادهم: {counts.Rejected}", reply, cancellationToken);
                return true;
            case "/approve":
            case "/reject":
            case "/revoke":
                if (pieces.Length != 2 || !long.TryParse(pieces[1], out var targetId) || targetId <= 0)
                { await SafeReply(chatId, $"الاستخدام: {command} <telegram_user_id>", reply, cancellationToken); return true; }
                if (targetId == adminId)
                { await SafeReply(chatId, "لا يمكن تغيير صلاحية مسؤول البوت.", reply, cancellationToken); return true; }
                var current = await users.GetByTelegramIdAsync(targetId, cancellationToken);
                var wasReapproval = command == "/approve" && current?.Status == TelegramRegistrationStatus.Rejected;
                var targetStatus = command == "/approve" ? TelegramRegistrationStatus.Approved : TelegramRegistrationStatus.Rejected;
                IReadOnlyCollection<TelegramRegistrationStatus> allowed = command switch
                {
                    "/approve" => [TelegramRegistrationStatus.Pending, TelegramRegistrationStatus.Rejected],
                    "/reject" => [TelegramRegistrationStatus.Pending],
                    _ => [TelegramRegistrationStatus.Approved]
                };
                var updated = await users.SetStatusAsync(targetId, targetStatus, adminId, allowed, cancellationToken);
                if (updated is null)
                {
                    await SafeReply(chatId, current is null ? "لا يوجد طلب مسجل لهذا المعرّف." : command == "/reject" && current.Status == TelegramRegistrationStatus.Approved
                        ? "هذا المستخدم معتمد؛ إذا أردت سحب اعتماده استخدم /revoke <telegram_user_id>."
                        : $"لم يتغير الوضع؛ الحالة الحالية: {StatusArabic(current.Status)}.", reply, cancellationToken);
                    return true;
                }
                await SafeReply(chatId, command == "/revoke" ? "تم سحب صلاحية المستخدم. بقيت جلسة Moodle المشفرة محفوظة لكنها لن تستخدم ما دام الحساب غير معتمد." : targetStatus == TelegramRegistrationStatus.Approved ? "تم اعتماد المستخدم." : "تم رفض المستخدم أو إلغاء اعتماده.", reply, cancellationToken);
                await NotifyApplicantAsync(updated, reply, cancellationToken, wasReapproval);
                return true;
            default:
                break;
        }
        if (command == "/my_access")
        {
            await SafeReply(chatId, "حسابك مسؤول البوت ومعتمد دائماً.", reply, cancellationToken);
            return true;
        }
        return false;
    }

    private static string AccessStatusText(TelegramRegistrationStatus? status) => status switch
    {
        TelegramRegistrationStatus.Pending => "طلبك قيد مراجعة المسؤول.",
        TelegramRegistrationStatus.Approved => "حسابك معتمد، ويمكنك استخدام خدمات وشوشني.",
        TelegramRegistrationStatus.Rejected => RejectedAccessNotice,
        _ => "لا يوجد طلب انضمام مسجل. أرسل /start لإنشاء طلب."
    };

    private static string FormatAssignmentCourseListError(MoodleAssignmentsResultKind kind) => kind switch
    {
        MoodleAssignmentsResultKind.Expired => "جلسة Moodle غير موجودة أو انتهت. أرسل /link لإعادة الربط.",
        MoodleAssignmentsResultKind.SessionRestoreFailed => "تعذر استرجاع جلسة Moodle المحفوظة. أرسل /link لإعادة ربط الحساب.",
        MoodleAssignmentsResultKind.Unavailable => MoodleAvailabilityMessages.TemporarilyUnavailable,
        _ => "تم فتح Moodle، لكن تعذر استخراج قائمة المساقات. لم أعتبر ذلك دليلاً على عدم وجود مساقات."
    };

    private async Task HandleAssignmentCourseCallbackAsync(
        CallbackQuery callback,
        Func<long, string, CancellationToken, Task> reply,
        Func<string, string, CancellationToken, Task> answer,
        Func<long, int, string, CancellationToken, Task> edit,
        CancellationToken cancellationToken,
        Action markAnswered)
    {
        if (callback.Message!.Chat.Type != ChatType.Private || callback.Message.Chat.Id != callback.From!.Id)
        {
            await answer(callback.Id, "افتح البوت في محادثة خاصة لاختيار المساق.", cancellationToken);
            markAnswered();
            return;
        }
        if (!await authorization.IsApprovedAsync(callback.From.Id, cancellationToken))
        {
            await answer(callback.Id, "حسابك غير معتمد لاستخدام خدمات Moodle.", cancellationToken);
            markAnswered();
            return;
        }

        var parts = callback.Data!.Split(':', 3);
        if (parts.Length != 3 || !Guid.TryParseExact(parts[1], "N", out var requestId)
            || !int.TryParse(parts[2], out var courseId))
        {
            await answer(callback.Id, "اختيار المساق غير صالح.", cancellationToken);
            markAnswered();
            return;
        }
        if (!_assignmentCoursePickers.TryGetValue(callback.From.Id, out var current)
            || current.RequestId != requestId || current.ExpiresAt < DateTimeOffset.UtcNow
            || !current.Courses.TryGetValue(courseId, out var courseName))
        {
            await answer(callback.Id, "انتهت صلاحية قائمة المساقات. أرسل /my_assignments لعرضها من جديد.", cancellationToken);
            markAnswered();
            return;
        }
        if (!_assignmentCoursePickers.TryRemove(callback.From.Id, out var consumed) || consumed.RequestId != requestId)
        {
            if (consumed is not null) _assignmentCoursePickers.TryAdd(callback.From.Id, consumed);
            await answer(callback.Id, "تم استخدام هذا الاختيار أو انتهت صلاحيته. أرسل /my_assignments من جديد.", cancellationToken);
            markAnswered();
            return;
        }

        await answer(callback.Id, "جارٍ تحميل واجبات المساق...", cancellationToken);
        markAnswered();
        try { await edit(callback.Message.Chat.Id, callback.Message.MessageId, $"تم اختيار المساق: {courseName}", cancellationToken); }
        catch (Exception exception) when (exception is not OperationCanceledException)
        { logger.LogDebug("Could not update the Moodle course picker message ({ExceptionType}); continuing with selected course.", exception.GetType().Name); }
        await SafeReply(callback.Message.Chat.Id, "⏳ جاري جلب واجبات المساق ومواعيدها من Moodle...", reply, cancellationToken);
        var result = await moodleBrowser.GetAssignmentsForCourseAsync(callback.From.Id, courseId, cancellationToken);
        if (!await authorization.IsApprovedAsync(callback.From.Id, cancellationToken))
        {
            await SafeReply(callback.Message.Chat.Id, "تم تغيير صلاحية حسابك أثناء تنفيذ الطلب؛ لم أرسل بيانات Moodle. تواصل مع مسؤول البوت.", reply, cancellationToken);
            return;
        }
        await SafeReply(callback.Message.Chat.Id,
            MoodleAssignmentMessageFormatter.Format(result, DateTimeOffset.UtcNow, moodleOptions.Value.TimeZoneId), reply, cancellationToken);
    }

    private async Task HandleExamCourseCallbackAsync(
        CallbackQuery callback,
        Func<long, string, CancellationToken, Task> reply,
        Func<string, string, CancellationToken, Task> answer,
        Func<long, int, string, CancellationToken, Task> edit,
        CancellationToken cancellationToken,
        Action markAnswered)
    {
        if (callback.Message!.Chat.Type != ChatType.Private || callback.Message.Chat.Id != callback.From!.Id)
        { await answer(callback.Id, "افتح البوت في محادثة خاصة لاختيار المساق.", cancellationToken); markAnswered(); return; }
        if (!await authorization.IsApprovedAsync(callback.From.Id, cancellationToken))
        { await answer(callback.Id, "حسابك غير معتمد لاستخدام خدمات Moodle.", cancellationToken); markAnswered(); return; }
        var parts = callback.Data!.Split(':', 3);
        if (parts.Length != 3 || !Guid.TryParseExact(parts[1], "N", out var requestId) || !int.TryParse(parts[2], out var courseId))
        { await answer(callback.Id, "اختيار المساق غير صالح.", cancellationToken); markAnswered(); return; }
        if (!_examCoursePickers.TryGetValue(callback.From.Id, out var current) || current.RequestId != requestId
            || current.ExpiresAt < DateTimeOffset.UtcNow || !current.Courses.TryGetValue(courseId, out var courseName))
        { await answer(callback.Id, "انتهت صلاحية قائمة المساقات. أرسل /my_exams لعرضها من جديد.", cancellationToken); markAnswered(); return; }
        if (!_examCoursePickers.TryRemove(callback.From.Id, out var consumed) || consumed.RequestId != requestId)
        { if (consumed is not null) _examCoursePickers.TryAdd(callback.From.Id, consumed); await answer(callback.Id, "تم استخدام هذا الاختيار أو انتهت صلاحيته. أرسل /my_exams من جديد.", cancellationToken); markAnswered(); return; }
        await answer(callback.Id, "جارٍ تحميل اختبارات المساق...", cancellationToken); markAnswered();
        try { await edit(callback.Message.Chat.Id, callback.Message.MessageId, $"تم اختيار المساق: {courseName}", cancellationToken); }
        catch (Exception exception) when (exception is not OperationCanceledException)
        { logger.LogDebug("Could not update the Moodle exam course picker ({ExceptionType}).", exception.GetType().Name); }
        await SafeReply(callback.Message.Chat.Id, "⏳ جاري جلب اختبارات المساق من Moodle...", reply, cancellationToken);
        var result = await moodleBrowser.GetQuizzesAsync(callback.From.Id, courseId, cancellationToken);
        if (!await authorization.IsApprovedAsync(callback.From.Id, cancellationToken))
        { await SafeReply(callback.Message.Chat.Id, "تم تغيير صلاحية حسابك أثناء تنفيذ الطلب؛ لم أرسل بيانات Moodle. تواصل مع مسؤول البوت.", reply, cancellationToken); return; }
        await SafeReply(callback.Message.Chat.Id, MoodleQuizMessageFormatter.Format(result, moodleOptions.Value.TimeZoneId), reply, cancellationToken);
    }

    private static string TruncateButtonLabel(string value) => value.Length <= 60 ? value : value[..57] + "…";

    private static string StatusArabic(TelegramRegistrationStatus status) => status switch
    {
        TelegramRegistrationStatus.Pending => "قيد المراجعة", TelegramRegistrationStatus.Approved => "معتمد", _ => "مرفوض/مسحوب الاعتماد"
    };

    private async Task NotifyApplicantAsync(TelegramUserRegistration registration, Func<long, string, CancellationToken, Task> reply, CancellationToken cancellationToken, bool wasReapproval = false)
    {
        var message = registration.Status == TelegramRegistrationStatus.Approved
            ? wasReapproval
                ? AccessNotificationMessages.Reapproved
                : "🎉 يا أهلاً وسهلاً فيك! صار حسابك معتمد، ونوّرت عيلة وشوشني 💛\nكنت مستنيك لدرجة إني جهّزت القهوة وفتحت الدفاتر 😄📚\n\nهسا بتقدر تربط حسابك الجامعي من Moodle. أرسل /link ونبدأ!"
            : RejectedAccessNotice;
        try { await reply(registration.TelegramUserId, message, cancellationToken); }
        catch (Exception exception) when (exception is not OperationCanceledException)
        { logger.LogWarning("Could not notify Telegram user about access decision ({ExceptionType}); database status was saved.", exception.GetType().Name); }
    }

    private async Task SafeReply(long chatId, string text, Func<long, string, CancellationToken, Task> reply, CancellationToken cancellationToken)
    {
        foreach (var chunk in TelegramTextChunker.Split(text))
        {
            try { await reply(chatId, chunk, cancellationToken); }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                logger.LogError("Failed to reply to Telegram ({ExceptionType}); response chunk length {Length}.", exception.GetType().Name, chunk.Length);
                return;
            }
        }
    }

    private static string FormatLinkResult(MoodleLinkResult result) => result.Kind switch
    {
        MoodleLinkResultKind.NoCourses => "تم التحقق من الحساب وحفظ الجلسة المشفّرة، وأظهرت صفحة Moodle رسالة صريحة بعدم وجود مساقات مسجلة حالياً.",
        MoodleLinkResultKind.Unavailable => MoodleAvailabilityMessages.TemporarilyUnavailable,
        MoodleLinkResultKind.CoursesNotDetected => "تم التحقق من الحساب وحفظ الجلسة المشفّرة، لكن لم أتعرف على عناصر المساقات في الصفحة. هذا لا يعني أن حسابك بلا مساقات؛ راجع سجل التشخيص ثم أرسل /my_courses لاحقاً.",
        MoodleLinkResultKind.UnexpectedRedirect => "تم التحقق من الحساب وحفظ الجلسة، لكن Moodle حوّل طلب المساقات إلى صفحة غير متوقعة. تحقق عبر /status ثم أعد المحاولة.",
        MoodleLinkResultKind.DashboardRedirect => "تم التحقق من الحساب وحفظ الجلسة، لكن صفحة المساقات أعادت توجيهي إلى لوحة Moodle. لم أعتبر ذلك دليلاً على عدم وجود مساقات.",
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
        MoodleLinkResultKind.NoCourses => "أظهرت صفحة Moodle رسالة صريحة بعدم وجود مساقات مسجلة حالياً.",
        MoodleLinkResultKind.CoursesNotDetected => "تم فتح حساب Moodle، لكن لم أتعرف على عناصر المساقات في الصفحة. هذا لا يعني أن حسابك بلا مساقات؛ تم تسجيل معلومات تشخيصية آمنة للمراجعة.",
        MoodleLinkResultKind.UnexpectedRedirect => "حوّل Moodle طلب المساقات إلى صفحة غير متوقعة. تحقق من الجلسة عبر /status ثم أعد المحاولة.",
        MoodleLinkResultKind.DashboardRedirect => "حوّل Moodle طلب المساقات إلى لوحة الحساب دون ظهور قائمة المساقات. لم أعتبر ذلك دليلاً على عدم وجود مساقات.",
        MoodleLinkResultKind.Success when result.CoursesRetrieved => "مساقاتك المسجلة في Moodle:\n• " + string.Join("\n• ", result.Courses),
        MoodleLinkResultKind.Unavailable => MoodleAvailabilityMessages.TemporarilyUnavailable,
        _ => "تعذر جلب المساقات من Moodle. أرسل /status للتحقق من الجلسة أو /link لإعادة الربط."
    };

    private enum LinkStep { StudentId, Password }
    private sealed record LinkFlow(LinkStep Step, string? StudentId, DateTimeOffset StartedAt);
    private sealed record AssignmentCoursePicker(Guid RequestId, IReadOnlyDictionary<int, string> Courses, DateTimeOffset ExpiresAt);
}
