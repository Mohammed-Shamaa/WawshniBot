using System.Net;

namespace Wawshni.Bot.Services;

/// <summary>Periodically requests this app's liveness endpoint while the process is running.</summary>
public sealed class SelfKeepAliveService(IConfiguration configuration, ILogger<SelfKeepAliveService> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var configuredUrl = configuration["KeepAlive:Url"];
        if (!Uri.TryCreate(configuredUrl, UriKind.Absolute, out var healthUri)
            || healthUri.Scheme != Uri.UriSchemeHttps
            || !string.Equals(healthUri.AbsolutePath.TrimEnd('/'), "/health", StringComparison.Ordinal))
        {
            logger.LogInformation("Self keep-alive is disabled; set KeepAlive__Url to the public HTTPS /health URL to enable it.");
            return;
        }

        if (!int.TryParse(configuration["KeepAlive:IntervalMinutes"], out var intervalMinutes))
            intervalMinutes = 10;
        intervalMinutes = Math.Clamp(intervalMinutes, 5, 14);

        using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(15) };
        using var timer = new PeriodicTimer(TimeSpan.FromMinutes(intervalMinutes));
        logger.LogInformation("Self keep-alive enabled with interval {IntervalMinutes} minutes.", intervalMinutes);

        try
        {
            while (await timer.WaitForNextTickAsync(stoppingToken))
            {
                try
                {
                    using var response = await client.GetAsync(healthUri, stoppingToken);
                    if (response.IsSuccessStatusCode)
                        logger.LogDebug("Self keep-alive health request succeeded with HTTP {StatusCode}.", (int)response.StatusCode);
                    else
                        logger.LogWarning("Self keep-alive health request returned HTTP {StatusCode}.", (int)response.StatusCode);
                }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { throw; }
                catch (HttpRequestException exception)
                {
                    logger.LogWarning("Self keep-alive request failed ({ExceptionType}).", exception.GetType().Name);
                }
                catch (TaskCanceledException)
                {
                    logger.LogWarning("Self keep-alive request timed out.");
                }
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { }
    }
}
