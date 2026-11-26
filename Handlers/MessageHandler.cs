using System.Collections.Concurrent;
using System.Security.Cryptography;
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
    IOptions<BotContactOptions> botContact,
    ILogger<MessageHandler> logger)
{
    private const long MaxImageBytes = 10 * 1024 * 1024;
    private const long MaxAudioBytes = 10 * 1024 * 1024;
    private const long MaxAssignmentFileBytes = 20 * 1024 * 1024;
    private const string ImageAnalysisHelp = "ابعت صورة واسألني عنها، وبحللها لك. الصور تُرسل لخدمة Gemini للتحليل؛ تجنب إرسال صور حساسة أو خاصة.";
    private const string VoiceHelp = "ابعتلي رسالة صوتية، بفهمها وبرد عليك بشكل طبيعي 🎙️ الصوت يُرسل لخدمة المساعد لتفريغه؛ تجنب إرسال معلومات حساسة.";
    private string RejectedAccessNotice => botContact.Value.RejectedAccessNotice;
    private readonly ConcurrentDictionary<long, LinkFlow> _flows = new();
    private readonly ConcurrentDictionary<long, AssignmentCoursePicker> _assignmentCoursePickers = new();
    private readonly ConcurrentDictionary<long, AssignmentCoursePicker> _examCoursePickers = new();
    private readonly ConcurrentDictionary<long, AssignmentSubmissionFlow> _assignmentSubmissionFlows = new();

    public async Task HandleAsync(
        Message message,
        Func<long, string, CancellationToken, Task> reply,
        Func<long, int, CancellationToken, Task> delete,
        Func<string, long, CancellationToken, Task<byte[]>> downloadImage,
        Func<long, string, Guid, CancellationToken, Task> adminRequest,
        Func<long, string, IReadOnlyList<(string Text, string CallbackData)>, CancellationToken, Task> coursePicker,
        CancellationToken cancellationToken)
    {
        if (message.From is null || message.Chat is null) return;
        var imageAttachment = GetImageAttachment(message);
        var audioAttachment = GetAudioAttachment(message);
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
        if (string.IsNullOrWhiteSpace(message.Text) && imageAttachment is null && audioAttachment is null && message.Document is null)
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

        var messageText = message.Text?.Trim() ?? "";
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
                ? "الأوامر المتاحة:\n/link — ربط حساب Moodle\n/status — حالة الربط\n/my_courses — مساقاتي الفعلية\n/my_assignments — اختر مساقاً لعرض واجباته ومواعيده\n/submit_assignment — اختيار واجب ورفعه بعد تأكيدك\n/my_exams — اختر مساقاً لعرض اختباراته\n/all_exams — عرض اختبارات جميع المساقات\n/reset_chat — مسح سياق المحادثة مع المساعد\n/logout — فصل الحساب وحذف الجلسة\n/cancel — إلغاء العملية الحالية\n/my_access — حالة طلب الانضمام\n\nوتقدر تطلبها بطريقتك، مثل «بدي أربط Moodle»، «ورجيني واجباتي»، «بدي أسلّم واجب»، أو «شو امتحاناتي؟». " + ImageAnalysisHelp + " " + VoiceHelp + "\nللأسئلة العادية، اكتب أو أرسل سؤالك مباشرة. لا ترسل كلمات مرور أو بيانات حساسة؛ تُرسل رسائلك إلى خدمة المساعد لتوليد الرد."
                : "يمكنك استخدام /my_access لمعرفة حالة طلبك. لإرسال طلب انضمام أو إعادة عرضه أرسل /start.";
            await SafeReply(message.Chat.Id, help, reply, cancellationToken);
            return;
        }
        if (registrationStatus != TelegramRegistrationStatus.Approved)
        {
            _flows.TryRemove(userId, out _);
            ClearAssignmentSubmissionFlow(userId);
            var notice = registrationStatus is null
                ? "لإنشاء طلب انضمامك إلى وشوشني، أرسل /start أولاً."
                : registrationStatus == TelegramRegistrationStatus.Pending
                    ? "طلبك قيد مراجعة المسؤول. أرسل /my_access لمعرفة الحالة."
                    : RejectedAccessNotice;
            await SafeReply(message.Chat.Id, notice, reply, cancellationToken);
            return;
        }
        if (!command.StartsWith('/') && _assignmentSubmissionFlows.TryGetValue(userId, out var submissionFlow)
            && submissionFlow.Step == AssignmentSubmissionStep.AwaitingContent)
        {
            await ReceiveAssignmentSubmissionContentAsync(message, submissionFlow, downloadImage, reply, coursePicker, cancellationToken);
            return;
        }
        if (imageAttachment is not null)
        {
            if (_flows.ContainsKey(userId))
            {
                await SafeReply(message.Chat.Id, "خلّينا نكمّل ربط Moodle أولاً؛ أرسل الرقم الجامعي أو كلمة المرور كنص، أو /cancel لإلغاء الربط. لن أرسل الصورة إلى المساعد أثناء إدخال بيانات الدخول.", reply, cancellationToken);
                return;
            }
            await HandleImageAsync(message, imageAttachment, downloadImage, reply, cancellationToken);
            return;
        }
        if (audioAttachment is not null)
        {
            if (_flows.ContainsKey(userId))
            {
                await SafeReply(message.Chat.Id, "خلّينا نكمّل ربط Moodle بالرسائل النصية فقط؛ أرسل الرقم أو كلمة المرور كنص، أو /cancel لإلغاء الربط.", reply, cancellationToken);
                return;
            }
            var transcript = await TranscribeVoiceAsync(message, audioAttachment, downloadImage, reply, cancellationToken);
            if (transcript is null) return;
            messageText = transcript;
            command = messageText.Split(' ', 2)[0].Split('@', 2)[0].ToLowerInvariant();
        }
        if (!_flows.ContainsKey(userId) && !command.StartsWith('/')
            && NaturalLanguageCommandRouter.MapToCommand(messageText) is { } routedCommand)
        {
            command = routedCommand;
            logger.LogInformation("Mapped an approved user's natural-language request to {Command}.", routedCommand);
        }
        if (command == "/cancel")
        {
            _flows.TryRemove(message.From.Id, out _);
            _assignmentCoursePickers.TryRemove(userId, out _);
            _examCoursePickers.TryRemove(userId, out _);
            ClearAssignmentSubmissionFlow(userId);
            await SafeReply(message.Chat.Id, "تم إلغاء العملية الحالية.", reply, cancellationToken);
            return;
        }
        if (command is "/submit_assignment" or "/submit_homework")
        {
            await SafeReply(message.Chat.Id, "⏳ جاري تحميل مساقاتك لاختيار الواجب...", reply, cancellationToken);
            var courses = await moodleBrowser.GetAssignmentCoursesAsync(userId, cancellationToken);
            if (!await authorization.IsApprovedAsync(userId, cancellationToken))
            { await SafeReply(message.Chat.Id, "تغير اعتماد حسابك أثناء الطلب؛ لم أعرض بيانات Moodle.", reply, cancellationToken); return; }
            if (courses.Kind is MoodleAssignmentsResultKind.Expired or MoodleAssignmentsResultKind.SessionRestoreFailed
                or MoodleAssignmentsResultKind.Unavailable or MoodleAssignmentsResultKind.ExtractionFailed)
            { await SafeReply(message.Chat.Id, FormatAssignmentCourseListError(courses.Kind), reply, cancellationToken); return; }
            if (courses.Courses.Count == 0) { await SafeReply(message.Chat.Id, "لم أجد مساقات مسجلة في حساب Moodle.", reply, cancellationToken); return; }
            var submissionRequest = Guid.NewGuid();
            ClearAssignmentSubmissionFlow(userId);
            _assignmentSubmissionFlows[userId] = new AssignmentSubmissionFlow(submissionRequest, AssignmentSubmissionStep.ChooseCourse,
                courses.Courses.ToDictionary(c => c.Id, c => c.Name), [], null, null, null, null, null, null, null, null, null, DateTimeOffset.UtcNow.AddMinutes(15));
            var choices = courses.Courses.Take(100).Select(c => (TruncateButtonLabel(c.Name), $"submitcourse:{submissionRequest:N}:{c.Id}")).ToArray();
            await coursePicker(message.Chat.Id, "اختر المساق الذي يحتوي الواجب الذي تريد تسليمه:", choices, cancellationToken);
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
            ClearAssignmentSubmissionFlow(userId);
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
                await SafeReply(message.Chat.Id, "تم استلام الرقم الجامعي. أرسل كلمة مرور Moodle الآن كرسالة واحدة، والبوت سيحذف الرسالة فور استلامها.", reply, cancellationToken);
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
                response = "الأوامر المتاحة:\n/link — ربط حساب Moodle\n/status — حالة الربط\n/my_courses — مساقاتي الفعلية\n/my_assignments — اختر مساقاً لعرض واجباته ومواعيده\n/submit_assignment — اختيار واجب ورفعه بعد تأكيدك\n/my_exams — اختر مساقاً لعرض اختباراته\n/all_exams — عرض اختبارات جميع المساقات\n/reset_chat — مسح سياق المحادثة مع المساعد\n/logout — فصل الحساب وحذف الجلسة\n/cancel — إلغاء العملية الحالية\n\nاكتب سؤالك مباشرة للمساعد. لا ترسل كلمات مرور أو بيانات حساسة؛ تُرسل الرسائل إلى خدمة المساعد.";
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

    private async Task HandleImageAsync(Message message, ImageAttachment attachment,
        Func<string, long, CancellationToken, Task<byte[]>> downloadImage,
        Func<long, string, CancellationToken, Task> reply, CancellationToken cancellationToken)
    {
        if (!IsSupportedImageMimeType(attachment.MimeType))
        {
            await SafeReply(message.Chat.Id, "أقدر أحلل صور JPEG أو PNG أو WebP أو HEIC/HEIF فقط حالياً.", reply, cancellationToken);
            return;
        }
        if (attachment.FileSize is > MaxImageBytes)
        {
            await SafeReply(message.Chat.Id, "حجم الصورة كبير 😅 أرسل صورة أصغر من 10 ميغابايت.", reply, cancellationToken);
            return;
        }

        var imageBytes = Array.Empty<byte>();
        try
        {
            imageBytes = await downloadImage(attachment.FileId, MaxImageBytes, cancellationToken);
            if (imageBytes.Length == 0)
            {
                await SafeReply(message.Chat.Id, "ما وصلني محتوى الصورة بشكل سليم. جرّب ترسلها مرة ثانية.", reply, cancellationToken);
                return;
            }
            if (imageBytes.LongLength > MaxImageBytes)
            {
                await SafeReply(message.Chat.Id, "حجم الصورة كبير 😅 أرسل صورة أصغر من 10 ميغابايت.", reply, cancellationToken);
                return;
            }

            await SafeReply(message.Chat.Id, "🤔 وصلت الصورة، خليني أشوفها...", reply, cancellationToken);
            var prompt = string.IsNullOrWhiteSpace(message.Caption)
                ? "حلل هذه الصورة واشرح محتواها بإيجاز، واذكر النصوص أو التفاصيل المهمة الظاهرة فيها. أجب بالعربية إلا إذا كان سياق المحادثة يوضح لغة أخرى."
                : message.Caption.Trim();
            if (prompt.Length > 6000)
            {
                await SafeReply(message.Chat.Id, "تعليق الصورة طويل جداً. أعد إرسالها بتعليق أقصر.", reply, cancellationToken);
                return;
            }
            var result = await geminiChat.ReplyWithImageAsync(message.From!.Id, prompt, imageBytes, attachment.MimeType, cancellationToken);
            await SafeReply(message.Chat.Id, result.Text, reply, cancellationToken);
        }
        catch (InvalidDataException)
        {
            await SafeReply(message.Chat.Id, "حجم الصورة كبير 😅 أرسل صورة أصغر من 10 ميغابايت.", reply, cancellationToken);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            logger.LogWarning("Telegram image analysis failed ({ExceptionType}); image content and identifiers omitted.", exception.GetType().Name);
            await SafeReply(message.Chat.Id, "تعذر استلام الصورة أو تحليلها حالياً. جرّب صورة أصغر أو أعد المحاولة بعد قليل.", reply, cancellationToken);
        }
        finally
        {
            if (imageBytes.Length > 0) CryptographicOperations.ZeroMemory(imageBytes);
        }
    }

    private static ImageAttachment? GetImageAttachment(Message message)
    {
        if (message.Photo is { Length: > 0 } photos)
        {
            var largest = photos[^1];
            return new ImageAttachment(largest.FileId, "image/jpeg", largest.FileSize);
        }
        if (message.Document is { MimeType: { } mimeType } document && mimeType.StartsWith("image/", StringComparison.OrdinalIgnoreCase))
            return new ImageAttachment(document.FileId, mimeType.ToLowerInvariant(), document.FileSize);
        return null;
    }

    private async Task<string?> TranscribeVoiceAsync(Message message, AudioAttachment attachment,
        Func<string, long, CancellationToken, Task<byte[]>> downloadFile,
        Func<long, string, CancellationToken, Task> reply, CancellationToken cancellationToken)
    {
        if (!IsSupportedAudioMimeType(attachment.MimeType))
        {
            await SafeReply(message.Chat!.Id, "صيغة هالصوت مش مدعومة حالياً. ابعت Voice من Telegram أو ملفاً بصيغة OGG/Opus أو MP3 أو WAV أو M4A.", reply, cancellationToken);
            return null;
        }
        if (attachment.FileSize is > MaxAudioBytes)
        {
            await SafeReply(message.Chat!.Id, "الصوت طويل/كبير عليّ شوي 😅 ابعت تسجيلاً أصغر من 10 ميغابايت.", reply, cancellationToken);
            return null;
        }

        var audioBytes = Array.Empty<byte>();
        try
        {
            audioBytes = await downloadFile(attachment.FileId, MaxAudioBytes, cancellationToken);
            if (audioBytes.Length == 0)
            {
                await SafeReply(message.Chat!.Id, "ما وصلني محتوى الصوت بشكل سليم. جرّب تبعته مرة ثانية.", reply, cancellationToken);
                return null;
            }
            if (audioBytes.LongLength > MaxAudioBytes)
            {
                await SafeReply(message.Chat!.Id, "الصوت طويل/كبير عليّ شوي 😅 ابعت تسجيلاً أصغر من 10 ميغابايت.", reply, cancellationToken);
                return null;
            }

            await SafeReply(message.Chat!.Id, "🎧 وصلتني، خليني أسمعك...", reply, cancellationToken);
            var result = await geminiChat.TranscribeAudioAsync(message.From!.Id, audioBytes, attachment.MimeType, cancellationToken);
            if (result.Kind != GeminiReplyKind.Success || string.IsNullOrWhiteSpace(result.Text))
            {
                await SafeReply(message.Chat!.Id, result.Text, reply, cancellationToken);
                return null;
            }
            return result.Text.Trim();
        }
        catch (InvalidDataException)
        {
            await SafeReply(message.Chat!.Id, "الصوت كبير 😅 ابعت تسجيلاً أصغر من 10 ميغابايت.", reply, cancellationToken);
            return null;
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            logger.LogWarning("Telegram voice transcription failed ({ExceptionType}); audio content and identifiers omitted.", exception.GetType().Name);
            await SafeReply(message.Chat!.Id, "تعذر استلام الصوت أو فهمه حالياً. جرّب تبعته مرة ثانية بعد قليل.", reply, cancellationToken);
            return null;
        }
        finally
        {
            if (audioBytes.Length > 0) CryptographicOperations.ZeroMemory(audioBytes);
        }
    }

    private static AudioAttachment? GetAudioAttachment(Message message)
    {
        if (message.Voice is { } voice)
            return new AudioAttachment(voice.FileId, NormalizeAudioMimeType(voice.MimeType, "audio/ogg"), voice.FileSize);
        if (message.Audio is { } audio)
            return new AudioAttachment(audio.FileId, NormalizeAudioMimeType(audio.MimeType, "audio/mpeg"), audio.FileSize);
        if (message.Document is { MimeType: { } mimeType } document && mimeType.StartsWith("audio/", StringComparison.OrdinalIgnoreCase))
            return new AudioAttachment(document.FileId, NormalizeAudioMimeType(mimeType, "audio/ogg"), document.FileSize);
        return null;
    }

    private static string NormalizeAudioMimeType(string? mimeType, string fallback) =>
        string.IsNullOrWhiteSpace(mimeType) ? fallback : mimeType.Split(';', 2)[0].Trim().ToLowerInvariant();

    private static bool IsSupportedAudioMimeType(string mimeType) => mimeType is
        "audio/ogg" or "audio/opus" or "audio/mpeg" or "audio/mp3" or "audio/wav" or "audio/aac" or "audio/aiff" or "audio/flac" or "audio/m4a" or "audio/webm";

    private static bool IsSupportedImageMimeType(string mimeType) => mimeType is
        "image/jpeg" or "image/png" or "image/webp" or "image/heic" or "image/heif";

    public async Task HandleCallbackAsync(
        CallbackQuery callback,
        Func<long, string, CancellationToken, Task> reply,
        Func<string, string, CancellationToken, Task> answer,
        Func<long, int, string, CancellationToken, Task> edit,
        Func<long, string, IReadOnlyList<(string Text, string CallbackData)>, CancellationToken, Task> coursePicker,
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
            if (callback.Data.StartsWith("submit", StringComparison.Ordinal))
            {
                await HandleAssignmentSubmissionCallbackAsync(callback, reply, answer, edit, coursePicker, cancellationToken, () => answered = true);
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
                await SafeReply(chatId, "أوامر المسؤول:\n/start — ترحيب\n/admin — فتح لوحة الإدارة\n/link — ربط Moodle\n/status — حالة الربط\n/my_courses — المساقات\n/my_assignments — اختيار مساق لعرض الواجبات\n/my_exams — اختيار مساق لعرض الاختبارات\n/all_exams — عرض اختبارات جميع المساقات\n/reset_chat — مسح سياق المحادثة مع المساعد\n/logout — فصل Moodle\n/pending_users — الطلبات المعلقة\n/users — إحصاءات المستخدمين\n/approve <telegram_user_id> — قبول طلب معلق أو إعادة اعتماد مرفوض\n/reject <telegram_user_id> — رفض طلب معلق فقط\n/revoke <telegram_user_id> — سحب اعتماد مستخدم معتمد\n\nتقدر كمان تطلب مهام Moodle بطريقتك، مثل «بدي أربط Moodle»، «ورجيني واجباتي»، أو «شو امتحاناتي؟». " + ImageAnalysisHelp + " لا ترسل كلمات مرور أو بيانات حساسة؛ تُرسل الرسائل العادية إلى خدمة المساعد. ", reply, cancellationToken);
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

    private string AccessStatusText(TelegramRegistrationStatus? status) => status switch
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

    private async Task ReceiveAssignmentSubmissionContentAsync(Message message, AssignmentSubmissionFlow flow,
        Func<string, long, CancellationToken, Task<byte[]>> downloadFile,
        Func<long, string, CancellationToken, Task> reply,
        Func<long, string, IReadOnlyList<(string Text, string CallbackData)>, CancellationToken, Task> picker,
        CancellationToken cancellationToken)
    {
        if (flow.ExpiresAt < DateTimeOffset.UtcNow)
        {
            ClearAssignmentSubmissionFlow(message.From!.Id);
            await SafeReply(message.Chat!.Id, "انتهت مهلة تجهيز التسليم. أرسل /submit_assignment للبدء من جديد.", reply, cancellationToken);
            return;
        }
        if (flow.Method == MoodleAssignmentSubmissionMethod.OnlineText)
        {
            var text = message.Text?.Trim();
            if (string.IsNullOrWhiteSpace(text) || text.StartsWith('/'))
            {
                await SafeReply(message.Chat!.Id, "أرسل النص المطلوب أو رابط الفيديو برسالة عادية، أو /cancel للإلغاء.", reply, cancellationToken);
                return;
            }
            if (text.Length > 20_000)
            {
                await SafeReply(message.Chat!.Id, "النص طويل جداً. أرسله على أجزاء أقصر (حد أقصى 20 ألف حرف).", reply, cancellationToken);
                return;
            }
            flow = flow with { PendingText = text, PendingFileName = null, PendingMimeType = null, PendingFileBytes = null,
                Step = AssignmentSubmissionStep.Confirm, ExpiresAt = DateTimeOffset.UtcNow.AddMinutes(10) };
            _assignmentSubmissionFlows[message.From!.Id] = flow;
            var preview = text.Length > 1200 ? text[..1200] + "… (معاينة مختصرة)" : text;
            await picker(message.Chat!.Id, $"معاينة التسليم\nالمساق: {flow.CourseName}\nالواجب: {flow.Assignment!.Title}\nالطريقة: نص إلكتروني\n\n{preview}\n\nهل تؤكد حفظه وتسليمه في Moodle؟",
                [ ("تأكيد التسليم", $"submitconfirm:{flow.RequestId:N}"), ("إلغاء", $"submitcancel:{flow.RequestId:N}") ], cancellationToken);
            return;
        }

        var document = message.Document;
        if (document is null)
        {
            await SafeReply(message.Chat!.Id, "هذا الواجب يتطلب ملفاً. أرسله كمرفق Document للحفاظ على الملف الأصلي، أو /cancel للإلغاء.", reply, cancellationToken);
            return;
        }
        var fileName = string.IsNullOrWhiteSpace(document.FileName) ? "submission" : Path.GetFileName(document.FileName);
        var extension = Path.GetExtension(fileName).ToLowerInvariant();
        var accepted = flow.Info?.AcceptedExtensions ?? [];
        if (accepted.Count > 0 && (string.IsNullOrWhiteSpace(extension) || !accepted.Contains(extension, StringComparer.OrdinalIgnoreCase)))
        {
            await SafeReply(message.Chat!.Id, $"امتداد الملف غير مقبول. الامتدادات التي يعرضها Moodle: {string.Join(", ", accepted)}.", reply, cancellationToken);
            return;
        }
        var maxBytes = Math.Min(flow.Info?.MaxFileBytes ?? MaxAssignmentFileBytes, MaxAssignmentFileBytes);
        if (document.FileSize is > 0 && document.FileSize > maxBytes)
        {
            await SafeReply(message.Chat!.Id, $"حجم الملف يتجاوز الحد الذي يسمح به الواجب ({FormatBytes(maxBytes)}).", reply, cancellationToken);
            return;
        }
        byte[] bytes;
        try { bytes = await downloadFile(document.FileId, maxBytes, cancellationToken); }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            logger.LogWarning("Could not download a Moodle assignment submission attachment ({ExceptionType}).", exception.GetType().Name);
            await SafeReply(message.Chat!.Id, "تعذر استلام الملف من Telegram. جرّب إرساله مرة أخرى.", reply, cancellationToken);
            return;
        }
        if (bytes.Length == 0 || bytes.LongLength > maxBytes)
        {
            if (bytes.Length > 0) CryptographicOperations.ZeroMemory(bytes);
            await SafeReply(message.Chat!.Id, "الملف فارغ أو يتجاوز الحجم المسموح.", reply, cancellationToken);
            return;
        }
        flow = flow with { PendingText = null, PendingFileName = fileName, PendingMimeType = document.MimeType,
            PendingFileBytes = bytes, Step = AssignmentSubmissionStep.Confirm, ExpiresAt = DateTimeOffset.UtcNow.AddMinutes(10) };
        _assignmentSubmissionFlows[message.From!.Id] = flow;
        await picker(message.Chat!.Id, $"معاينة التسليم\nالمساق: {flow.CourseName}\nالواجب: {flow.Assignment!.Title}\nالطريقة: ملف\nاسم الملف: {fileName}\nالحجم: {FormatBytes(bytes.LongLength)}\n\nهل تؤكد رفعه وتسليمه في Moodle؟",
            [ ("تأكيد التسليم", $"submitconfirm:{flow.RequestId:N}"), ("إلغاء", $"submitcancel:{flow.RequestId:N}") ], cancellationToken);
    }

    private async Task HandleAssignmentSubmissionCallbackAsync(CallbackQuery callback,
        Func<long, string, CancellationToken, Task> reply,
        Func<string, string, CancellationToken, Task> answer,
        Func<long, int, string, CancellationToken, Task> edit,
        Func<long, string, IReadOnlyList<(string Text, string CallbackData)>, CancellationToken, Task> picker,
        CancellationToken cancellationToken, Action markAnswered)
    {
        if (callback.Message!.Chat.Type != ChatType.Private || callback.Message.Chat.Id != callback.From!.Id)
        { await answer(callback.Id, "استخدم هذه الخطوات في المحادثة الخاصة مع البوت.", cancellationToken); markAnswered(); return; }
        if (!await authorization.IsApprovedAsync(callback.From.Id, cancellationToken))
        { await answer(callback.Id, "حسابك غير معتمد لاستخدام خدمات Moodle.", cancellationToken); markAnswered(); return; }
        var parts = callback.Data!.Split(':', 3);
        if (parts.Length != 3 || !Guid.TryParseExact(parts[1], "N", out var requestId)
            || !_assignmentSubmissionFlows.TryGetValue(callback.From.Id, out var flow)
            || flow.RequestId != requestId || flow.ExpiresAt < DateTimeOffset.UtcNow)
        { ClearAssignmentSubmissionFlow(callback.From.Id); await answer(callback.Id, "انتهت صلاحية خطوات التسليم. أرسل /submit_assignment للبدء من جديد.", cancellationToken); markAnswered(); return; }

        if (parts[0] == "submitcancel")
        {
            ClearAssignmentSubmissionFlow(callback.From.Id);
            await answer(callback.Id, "تم إلغاء التسليم.", cancellationToken); markAnswered();
            try { await edit(callback.Message.Chat.Id, callback.Message.MessageId, "تم إلغاء التسليم.", cancellationToken); } catch (Exception ex) when (ex is not OperationCanceledException) { logger.LogDebug("Could not update cancelled submission prompt ({ExceptionType}).", ex.GetType().Name); }
            return;
        }
        if (parts[0] == "submitcourse" && flow.Step == AssignmentSubmissionStep.ChooseCourse && int.TryParse(parts[2], out var courseId)
            && flow.Courses.TryGetValue(courseId, out var courseName))
        {
            await answer(callback.Id, "جارٍ تحميل واجبات المساق...", cancellationToken); markAnswered();
            try { await edit(callback.Message.Chat.Id, callback.Message.MessageId, $"تم اختيار المساق: {courseName}", cancellationToken); } catch (Exception ex) when (ex is not OperationCanceledException) { logger.LogDebug("Could not update assignment submission course prompt ({ExceptionType}).", ex.GetType().Name); }
            await SafeReply(callback.Message.Chat.Id, "⏳ جاري تحميل واجبات المساق...", reply, cancellationToken);
            var result = await moodleBrowser.GetAssignmentsForCourseAsync(callback.From.Id, courseId, cancellationToken);
            if (!await authorization.IsApprovedAsync(callback.From.Id, cancellationToken))
            { ClearAssignmentSubmissionFlow(callback.From.Id); await SafeReply(callback.Message.Chat.Id, "تغير اعتماد حسابك أثناء الطلب؛ لم أعرض بيانات Moodle.", reply, cancellationToken); return; }
            if (result.Assignments.Count == 0)
            { _assignmentSubmissionFlows.TryRemove(callback.From.Id, out _); await SafeReply(callback.Message.Chat.Id, MoodleAssignmentMessageFormatter.Format(result, DateTimeOffset.UtcNow, moodleOptions.Value.TimeZoneId), reply, cancellationToken); return; }
            var assignments = result.Assignments.Take(100).ToArray();
            flow = flow with { Step = AssignmentSubmissionStep.ChooseAssignment, CourseId = courseId, CourseName = courseName, Assignments = assignments, ExpiresAt = DateTimeOffset.UtcNow.AddMinutes(15) };
            _assignmentSubmissionFlows[callback.From.Id] = flow;
            var choices = assignments.Select((assignment, index) => (TruncateButtonLabel(assignment.Title), $"submitassign:{requestId:N}:{index}" )).ToArray();
            await picker(callback.Message.Chat.Id, result.Kind == MoodleAssignmentsResultKind.Partial
                ? "اختر الواجب. ملاحظة: قائمة الواجبات جزئية، لذلك قد لا تظهر كلها."
                : "اختر الواجب الذي تريد تسليمه:", choices, cancellationToken);
            return;
        }
        if (parts[0] == "submitassign" && flow.Step == AssignmentSubmissionStep.ChooseAssignment && int.TryParse(parts[2], out var index)
            && index >= 0 && index < flow.Assignments.Count)
        {
            await answer(callback.Id, "جارٍ فحص إعدادات التسليم...", cancellationToken); markAnswered();
            var assignment = flow.Assignments[index];
            var info = await moodleBrowser.GetAssignmentSubmissionInfoAsync(callback.From.Id, assignment, cancellationToken);
            if (!await authorization.IsApprovedAsync(callback.From.Id, cancellationToken))
            { ClearAssignmentSubmissionFlow(callback.From.Id); await reply(callback.Message.Chat.Id, "تغير اعتماد حسابك أثناء الطلب؛ لم أعرض بيانات Moodle.", cancellationToken); return; }
            if (info.Kind != MoodleAssignmentSubmissionKind.Ready)
            { _assignmentSubmissionFlows.TryRemove(callback.From.Id, out _); await reply(callback.Message.Chat.Id, info.Message ?? "تعذر قراءة نموذج التسليم في Moodle.", cancellationToken); return; }
            flow = flow with { Step = AssignmentSubmissionStep.ChooseMethod, Assignment = assignment, Info = info, ExpiresAt = DateTimeOffset.UtcNow.AddMinutes(15) };
            _assignmentSubmissionFlows[callback.From.Id] = flow;
            if (info.AllowsOnlineText && info.AllowsFiles)
            {
                await picker(callback.Message.Chat.Id, "حدد طريقة التسليم التي يسمح بها Moodle. إذا كان المطلوب رابط فيديو، اختر النص الإلكتروني والصق الرابط.",
                    [ ("نص إلكتروني / رابط", $"submitmode:{requestId:N}:text"), ("رفع ملف", $"submitmode:{requestId:N}:file") ], cancellationToken);
            }
            else
            {
                var method = info.AllowsOnlineText ? MoodleAssignmentSubmissionMethod.OnlineText : MoodleAssignmentSubmissionMethod.File;
                flow = flow with { Step = AssignmentSubmissionStep.AwaitingContent, Method = method };
                _assignmentSubmissionFlows[callback.From.Id] = flow;
                await PromptForSubmissionContentAsync(callback.Message.Chat.Id, flow, reply, cancellationToken);
            }
            return;
        }
        if (parts[0] == "submitmode" && flow.Step == AssignmentSubmissionStep.ChooseMethod)
        {
            var method = parts[2] switch { "text" => MoodleAssignmentSubmissionMethod.OnlineText, "file" => MoodleAssignmentSubmissionMethod.File, _ => (MoodleAssignmentSubmissionMethod?)null };
            if (method is null || (method == MoodleAssignmentSubmissionMethod.OnlineText && flow.Info?.AllowsOnlineText != true)
                || (method == MoodleAssignmentSubmissionMethod.File && flow.Info?.AllowsFiles != true))
            { await answer(callback.Id, "طريقة التسليم غير متاحة لهذا الواجب.", cancellationToken); markAnswered(); return; }
            flow = flow with { Step = AssignmentSubmissionStep.AwaitingContent, Method = method, ExpiresAt = DateTimeOffset.UtcNow.AddMinutes(15) };
            _assignmentSubmissionFlows[callback.From.Id] = flow;
            await answer(callback.Id, "تم اختيار طريقة التسليم.", cancellationToken); markAnswered();
            await PromptForSubmissionContentAsync(callback.Message.Chat.Id, flow, reply, cancellationToken);
            return;
        }
        if (parts[0] == "submitconfirm" && flow.Step == AssignmentSubmissionStep.Confirm && flow.Assignment is not null && flow.Method is not null)
        {
            if (!_assignmentSubmissionFlows.TryRemove(callback.From.Id, out var confirmedFlow))
            { await answer(callback.Id, "تم استخدام هذا التأكيد مسبقاً.", cancellationToken); markAnswered(); return; }
            await answer(callback.Id, "جارٍ إرسال الواجب إلى Moodle...", cancellationToken); markAnswered();
            try { await edit(callback.Message.Chat.Id, callback.Message.MessageId, "جارٍ إرسال الواجب إلى Moodle...", cancellationToken); } catch (Exception ex) when (ex is not OperationCanceledException) { logger.LogDebug("Could not update submission confirmation ({ExceptionType}).", ex.GetType().Name); }
            try
            {
                if (!await authorization.IsApprovedAsync(callback.From.Id, cancellationToken))
                { await reply(callback.Message.Chat.Id, "تغير اعتماد حسابك؛ لم يتم إرسال الواجب.", cancellationToken); return; }
                var result = await moodleBrowser.SubmitAssignmentAsync(callback.From.Id, confirmedFlow.Assignment!, confirmedFlow.Method!.Value,
                    confirmedFlow.PendingText, confirmedFlow.PendingFileName, confirmedFlow.PendingMimeType, confirmedFlow.PendingFileBytes, cancellationToken);
                await reply(callback.Message.Chat.Id, result.Message, cancellationToken);
            }
            finally { if (confirmedFlow.PendingFileBytes is { Length: > 0 } data) CryptographicOperations.ZeroMemory(data); }
            return;
        }
        await answer(callback.Id, "هذه الخطوة غير صالحة أو تم استخدامها. أرسل /submit_assignment للبدء من جديد.", cancellationToken); markAnswered();
    }

    private async Task PromptForSubmissionContentAsync(long chatId, AssignmentSubmissionFlow flow,
        Func<long, string, CancellationToken, Task> reply, CancellationToken cancellationToken)
    {
        if (flow.Method == MoodleAssignmentSubmissionMethod.OnlineText)
        {
            var asksVideoLink = flow.Assignment?.Description is { } description
                && (description.Contains("فيديو", StringComparison.OrdinalIgnoreCase) || description.Contains("video", StringComparison.OrdinalIgnoreCase))
                && (description.Contains("رابط", StringComparison.OrdinalIgnoreCase) || description.Contains("لينك", StringComparison.OrdinalIgnoreCase)
                    || description.Contains("link", StringComparison.OrdinalIgnoreCase) || description.Contains("youtube", StringComparison.OrdinalIgnoreCase)
                    || description.Contains("google drive", StringComparison.OrdinalIgnoreCase));
            var instruction = asksVideoLink
                ? "قرأت في تعليمات الواجب أنها تطلب رابط فيديو؛ أرسل الرابط في رسالة عادية وسأضعه في محرر النص."
                : "أرسل النص الذي تريد وضعه في محرر Moodle (ويمكن أن يكون رابطاً إذا طلبه المدرّس).";
            await reply(chatId, $"{instruction} لن أرسله قبل أن أعرض لك معاينة وتضغط تأكيد التسليم.", cancellationToken);
        }
        else
        {
            var info = flow.Info!;
            var accepted = info.AcceptedExtensions.Count == 0 ? "لم يحدد Moodle امتدادات ظاهرة" : string.Join(", ", info.AcceptedExtensions);
            var asksPdf = flow.Assignment?.Description?.Contains("pdf", StringComparison.OrdinalIgnoreCase) == true;
            var maxSize = info.MaxFileBytes.HasValue
                ? $"حد Moodle الظاهر: {FormatBytes(Math.Min(info.MaxFileBytes.Value, MaxAssignmentFileBytes))}"
                : $"لم يظهر حد Moodle؛ حد الاستلام في البوت {FormatBytes(MaxAssignmentFileBytes)}";
            var instruction = asksPdf ? "تعليمات الواجب تذكر PDF؛ تأكد أن ملفك PDF." : "";
            await reply(chatId, $"{instruction} أرفق الملف كـ Document. الامتدادات المقبولة: {accepted}. {maxSize}. سأعرض معاينة وأطلب تأكيدك قبل التسليم.", cancellationToken);
        }
    }

    private static string FormatBytes(long bytes) => bytes >= 1024 * 1024
        ? $"{bytes / (1024d * 1024d):0.#} ميغابايت" : $"{bytes / 1024d:0.#} كيلوبايت";

    private void ClearAssignmentSubmissionFlow(long userId)
    {
        if (_assignmentSubmissionFlows.TryRemove(userId, out var old) && old.PendingFileBytes is { Length: > 0 } bytes)
            CryptographicOperations.ZeroMemory(bytes);
    }

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
    private sealed record ImageAttachment(string FileId, string MimeType, long? FileSize);
    private sealed record AudioAttachment(string FileId, string MimeType, long? FileSize);
    private enum AssignmentSubmissionStep { ChooseCourse, ChooseAssignment, ChooseMethod, AwaitingContent, Confirm }
    private sealed record AssignmentSubmissionFlow(Guid RequestId, AssignmentSubmissionStep Step,
        IReadOnlyDictionary<int, string> Courses, IReadOnlyList<MoodleAssignment> Assignments,
        int? CourseId, string? CourseName, MoodleAssignment? Assignment, MoodleAssignmentSubmissionInfo? Info,
        MoodleAssignmentSubmissionMethod? Method, string? PendingText, string? PendingFileName, string? PendingMimeType,
        byte[]? PendingFileBytes, DateTimeOffset ExpiresAt);
    private sealed record AssignmentCoursePicker(Guid RequestId, IReadOnlyDictionary<int, string> Courses, DateTimeOffset ExpiresAt);
}
