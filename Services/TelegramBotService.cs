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
    private TelegramBotClient? _client;
    private readonly MessageHandler _handler;
    private readonly ILogger<TelegramBotService> _logger;
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
        await _client.ReceiveAsync(this, new ReceiverOptions { AllowedUpdates = [UpdateType.Message, UpdateType.CallbackQuery] }, cancellationToken);
    }
    public async Task HandleUpdateAsync(ITelegramBotClient botClient, Update update, CancellationToken cancellationToken)
    {
        if (update.Message is { } message)
        {
            await _handler.HandleAsync(message,
                async (chatId, text, ct) => await botClient.SendMessage(chatId, text, cancellationToken: ct),
                async (chatId, messageId, ct) => await botClient.DeleteMessage(chatId, messageId, ct),
                async (chatId, text, callbackData, ct) => await botClient.SendMessage(chatId, text,
                    replyMarkup: new InlineKeyboardMarkup(new[]
                    {
                        new[]
                        {
                            InlineKeyboardButton.WithCallbackData("قبول الطلب ✅", $"reg:approve:{callbackData}"),
                            InlineKeyboardButton.WithCallbackData("رفض الطلب ❌", $"reg:reject:{callbackData}")
                        }
                    }), cancellationToken: ct),
                cancellationToken);
            return;
        }

        if (update.CallbackQuery is { } callback)
        {
            await _handler.HandleCallbackAsync(callback,
                async (chatId, text, ct) => await botClient.SendMessage(chatId, text, cancellationToken: ct),
                async (callbackId, text, ct) => await botClient.AnswerCallbackQuery(callbackId, text, cancellationToken: ct),
                async (chatId, messageId, text, ct) => await botClient.EditMessageText(chatId, messageId, text,
                    replyMarkup: new InlineKeyboardMarkup(Array.Empty<InlineKeyboardButton[]>()), cancellationToken: ct),
                cancellationToken);
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
        }
        else if (exception is ApiRequestException apiException) _logger.LogError("Telegram API error {ErrorCode} while handling {Source}.", apiException.ErrorCode, source);
        else _logger.LogError("Telegram polling error of type {ExceptionType} from {Source}.", exception.GetType().Name, source);
        return Task.CompletedTask;
    }
}
