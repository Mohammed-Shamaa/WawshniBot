using Wawshni.Bot.Services;
namespace Wawshni.Bot;
public sealed class Worker(TelegramBotService bot, ILogger<Worker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try { await bot.RunAsync(stoppingToken); }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { logger.LogInformation("Telegram polling stopped."); }
        catch (Exception exception) { logger.LogCritical("Telegram worker stopped unexpectedly ({ExceptionType}).", exception.GetType().Name); throw; }
    }
}
