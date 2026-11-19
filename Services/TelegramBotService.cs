using Telegram.Bot;
using Telegram.Bot.Exceptions;
using Telegram.Bot.Polling;
using Telegram.Bot.Types;
using Telegram.Bot.Types.Enums;
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
        await _client.ReceiveAsync(this, new ReceiverOptions { AllowedUpdates = [UpdateType.Message] }, cancellationToken);
    }
    public Task HandleUpdateAsync(ITelegramBotClient botClient, Update update, CancellationToken cancellationToken) =>
        update.Message is { } message
            ? _handler.HandleAsync(message, async (chatId, text, ct) => await botClient.SendMessage(chatId, text, cancellationToken: ct), cancellationToken)
            : Task.CompletedTask;
    public Task HandleErrorAsync(ITelegramBotClient botClient, Exception exception, HandleErrorSource source, CancellationToken cancellationToken)
    {
        if (exception is ApiRequestException apiException) _logger.LogError("Telegram API error {ErrorCode} while handling {Source}.", apiException.ErrorCode, source);
        else _logger.LogError(exception, "Telegram polling error from {Source}.", source);
        return Task.CompletedTask;
    }
}
