using System.Collections.Concurrent;
using Microsoft.Extensions.Options;
using Microsoft.Playwright;

namespace Wawshni.Bot.Moodle;

public enum MoodleLinkResultKind { Success, InvalidCredentials, ChallengeRequired, IdentityUnverified, NotConfigured, Unavailable, Expired, NoCourses, CoursesNotDetected, UnexpectedRedirect, DashboardRedirect }
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
                // This context starts without cookies, and the student ID above was submitted to the IUG login form.
                // Moodle's profile page does not necessarily display usernames, so authenticate via its session marker.
                var authenticated = IsUniversityHost(page.Url)
                    && !page.Url.Contains("/login/", StringComparison.OrdinalIgnoreCase)
                    && await page.Locator("a[href*='logout'], a[href*='login/logout.php'], #user-menu-toggle, [data-region='user-menu']").CountAsync() > 0;
                if (!authenticated) return new(MoodleLinkResultKind.InvalidCredentials, [], false);

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
            logger.LogInformation("Starting Moodle course retrieval; loading the authorized account's encrypted browser session.");
            var stored = await sessions.GetAsync(telegramUserId, cancellationToken);
            if (stored is null) return new(MoodleLinkResultKind.Expired, [], false);
            logger.LogInformation("Encrypted Moodle session record was found.");
            return await WithStoredSessionAsync(stored, async page =>
            {
                return await ReadCoursesOnPageAsync(page, cancellationToken);
            }, cancellationToken);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            logger.LogError("Moodle course retrieval failed ({ExceptionType}). Safe message: {SafeMessage}. Stack trace: {StackTrace}",
                exception.GetType().FullName, SafeExceptionMessage(exception), exception.StackTrace);
            return new(MoodleLinkResultKind.Unavailable, [], false);
        }
        finally { gate.Release(); }
    }

    private async Task<T> WithStoredSessionAsync<T>(StoredMoodleSession stored, Func<IPage, Task<T>> action, CancellationToken cancellationToken)
    {
        logger.LogInformation("Decrypting the saved Moodle browser state in memory.");
        var state = crypto.Decrypt(stored.TelegramUserId, stored.Encrypted);
        logger.LogInformation("Moodle browser state decrypted successfully; values are not logged.");
        using var playwright = await Playwright.CreateAsync();
        await using var browser = await playwright.Chromium.LaunchAsync(new BrowserTypeLaunchOptions { Headless = true, Args = ["--no-sandbox", "--disable-dev-shm-usage"] });
        var context = await browser.NewContextAsync(new BrowserNewContextOptions { StorageState = state });
        var cookies = await context.CookiesAsync();
        using var storage = System.Text.Json.JsonDocument.Parse(state);
        var originCount = storage.RootElement.TryGetProperty("origins", out var origins) && origins.ValueKind == System.Text.Json.JsonValueKind.Array ? origins.GetArrayLength() : 0;
        logger.LogInformation("Playwright context created with persisted storage state (cookie count {CookieCount}, origin count {OriginCount}); cookie values omitted.", cookies.Count, originCount);
        try
        {
            var page = await context.NewPageAsync();
            page.SetDefaultTimeout(10_000);
            return await action(page);
        }
        finally { await context.CloseAsync(); }
    }

    private async Task<MoodleLinkResult> ReadCoursesOnPageAsync(IPage page, CancellationToken cancellationToken)
    {
        // A newly restored context starts at about:blank. Always derive the target from configured Moodle BaseUrl.
        var target = MoodleCoursePageUrl.FromBaseUrl(options.Value.BaseUrl);
        logger.LogInformation("Navigating Playwright to Moodle courses page {TargetUrl}.", SafeUrl(target.ToString()));
        var response = await page.GotoAsync(target.ToString(), new() { WaitUntil = WaitUntilState.DOMContentLoaded, Timeout = 25_000 });
        var statusCode = response?.Status;
        logger.LogInformation("Moodle courses navigation completed: final URL {FinalUrl}; HTTP status {StatusCode}.", SafeUrl(page.Url), statusCode?.ToString() ?? "unavailable");

        if (!IsUniversityHost(page.Url))
        {
            logger.LogWarning("Moodle courses navigation left the university domain; final URL {FinalUrl}.", SafeUrl(page.Url));
            return new(MoodleLinkResultKind.UnexpectedRedirect, [], false);
        }
        if (page.Url.Contains("/login/", StringComparison.OrdinalIgnoreCase))
        {
            logger.LogWarning("Restored Moodle session was redirected to the login page.");
            return new(MoodleLinkResultKind.Expired, [], false);
        }
        if (statusCode is >= 400)
        {
            logger.LogWarning("Moodle courses endpoint returned HTTP {StatusCode}.", statusCode);
            return new(MoodleLinkResultKind.Unavailable, [], false);
        }

        const string linksSelector = "a[href*='course/view.php']";
        var links = page.Locator(linksSelector).Visible;
        try
        {
            await links.First.WaitForAsync(new LocatorWaitForOptions { State = WaitForSelectorState.Visible, Timeout = 12_000 });
        }
        catch (PlaywrightException)
        {
            // Some Moodle pages render the course list asynchronously; collect a safe DOM summary below.
        }

        var linkCount = await links.CountAsync();
        var legacyCardCount = await page.Locator(".coursebox").CountAsync();
        var dashboardCardCount = await page.Locator(".dashboard-card").CountAsync();
        var courseContentCount = await page.Locator("[data-region='course-content-item'], [data-region='course-content'] .card").CountAsync();
        logger.LogInformation("Moodle course DOM summary: matching course links {LinkCount}, legacy cards {LegacyCardCount}, dashboard cards {DashboardCardCount}, course content items {CourseContentCount}.", linkCount, legacyCardCount, dashboardCardCount, courseContentCount);

        if (linkCount == 0)
        {
            var pagePath = new Uri(page.Url).AbsolutePath;
            logger.LogWarning("No recognized course links appeared after waiting for dynamic content. Page path: {PagePath}; candidate counts: legacy={LegacyCount}, dashboard={DashboardCount}, content={ContentCount}.", SafePath(pagePath), legacyCardCount, dashboardCardCount, courseContentCount);
            if (await HasExplicitEmptyCourseMessageAsync(page))
            {
                logger.LogInformation("Moodle displayed an explicit empty-course message.");
                return new(MoodleLinkResultKind.NoCourses, [], true);
            }
            if (pagePath.TrimEnd('/').Equals("/my", StringComparison.OrdinalIgnoreCase)
                || pagePath.Equals("/my/index.php", StringComparison.OrdinalIgnoreCase))
            {
                logger.LogWarning("Moodle redirected the courses request to the dashboard without rendering course links.");
                return new(MoodleLinkResultKind.DashboardRedirect, [], false);
            }
            return new(MoodleLinkResultKind.CoursesNotDetected, [], false);
        }

        var names = MoodleCourseDomExtractor.NormalizeNames(await links.AllInnerTextsAsync());
        logger.LogInformation("Moodle course extraction completed; extracted {CourseCount} course names.", names.Count);
        return names.Count == 0
            ? new(MoodleLinkResultKind.CoursesNotDetected, [], false)
            : new(MoodleLinkResultKind.Success, names, true);
    }

    private static async Task<bool> HasExplicitEmptyCourseMessageAsync(IPage page)
    {
        var emptyMessage = page.Locator("[data-region='empty-message'], .empty-state, .no-courses");
        if (await emptyMessage.CountAsync() > 0)
        {
            var text = string.Join(" ", await emptyMessage.AllInnerTextsAsync());
            if (ContainsExplicitEmptyMessage(text)) return true;
        }

        var alerts = page.Locator("[role='alert'], .alert-info");
        var alertText = string.Join(" ", await alerts.AllInnerTextsAsync());
        return ContainsExplicitEmptyMessage(alertText);
    }

    private static bool ContainsExplicitEmptyMessage(string text) =>
        text.Contains("you are not enrolled in any courses", StringComparison.OrdinalIgnoreCase)
        || text.Contains("no courses available", StringComparison.OrdinalIgnoreCase)
        || text.Contains("no courses found", StringComparison.OrdinalIgnoreCase)
        || text.Contains("لا توجد مساقات", StringComparison.OrdinalIgnoreCase)
        || text.Contains("لا يوجد مساقات", StringComparison.OrdinalIgnoreCase)
        || text.Contains("لا توجد مقررات", StringComparison.OrdinalIgnoreCase);

    private static string SafeUrl(string value)
    {
        if (!Uri.TryCreate(value, UriKind.Absolute, out var uri)) return "unavailable";
        // Keep origin and path for redirect diagnosis; never include user info, query, or fragment values.
        return $"{uri.Scheme}://{uri.Host}{SafePath(uri.AbsolutePath)}";
    }

    private static string SafePath(string path) =>
        System.Text.RegularExpressions.Regex.Replace(path, @"/(?=[A-Za-z0-9_-]{20,}(?:/|$))[A-Za-z0-9_-]+", "/[redacted]");

    private static string SafeExceptionMessage(Exception exception)
    {
        var message = System.Text.RegularExpressions.Regex.Replace(
            exception.Message,
            @"https?://[^\s""'<>]+",
            match => SafeUrl(match.Value.TrimEnd(',', '.', ')', ']')));
        return System.Text.RegularExpressions.Regex.Replace(
            message,
            @"(?i)\b(password|passwd|token|cookie|set-cookie|authorization|sesskey|session|samlresponse|assertion)\b\s*[:=]\s*[^,;\s&]+",
            "$1=[redacted]");
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
