using System.Collections.Concurrent;
using Microsoft.Extensions.Options;
using Microsoft.Playwright;

namespace Wawshni.Bot.Moodle;

public enum MoodleLinkResultKind { Success, InvalidCredentials, ChallengeRequired, IdentityUnverified, NotConfigured, Unavailable, Expired }
public sealed record MoodleLinkResult(MoodleLinkResultKind Kind, IReadOnlyList<string> Courses, bool CoursesRetrieved);
public sealed record MoodleStatusResult(bool Connected, bool Expired);

/// <summary>Runs short-lived Chromium contexts only for account linking and Moodle commands.</summary>
public sealed class MoodleBrowserService(
    IOptions<MoodleOptions> options,
    MoodleSessionRepository sessions,
    MoodleSessionCrypto crypto,
    ILogger<MoodleBrowserService> logger)
{
    private readonly ConcurrentDictionary<long, SemaphoreSlim> _userLocks = new();

    public async Task<MoodleLinkResult> LinkAsync(long telegramUserId, string studentId, string password, CancellationToken cancellationToken)
    {
        var gate = _userLocks.GetOrAdd(telegramUserId, _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(cancellationToken);
        try
        {
            if (string.IsNullOrWhiteSpace(options.Value.SessionEncryptionKey)) return new(MoodleLinkResultKind.NotConfigured, [], false);
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(TimeSpan.FromSeconds(Math.Clamp(options.Value.LoginTimeoutSeconds, 15, 120)));
            using var playwright = await Playwright.CreateAsync();
            await using var browser = await playwright.Chromium.LaunchAsync(new BrowserTypeLaunchOptions
            {
                Headless = true,
                Args = ["--no-sandbox", "--disable-dev-shm-usage"]
            });
            var context = await browser.NewContextAsync();
            try
            {
                var page = await context.NewPageAsync();
                page.SetDefaultTimeout(10_000);
                await page.GotoAsync(new Uri(options.Value.BaseUrl, "login/index.php").ToString(), new() { WaitUntil = WaitUntilState.DOMContentLoaded, Timeout = 20_000 });
                if (!IsUniversityHost(page.Url)) return new(MoodleLinkResultKind.Unavailable, [], false);
                var sso = page.Locator("a[href*='/auth/saml2/login.php']");
                if (await sso.CountAsync() == 0)
                    return new(MoodleLinkResultKind.Unavailable, [], false);
                await sso.First.ClickAsync();
                await page.WaitForLoadStateAsync(LoadState.DOMContentLoaded, new() { Timeout = 20_000 }).ContinueOnTimeout();
                if (!IsUniversityHost(page.Url)) return new(MoodleLinkResultKind.Unavailable, [], false);
                if (await HasInteractiveChallengeAsync(page)) return new(MoodleLinkResultKind.ChallengeRequired, [], false);

                var passwordInput = page.Locator("input[type='password']").First;
                if (await page.Locator("input[type='password']").CountAsync() == 0)
                    return new(MoodleLinkResultKind.Unavailable, [], false);
                var usernameInput = page.Locator("input#username, input[name='username'], input[name*='user' i], input[type='email'], input[type='text']").First;
                if (await page.Locator("input#username, input[name='username'], input[name*='user' i], input[type='email'], input[type='text']").CountAsync() == 0)
                    return new(MoodleLinkResultKind.Unavailable, [], false);
                await usernameInput.FillAsync(studentId);
                await passwordInput.FillAsync(password);
                var submit = page.Locator("button[type='submit'], input[type='submit']").First;
                if (await page.Locator("button[type='submit'], input[type='submit']").CountAsync() == 0)
                    return new(MoodleLinkResultKind.Unavailable, [], false);
                await submit.ClickAsync();
                await page.WaitForLoadStateAsync(LoadState.DOMContentLoaded, new() { Timeout = 20_000 }).ContinueOnTimeout();
                if (!IsUniversityHost(page.Url)) return new(MoodleLinkResultKind.Unavailable, [], false);
                if (await HasInteractiveChallengeAsync(page)) return new(MoodleLinkResultKind.ChallengeRequired, [], false);

                var profileUrl = new Uri(options.Value.BaseUrl, "user/profile.php");
                await page.GotoAsync(profileUrl.ToString(), new() { WaitUntil = WaitUntilState.DOMContentLoaded, Timeout = 20_000 });
                var profileText = await page.Locator("body").InnerTextAsync();
                var authenticated = !page.Url.Contains("/login/", StringComparison.OrdinalIgnoreCase)
                    && await page.Locator("a[href*='logout'], a[href*='login/logout.php'], #user-menu-toggle, [data-region='user-menu']").CountAsync() > 0;
                if (!authenticated) return new(MoodleLinkResultKind.InvalidCredentials, [], false);
                if (!profileText.Contains(studentId, StringComparison.OrdinalIgnoreCase))
                    return new(MoodleLinkResultKind.IdentityUnverified, [], false);

                var state = await context.StorageStateAsync();
                var encrypted = crypto.Encrypt(telegramUserId, state);
                await sessions.SaveAsync(telegramUserId, studentId, encrypted, timeout.Token);
                try { return await ReadCoursesOnPageAsync(page, timeout.Token); }
                catch (Exception exception) when (exception is not OperationCanceledException)
                {
                    logger.LogWarning("Moodle login succeeded but course retrieval failed ({ExceptionType}).", exception.GetType().Name);
                    return new(MoodleLinkResultKind.Success, [], false);
                }
            }
            finally { await context.CloseAsync(); }
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            logger.LogWarning("Moodle operation timed out.");
            return new(MoodleLinkResultKind.Unavailable, [], false);
        }
        catch (InvalidOperationException exception) when (exception.Message.Contains("not configured", StringComparison.OrdinalIgnoreCase))
        {
            return new(MoodleLinkResultKind.NotConfigured, [], false);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            logger.LogWarning("Moodle login or session operation failed ({ExceptionType}).", exception.GetType().Name);
            return new(MoodleLinkResultKind.Unavailable, [], false);
        }
        finally { gate.Release(); }
    }

    public async Task<MoodleStatusResult> GetStatusAsync(long telegramUserId, CancellationToken cancellationToken)
    {
        var gate = _userLocks.GetOrAdd(telegramUserId, _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(cancellationToken);
        try
        {
            var stored = await sessions.GetAsync(telegramUserId, cancellationToken);
            if (stored is null) return new(false, false);
            return await WithStoredSessionAsync(stored, async page =>
            {
                await page.GotoAsync(new Uri(options.Value.BaseUrl, "my/").ToString(), new() { WaitUntil = WaitUntilState.DOMContentLoaded, Timeout = 15_000 });
                var valid = IsUniversityHost(page.Url) && !page.Url.Contains("/login/", StringComparison.OrdinalIgnoreCase);
                return new MoodleStatusResult(valid, !valid);
            }, cancellationToken);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            logger.LogWarning("Moodle session check failed ({ExceptionType}).", exception.GetType().Name);
            return new(false, true);
        }
        finally { gate.Release(); }
    }

    public async Task<MoodleLinkResult> GetCoursesAsync(long telegramUserId, CancellationToken cancellationToken)
    {
        var gate = _userLocks.GetOrAdd(telegramUserId, _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(cancellationToken);
        try
        {
            var stored = await sessions.GetAsync(telegramUserId, cancellationToken);
            if (stored is null) return new(MoodleLinkResultKind.Expired, [], false);
            return await WithStoredSessionAsync(stored, async page =>
            {
                return await ReadCoursesOnPageAsync(page, cancellationToken);
            }, cancellationToken);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            logger.LogWarning("Moodle course retrieval failed ({ExceptionType}).", exception.GetType().Name);
            return new(MoodleLinkResultKind.Unavailable, [], false);
        }
        finally { gate.Release(); }
    }

    private async Task<T> WithStoredSessionAsync<T>(StoredMoodleSession stored, Func<IPage, Task<T>> action, CancellationToken cancellationToken)
    {
        var state = crypto.Decrypt(stored.TelegramUserId, stored.Encrypted);
        using var playwright = await Playwright.CreateAsync();
        await using var browser = await playwright.Chromium.LaunchAsync(new BrowserTypeLaunchOptions { Headless = true, Args = ["--no-sandbox", "--disable-dev-shm-usage"] });
        var context = await browser.NewContextAsync(new BrowserNewContextOptions { StorageState = state });
        try
        {
            var page = await context.NewPageAsync();
            page.SetDefaultTimeout(10_000);
            return await action(page);
        }
        finally { await context.CloseAsync(); }
    }

    private static async Task<MoodleLinkResult> ReadCoursesOnPageAsync(IPage page, CancellationToken cancellationToken)
    {
        await page.GotoAsync(new Uri(new Uri(page.Url), "/my/courses.php").ToString(), new() { WaitUntil = WaitUntilState.DOMContentLoaded, Timeout = 15_000 });
        if (!IsUniversityHost(page.Url) || page.Url.Contains("/login/", StringComparison.OrdinalIgnoreCase))
            return new(MoodleLinkResultKind.Success, [], false);
        var links = page.Locator("a[href*='/course/view.php?id=']");
        var names = (await links.AllInnerTextsAsync()).Select(x => x.Trim()).Where(x => x.Length > 0).Distinct(StringComparer.Ordinal).Take(50).ToArray();
        return new(MoodleLinkResultKind.Success, names, true);
    }

    private static async Task<bool> HasInteractiveChallengeAsync(IPage page)
    {
        if (await page.Locator("iframe[src*='captcha' i], iframe[title*='captcha' i], .g-recaptcha, [id*='captcha' i], input[name*='otp' i], input[name*='totp' i]").CountAsync() > 0) return true;
        var text = await page.Locator("body").InnerTextAsync();
        return text.Contains("multi-factor", StringComparison.OrdinalIgnoreCase)
            || text.Contains("verification code", StringComparison.OrdinalIgnoreCase)
            || text.Contains("رمز التحقق", StringComparison.OrdinalIgnoreCase)
            || text.Contains("رمز المصادقة", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsUniversityHost(string url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || uri.Scheme != Uri.UriSchemeHttps) return false;
        return uri.Host.Equals("iugaza.edu.ps", StringComparison.OrdinalIgnoreCase)
            || uri.Host.EndsWith(".iugaza.edu.ps", StringComparison.OrdinalIgnoreCase);
    }
}

internal static class PlaywrightTaskExtensions
{
    public static async Task ContinueOnTimeout(this Task task)
    {
        try { await task; }
        catch (PlaywrightException) { }
    }
}
