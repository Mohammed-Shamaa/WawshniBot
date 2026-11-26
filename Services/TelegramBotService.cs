using Telegram.Bot;
using Telegram.Bot.Exceptions;
using Telegram.Bot.Polling;
using Telegram.Bot.Types;
using Telegram.Bot.Types.Enums;
using Telegram.Bot.Types.ReplyMarkups;
using Wawshni.Bot.Handlers;
using Microsoft.Extensions.Options;
namespace Wawshni.Bot.Services;
public sealed class TelegramBotService : IUpdateHandler
{
    private static readonly TimeSpan[] PollingConflictBackoff =
    [
        TimeSpan.FromSeconds(5),
        TimeSpan.FromSeconds(15),
        TimeSpan.FromSeconds(30)
    ];
    private TelegramBotClient? _client;
    private readonly MessageHandler _handler;
    private readonly ILogger<TelegramBotService> _logger;
    private CancellationTokenSource? _pollingCancellation;
    private int _pollingActive;
    private int _pollingConflictDetected;
    private long _lastSuccessfulUpdateTicks;
    public bool PollingActive => Volatile.Read(ref _pollingActive) == 1;
    public DateTimeOffset? LastSuccessfulUpdate
    {
        get
        {
            var ticks = Interlocked.Read(ref _lastSuccessfulUpdateTicks);
            return ticks == 0 ? null : new DateTimeOffset(ticks, TimeSpan.Zero);
        }
    }
    public TelegramBotService(IOptions<TelegramOptions> options, MessageHandler handler, ILogger<TelegramBotService> logger)
    { _options = options; _handler = handler; _logger = logger; }
    private readonly IOptions<TelegramOptions> _options;
    public async Task RunAsync(CancellationToken cancellationToken)
    {
        var token = _options.Value.Token;
        if (string.IsNullOrWhiteSpace(token))
        {
            _logger.LogWarning("TelegramBot:Token is not configured; Telegram polling is disabled.");
            return;
        }
        _client = new TelegramBotClient(token);
        var me = await _client.GetMe(cancellationToken);
        _logger.LogInformation("Starting long polling for @{BotUsername}.", me.Username);
        var conflictAttempt = 0;
        while (!cancellationToken.IsCancellationRequested)
        {
            using var pollingCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            _pollingCancellation = pollingCancellation;
            Interlocked.Exchange(ref _pollingConflictDetected, 0);
            Volatile.Write(ref _pollingActive, 1);
            var conflictDetected = false;
            try
            {
                await _client.ReceiveAsync(this,
                    new ReceiverOptions { AllowedUpdates = [UpdateType.Message, UpdateType.CallbackQuery] },
                    pollingCancellation.Token);
            }
            catch (OperationCanceledException) when (pollingCancellation.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
            {
                conflictDetected = Volatile.Read(ref _pollingConflictDetected) == 1;
            }
            finally
            {
                _pollingCancellation = null;
                Volatile.Write(ref _pollingActive, 0);
            }

            if (!conflictDetected) return;
            if (conflictAttempt >= PollingConflictBackoff.Length)
            {
                _logger.LogCritical("Telegram polling stopped after repeated HTTP 409 conflicts. Stop the other process polling this token or use a token dedicated to this service.");
                return;
            }

            var delay = PollingConflictBackoff[conflictAttempt++];
            _logger.LogWarning("Telegram polling received HTTP 409; retrying after {DelaySeconds} seconds (retry {RetryNumber}/{RetryLimit}).",
                delay.TotalSeconds, conflictAttempt, PollingConflictBackoff.Length);
            await Task.Delay(delay, cancellationToken);
        }
    }
    public async Task<bool> TrySendMessageAsync(long chatId, string text, CancellationToken cancellationToken)
    {
        if (_client is null) return false;
        try { await _client.SendMessage(chatId, text, cancellationToken: cancellationToken); return true; }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            _logger.LogWarning("Telegram notification delivery failed ({ExceptionType}).", exception.GetType().Name);
            return false;
        }
    }
    public async Task HandleUpdateAsync(ITelegramBotClient botClient, Update update, CancellationToken cancellationToken)
    {
        if (update.Message is { } message)
        {
            await _handler.HandleAsync(message,
                async (chatId, text, ct) => await botClient.SendMessage(chatId, text, cancellationToken: ct),
                async (chatId, messageId, ct) => await botClient.DeleteMessage(chatId, messageId, ct),
                async (fileId, maxBytes, ct) =>
                {
                    var telegramFile = await botClient.GetFile(fileId, ct);
                    if (telegramFile.FileSize is > 0 && telegramFile.FileSize > maxBytes)
                        throw new InvalidDataException("Telegram image exceeds the configured limit.");
                    await using var buffer = new MemoryStream();
                    await botClient.DownloadFile(telegramFile, buffer, ct);
                    if (buffer.Length > maxBytes)
                        throw new InvalidDataException("Telegram image exceeds the configured limit.");
                    return buffer.ToArray();
                },
                async (chatId, text, callbackData, ct) => await botClient.SendMessage(chatId, text,
                    replyMarkup: new InlineKeyboardMarkup(new[]
                    {
                        new[]
                        {
                            InlineKeyboardButton.WithCallbackData("قبول الطلب ✅", $"reg:approve:{callbackData}"),
                            InlineKeyboardButton.WithCallbackData("رفض الطلب ❌", $"reg:reject:{callbackData}")
                        }
                    }), cancellationToken: ct),
                async (chatId, text, choices, ct) => await botClient.SendMessage(chatId, text,
                    replyMarkup: new InlineKeyboardMarkup(choices.Select(choice =>
                        new[] { InlineKeyboardButton.WithCallbackData(choice.Text, choice.CallbackData) })), cancellationToken: ct),
                cancellationToken);
            Interlocked.Exchange(ref _lastSuccessfulUpdateTicks, DateTimeOffset.UtcNow.Ticks);
            return;
        }

        if (update.CallbackQuery is { } callback)
        {
            await _handler.HandleCallbackAsync(callback,
                async (chatId, text, ct) => await botClient.SendMessage(chatId, text, cancellationToken: ct),
                async (callbackId, text, ct) => await botClient.AnswerCallbackQuery(callbackId, text, cancellationToken: ct),
                async (chatId, messageId, text, ct) => await botClient.EditMessageText(chatId, messageId, text,
                    replyMarkup: new InlineKeyboardMarkup(Array.Empty<InlineKeyboardButton[]>()), cancellationToken: ct),
                async (chatId, text, choices, ct) => await botClient.SendMessage(chatId, text,
                    replyMarkup: new InlineKeyboardMarkup(choices.Select(choice =>
                        new[] { InlineKeyboardButton.WithCallbackData(choice.Text, choice.CallbackData) })), cancellationToken: ct),
                cancellationToken);
            Interlocked.Exchange(ref _lastSuccessfulUpdateTicks, DateTimeOffset.UtcNow.Ticks);
        }
    }
    public Task HandleErrorAsync(ITelegramBotClient botClient, Exception exception, HandleErrorSource source, CancellationToken cancellationToken)
    {
        if (exception is ApiRequestException { ErrorCode: 409 } conflict)
        {
            var description = conflict.Message;
            var cause = description.Contains("webhook", StringComparison.OrdinalIgnoreCase)
                ? "active-webhook"
                : description.Contains("getupdates", StringComparison.OrdinalIgnoreCase)
                    ? "another-poller"
                    : "unknown-conflict";
            _logger.LogError("Telegram polling conflict (HTTP 409, {Cause}) while handling {Source}.", cause, source);
            if (source == HandleErrorSource.PollingError && _pollingCancellation is { } pollingCancellation)
            {
                Interlocked.Exchange(ref _pollingConflictDetected, 1);
                _logger.LogWarning("Stopping this polling attempt after HTTP 409 so it can retry cleanly after backoff.");
                pollingCancellation.Cancel();
            }
                _pollingCancellation?.Cancel();
        }
        else if (exception is ApiRequestException apiException) _logger.LogError("Telegram API error {ErrorCode} while handling {Source}.", apiException.ErrorCode, source);
        else _logger.LogError("Telegram polling error of type {ExceptionType} from {Source}.", exception.GetType().Name, source);
        return Task.CompletedTask;
    }
}
