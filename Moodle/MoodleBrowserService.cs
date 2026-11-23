using System.Collections.Concurrent;
using Microsoft.Extensions.Options;
using Microsoft.Playwright;

namespace Wawshni.Bot.Moodle;

public enum MoodleLinkResultKind { Success, InvalidCredentials, ChallengeRequired, IdentityUnverified, NotConfigured, Unavailable, Expired, NoCourses, CoursesNotDetected, UnexpectedRedirect, DashboardRedirect }
public sealed record MoodleLinkResult(MoodleLinkResultKind Kind, IReadOnlyList<string> Courses, bool CoursesRetrieved);
public sealed record MoodleStatusResult(bool Connected, bool Expired, bool Unavailable = false);

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
        var stage = "browser-initialization";
        try
        {
            if (string.IsNullOrWhiteSpace(options.Value.SessionEncryptionKey)) return new(MoodleLinkResultKind.NotConfigured, [], false);
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            var loginTimeoutMs = Math.Clamp(options.Value.LoginTimeoutSeconds, 15, 120) * 1000;
            timeout.CancelAfter(TimeSpan.FromMilliseconds(loginTimeoutMs));
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
                page.SetDefaultTimeout(20_000);
                page.SetDefaultNavigationTimeout(loginTimeoutMs);
                stage = "login-page-navigation";
                await page.GotoAsync(new Uri(options.Value.BaseUrl, "login/index.php").ToString(), new() { WaitUntil = WaitUntilState.DOMContentLoaded, Timeout = loginTimeoutMs });
                if (!IsUniversityHost(page.Url)) return new(MoodleLinkResultKind.Unavailable, [], false);
                stage = "sso-link-discovery";
                var sso = page.Locator("a[href*='/auth/saml2/login.php']");
                if (await sso.CountAsync() == 0)
                    return new(MoodleLinkResultKind.Unavailable, [], false);
                stage = "sso-navigation";
                await sso.First.ClickAsync();
                await page.WaitForLoadStateAsync(LoadState.DOMContentLoaded, new() { Timeout = loginTimeoutMs }).ContinueOnTimeout();
                if (!IsUniversityHost(page.Url)) return new(MoodleLinkResultKind.Unavailable, [], false);
                if (await HasInteractiveChallengeAsync(page)) return new(MoodleLinkResultKind.ChallengeRequired, [], false);

                stage = "credential-form-discovery";
                var passwordInput = page.Locator("input[type='password']").First;
                if (await page.Locator("input[type='password']").CountAsync() == 0)
                    return new(MoodleLinkResultKind.Unavailable, [], false);
                var usernameInput = page.Locator("input#username, input[name='username'], input[name*='user' i], input[type='email'], input[type='text']").First;
                if (await page.Locator("input#username, input[name='username'], input[name*='user' i], input[type='email'], input[type='text']").CountAsync() == 0)
                    return new(MoodleLinkResultKind.Unavailable, [], false);
                stage = "credential-form-submit";
                await usernameInput.FillAsync(studentId);
                await passwordInput.FillAsync(password);
                var submit = page.Locator("button[type='submit'], input[type='submit']").First;
                if (await page.Locator("button[type='submit'], input[type='submit']").CountAsync() == 0)
                    return new(MoodleLinkResultKind.Unavailable, [], false);
                await submit.ClickAsync();
                await page.WaitForLoadStateAsync(LoadState.DOMContentLoaded, new() { Timeout = loginTimeoutMs }).ContinueOnTimeout();
                if (!IsUniversityHost(page.Url)) return new(MoodleLinkResultKind.Unavailable, [], false);
                if (await HasInteractiveChallengeAsync(page)) return new(MoodleLinkResultKind.ChallengeRequired, [], false);

                stage = "authenticated-profile-validation";
                var profileUrl = new Uri(options.Value.BaseUrl, "user/profile.php");
                await page.GotoAsync(profileUrl.ToString(), new() { WaitUntil = WaitUntilState.DOMContentLoaded, Timeout = loginTimeoutMs });
                // This context starts without cookies, and the student ID above was submitted to the IUG login form.
                // Moodle's profile page does not necessarily display usernames, so authenticate via its session marker.
                var authenticated = IsUniversityHost(page.Url)
                    && !page.Url.Contains("/login/", StringComparison.OrdinalIgnoreCase)
                    && await page.Locator("a[href*='logout'], a[href*='login/logout.php'], #user-menu-toggle, [data-region='user-menu']").CountAsync() > 0;
                if (!authenticated) return new(MoodleLinkResultKind.InvalidCredentials, [], false);

                stage = "session-persistence";
                var state = await context.StorageStateAsync();
                var encrypted = crypto.Encrypt(telegramUserId, state);
                // Moodle navigation may consume the whole interaction timeout. Keep persistence
                // independent so an authenticated session is not discarded because course
                // discovery took too long.
                await sessions.SaveAsync(telegramUserId, studentId, encrypted, cancellationToken);
                stage = "initial-course-retrieval";
                try { return await ReadCoursesOnPageAsync(page, timeout.Token); }
                catch (OperationCanceledException) when (timeout.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
                {
                    logger.LogWarning("Moodle account was authenticated and its session saved, but initial course retrieval timed out.");
                    return new(MoodleLinkResultKind.Success, [], false);
                }
                catch (Exception exception) when (exception is not OperationCanceledException)
                {
                    logger.LogWarning("Moodle login succeeded but course retrieval failed ({ExceptionType}).", exception.GetType().Name);
                    return new(MoodleLinkResultKind.Success, [], false);
                }
            }
            finally { await context.CloseAsync(); }
        }
        catch (OperationCanceledException exception) when (!cancellationToken.IsCancellationRequested)
        {
            logger.LogWarning("Moodle link operation timed out during stage {Stage} ({ExceptionType}).", stage, exception.GetType().Name);
            return new(MoodleLinkResultKind.Unavailable, [], false);
        }
        catch (InvalidOperationException exception) when (exception.Message.Contains("not configured", StringComparison.OrdinalIgnoreCase))
        {
            return new(MoodleLinkResultKind.NotConfigured, [], false);
        }
        catch (TimeoutException exception)
        {
            logger.LogWarning("Moodle login timed out during stage {Stage} ({ExceptionType}).", stage, exception.GetType().Name);
            return new(MoodleLinkResultKind.Unavailable, [], false);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            logger.LogWarning("Moodle login or session operation failed during stage {Stage} ({ExceptionType}).", stage, exception.GetType().Name);
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
                IResponse? response;
                try
                {
                    response = await page.GotoAsync(new Uri(options.Value.BaseUrl, "my/").ToString(),
                        new() { WaitUntil = WaitUntilState.DOMContentLoaded, Timeout = 25_000 });
                }
                catch (TimeoutException exception)
                {
                    logger.LogWarning("Moodle session check timed out while opening the dashboard ({ExceptionType}); the saved session was retained.", exception.GetType().Name);
                    return new MoodleStatusResult(false, false, true);
                }

                if (page.Url.Contains("/login/", StringComparison.OrdinalIgnoreCase))
                    return new MoodleStatusResult(false, true);
                if (!IsUniversityHost(page.Url) || response?.Status is >= 400)
                    return new MoodleStatusResult(false, false, true);
                return new MoodleStatusResult(true, false);
            }, cancellationToken);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            logger.LogWarning("Moodle session check failed ({ExceptionType}); the saved session was retained.", exception.GetType().Name);
            return new(false, false, true);
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

    public async Task<MoodleAssignmentCourseListResult> GetAssignmentCoursesAsync(long telegramUserId, CancellationToken cancellationToken)
    {
        var gate = _userLocks.GetOrAdd(telegramUserId, _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(cancellationToken);
        try
        {
            logger.LogInformation("Loading enrolled Moodle courses for assignment selection using the saved encrypted session.");
            var stored = await sessions.GetAsync(telegramUserId, cancellationToken);
            if (stored is null) return new(MoodleAssignmentsResultKind.Expired, []);
            return await WithStoredSessionAsync(stored, async page => await ReadAssignmentCoursesOnPageAsync(page, cancellationToken), cancellationToken);
        }
        catch (System.Security.Cryptography.CryptographicException exception)
        {
            logger.LogError("Saved Moodle session failed authenticated decryption ({ExceptionType}).", exception.GetType().Name);
            return new(MoodleAssignmentsResultKind.SessionRestoreFailed, []);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            logger.LogError("Moodle assignment course-list retrieval failed ({ExceptionType}). Safe message: {SafeMessage}. Stack trace: {StackTrace}",
                exception.GetType().FullName, SafeExceptionMessage(exception), exception.StackTrace);
            return new(MoodleAssignmentsResultKind.Unavailable, []);
        }
        finally { gate.Release(); }
    }

    public Task<MoodleAssignmentsResult> GetAssignmentsAsync(long telegramUserId, CancellationToken cancellationToken) =>
        GetAssignmentsCoreAsync(telegramUserId, null, cancellationToken);

    public Task<MoodleAssignmentsResult> GetAssignmentsForCourseAsync(long telegramUserId, int courseId, CancellationToken cancellationToken) =>
        GetAssignmentsCoreAsync(telegramUserId, courseId, cancellationToken);

    private async Task<MoodleAssignmentsResult> GetAssignmentsCoreAsync(long telegramUserId, int? selectedCourseId, CancellationToken cancellationToken)
    {
        var gate = _userLocks.GetOrAdd(telegramUserId, _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(cancellationToken);
        try
        {
            logger.LogInformation("Starting Moodle assignment retrieval using the saved encrypted session; selected course filter {SelectedCourseId}.", selectedCourseId);
            var stored = await sessions.GetAsync(telegramUserId, cancellationToken);
            if (stored is null) return new(MoodleAssignmentsResultKind.Expired, []);
            return await WithStoredSessionAsync(stored, async page => await ReadAssignmentsOnPageAsync(page, cancellationToken, selectedCourseId), cancellationToken);
        }
        catch (System.Security.Cryptography.CryptographicException exception)
        {
            logger.LogError("Saved Moodle session failed authenticated decryption ({ExceptionType}).", exception.GetType().Name);
            return new(MoodleAssignmentsResultKind.SessionRestoreFailed, []);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            logger.LogError("Moodle assignment retrieval failed ({ExceptionType}). Safe message: {SafeMessage}. Stack trace: {StackTrace}",
                exception.GetType().FullName, SafeExceptionMessage(exception), exception.StackTrace);
            return new(MoodleAssignmentsResultKind.Unavailable, []);
        }
        finally { gate.Release(); }
    }

    private async Task<MoodleAssignmentCourseListResult> ReadAssignmentCoursesOnPageAsync(IPage page, CancellationToken cancellationToken)
    {
        var target = MoodleCoursePageUrl.FromBaseUrl(options.Value.BaseUrl);
        var response = await page.GotoAsync(target.ToString(), new() { WaitUntil = WaitUntilState.DOMContentLoaded, Timeout = 25_000 });
        logger.LogInformation("Moodle assignment course picker loaded; final URL {FinalUrl}; HTTP status {StatusCode}.", SafeUrl(page.Url), response?.Status.ToString() ?? "unavailable");
        if (!IsUniversityHost(page.Url) || response?.Status is >= 400) return new(MoodleAssignmentsResultKind.Unavailable, []);
        if (page.Url.Contains("/login/", StringComparison.OrdinalIgnoreCase)) return new(MoodleAssignmentsResultKind.Expired, []);

        var foundLinks = new List<CourseLink>();
        var visited = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var current = new Uri(page.Url);
        var incomplete = false;
        for (var pageNumber = 0; pageNumber < 50; pageNumber++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!visited.Add(current.AbsoluteUri)) { incomplete = true; break; }
            var anchors = page.Locator("a[href*='course/view.php']").Visible;
            if (pageNumber == 0)
            {
                try { await anchors.First.WaitForAsync(new() { State = WaitForSelectorState.Visible, Timeout = 12_000 }); }
                catch (PlaywrightException) { /* Inspect a possible explicit empty-course state below. */ }
            }
            var pageLinks = await anchors.EvaluateAllAsync<CourseLink[]>("els => els.map(a => ({ href: a.href, name: (a.innerText || a.getAttribute('aria-label') || a.getAttribute('title') || '').trim() })).filter(x => x.href)");
            foundLinks.AddRange(pageLinks);
            var nextLink = page.Locator(".pagination a[rel='next'], nav[aria-label*='pagination' i] a[aria-label*='next' i], nav[aria-label*='التصفح'] a[aria-label*='التالي'], a[aria-label='Next page'], a[aria-label='الصفحة التالية']");
            if (await nextLink.CountAsync() == 0) break;
            if (pageNumber == 49) { incomplete = true; break; }
            var href = await nextLink.First.GetAttributeAsync("href");
            if (!Uri.TryCreate(current, href, out var nextUri) || !IsUniversityHost(nextUri.ToString())) { incomplete = true; break; }
            current = nextUri;
            var pageResponse = await page.GotoAsync(current.ToString(), new() { WaitUntil = WaitUntilState.DOMContentLoaded, Timeout = 20_000 });
            if (!IsUniversityHost(page.Url) || page.Url.Contains("/login/", StringComparison.OrdinalIgnoreCase)) return new(MoodleAssignmentsResultKind.Expired, []);
            if (pageResponse?.Status is >= 400) { incomplete = true; break; }
        }

        var links = MoodleCourseDomExtractor.NormalizeLinks(foundLinks.Select(link => new MoodleCourseLink(link.Name, link.Href)), options.Value.BaseUrl);
        var courses = MoodleCourseDomExtractor.ToAssignmentOptions(links);
        logger.LogInformation("Moodle assignment picker found {CourseCount} enrolled courses; course list incomplete {Incomplete}.", courses.Count, incomplete);
        if (courses.Count == 0)
            return await HasExplicitEmptyCourseMessageAsync(page)
                ? new(MoodleAssignmentsResultKind.Success, [])
                : new(MoodleAssignmentsResultKind.ExtractionFailed, []);
        return new(incomplete ? MoodleAssignmentsResultKind.Partial : MoodleAssignmentsResultKind.Success, courses);
    }

    private async Task<MoodleAssignmentsResult> ReadAssignmentsOnPageAsync(IPage page, CancellationToken cancellationToken, int? selectedCourseId = null)
    {
        var target = MoodleCoursePageUrl.FromBaseUrl(options.Value.BaseUrl);
        var response = await page.GotoAsync(target.ToString(), new() { WaitUntil = WaitUntilState.DOMContentLoaded, Timeout = 25_000 });
        logger.LogInformation("Moodle assignments stage {Stage}: final URL {FinalUrl}; page title {PageTitle}; HTTP status {StatusCode}.",
            "course-list-navigation", SafeUrl(page.Url), await page.TitleAsync(), response?.Status.ToString() ?? "unavailable");
        if (!IsUniversityHost(page.Url)) return new(MoodleAssignmentsResultKind.Unavailable, []);
        if (page.Url.Contains("/login/", StringComparison.OrdinalIgnoreCase)) return new(MoodleAssignmentsResultKind.Expired, []);
        if (response?.Status is >= 400) return new(MoodleAssignmentsResultKind.Unavailable, []);

        var courses = new List<CourseLink>();
        var coursePages = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var coursePageUrl = new Uri(page.Url);
        var coursePaginationTruncated = false;
        var coursePaginationFailed = false;
        for (var pageNumber = 0; pageNumber < 50; pageNumber++)
        {
            if (!coursePages.Add(coursePageUrl.AbsoluteUri))
            {
                coursePaginationFailed = true;
                logger.LogWarning("Moodle course list pagination repeated a page URL; retaining courses already discovered.");
                break;
            }
            var courseAnchors = page.Locator("a[href*='course/view.php']").Visible;
            if (pageNumber == 0)
            {
                try { await courseAnchors.First.WaitForAsync(new() { State = WaitForSelectorState.Visible, Timeout = 12_000 }); }
                catch (PlaywrightException) { /* Log the observed counts below; absence alone does not mean no enrollment. */ }
            }
            var pageCourses = await courseAnchors.EvaluateAllAsync<CourseLink[]>("els => els.map(a => ({ href: a.href, name: (a.innerText || a.getAttribute('aria-label') || a.getAttribute('title') || '').trim() })).filter(x => x.href)");
            courses.AddRange(pageCourses);
            if (pageNumber == 0)
                logger.LogInformation("Moodle assignments stage {Stage}: visible course links {VisibleCourseLinkCount}; page {PageNumber}.", "course-link-discovery", pageCourses.Length, pageNumber + 1);
            var nextLink = page.Locator(".pagination a[rel='next'], nav[aria-label*='pagination' i] a[aria-label*='next' i], nav[aria-label*='التصفح'] a[aria-label*='التالي'], a[aria-label='Next page'], a[aria-label='الصفحة التالية']");
            if (await nextLink.CountAsync() == 0) break;
            if (pageNumber == 49) { coursePaginationTruncated = true; break; }
            var nextHref = await nextLink.First.GetAttributeAsync("href");
            if (!Uri.TryCreate(coursePageUrl, nextHref, out var nextUri) || !IsUniversityHost(nextUri.ToString()))
            {
                coursePaginationFailed = true;
                logger.LogWarning("Moodle course list pagination link was invalid; retaining courses already discovered.");
                break;
            }
            coursePageUrl = nextUri;
            IResponse? coursePageResponse;
            try { coursePageResponse = await page.GotoAsync(coursePageUrl.ToString(), new() { WaitUntil = WaitUntilState.DOMContentLoaded, Timeout = 20_000 }); }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                coursePaginationFailed = true;
                logger.LogWarning("Moodle course list pagination failed ({ExceptionType}); retaining courses already discovered.", exception.GetType().Name);
                break;
            }
            if (!IsUniversityHost(page.Url) || page.Url.Contains("/login/", StringComparison.OrdinalIgnoreCase)) return new(MoodleAssignmentsResultKind.Expired, []);
            if (coursePageResponse?.Status is >= 400)
            {
                coursePaginationFailed = true;
                logger.LogWarning("Moodle course list pagination returned HTTP {StatusCode}; retaining courses already discovered.", coursePageResponse.Status);
                break;
            }
        }
        var allDiscoveredCourses = MoodleCourseDomExtractor.NormalizeLinks(
            courses.Select(c => new MoodleCourseLink(c.Name, c.Href)), options.Value.BaseUrl);
        logger.LogInformation("Moodle assignments stage {Stage}: unique enrolled courses {CourseCount} from {CourseLinkCount} links.",
            "course-link-normalization", allDiscoveredCourses.Count, courses.Count);
        var distinctCourses = selectedCourseId.HasValue
            ? allDiscoveredCourses.Where(course => int.TryParse(System.Web.HttpUtility.ParseQueryString(new Uri(course.Href).Query)["id"], out var id) && id == selectedCourseId.Value).ToList()
            : allDiscoveredCourses.ToList();
        if (selectedCourseId.HasValue && distinctCourses.Count == 0)
        {
            logger.LogWarning("Selected Moodle course {CourseId} is not in the current user's enrolled course list.", selectedCourseId.Value);
            return new(MoodleAssignmentsResultKind.ExtractionFailed, []);
        }
        if (distinctCourses.Count == 0)
        {
            logger.LogWarning("Moodle assignments stage {Stage}: no course URLs passed validation; final URL {FinalUrl}; page title {PageTitle}.",
                "course-link-normalization", SafeUrl(page.Url), await page.TitleAsync());
            if (await HasExplicitEmptyCourseMessageAsync(page))
                return new(MoodleAssignmentsResultKind.Success, []);
            return new(MoodleAssignmentsResultKind.ExtractionFailed, []);
        }

        var collected = new List<MoodleAssignment>();
        var failures = coursePaginationTruncated || coursePaginationFailed ? 1 : 0;
        var recognizedCoursePages = 0;
        var coursesInspected = 0;
        var coursesWithoutAssignments = 0;
        var coursesFailed = 0;
        var assignmentsDiscovered = 0;
        var assignmentDetailsInspected = 0;
        var assignmentDetailsFailed = 0;
        var sessionExpiredDuringScan = false;
        if (coursePaginationTruncated) logger.LogWarning("Moodle course pagination reached its safety limit; assignment results may be partial.");
        var courseOrdinal = 0;
        foreach (var course in distinctCourses)
        {
            courseOrdinal++;
            var stage = "course-id";
            var courseFailed = false;
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                var courseUri = new Uri(course.Href);
                var courseId = System.Web.HttpUtility.ParseQueryString(courseUri.Query).Get("id");
                if (!int.TryParse(courseId, out var id)) { failures++; coursesFailed++; logger.LogWarning("Moodle assignment stage {Stage}: could not read course id for course #{CourseOrdinal}.", "course-id", courseOrdinal); continue; }
                var indexUrl = new Uri(options.Value.BaseUrl, $"mod/assign/index.php?id={id}");
                var courseActivityUrl = new Uri(options.Value.BaseUrl, $"course/view.php?id={id}");
                stage = "course-page-navigation";
                logger.LogInformation("Moodle assignment stage {Stage}: opening course page for course #{CourseOrdinal}; URL {Url}.",
                    "course-page-navigation", courseOrdinal, SafeUrl(courseActivityUrl.ToString()));
                var coursePageResponse = await page.GotoAsync(courseActivityUrl.ToString(), new() { WaitUntil = WaitUntilState.DOMContentLoaded, Timeout = 20_000 });
                logger.LogInformation("Moodle course page result for course #{CourseOrdinal}: final URL {FinalUrl}; page title {PageTitle}; HTTP status {StatusCode}.",
                    courseOrdinal, SafeUrl(page.Url), await page.TitleAsync(), coursePageResponse?.Status.ToString() ?? "unavailable");
                if (!IsUniversityHost(page.Url) || page.Url.Contains("/login/", StringComparison.OrdinalIgnoreCase))
                {
                    failures++;
                    coursesFailed++;
                    sessionExpiredDuringScan = true;
                    logger.LogWarning("Moodle session expired or left the university host while opening course #{CourseOrdinal}; preserving assignments already collected.", courseOrdinal);
                    break;
                }
                if (coursePageResponse?.Status is >= 400)
                {
                    failures++;
                    coursesFailed++;
                    logger.LogWarning("Moodle course page returned HTTP {StatusCode} for course #{CourseOrdinal}; it was not treated as an empty course.", coursePageResponse.Status, courseOrdinal);
                    continue;
                }
                stage = "course-page-dom";
                var coursePageLinks = await ReadVisibleAssignmentLinksAsync(page);
                var indexData = new AssignmentIndexPageResult([], false, false);
                try
                {
                    stage = "assignment-index-pagination";
                    logger.LogInformation("Moodle assignment stage {Stage}: checking the course assignment index for course #{CourseOrdinal}; URL {Url}.",
                        "assignment-index-navigation", courseOrdinal, SafeUrl(indexUrl.ToString()));
                    var indexResult = await ReadAssignmentLinksAsync(page, indexUrl, cancellationToken);
                    if (indexResult is null)
                    {
                        failures++;
                        coursesFailed++;
                        sessionExpiredDuringScan = true;
                        logger.LogWarning("Moodle session expired while opening the assignment index for course #{CourseOrdinal}; preserving assignments already collected.", courseOrdinal);
                        break;
                    }
                    indexData = indexResult;
                }
                catch (Exception exception) when (exception is not OperationCanceledException)
                {
                    failures++;
                    courseFailed = true;
                    logger.LogWarning("Moodle assignment index failed for course #{CourseOrdinal} ({ExceptionType}); safe message: {SafeMessage}.",
                        courseOrdinal, exception.GetType().Name, SafeExceptionMessage(exception));
                }
                var allCourseLinks = coursePageLinks.Concat(indexData.Links)
                    .Select(link => new MoodleAssignmentLink(link.Href, link.Title));
                var normalizedCourseLinks = MoodleAssignmentExtractor.NormalizeLinks(allCourseLinks, options.Value.BaseUrl)
                    .Select(link => new AssignmentLink { Href = link.Href, Title = link.Title }).ToArray();
                indexData = new AssignmentIndexPageResult(normalizedCourseLinks, indexData.Truncated,
                    indexData.Recognized || coursePageLinks.Length > 0);
                if (indexData.Truncated) { failures++; courseFailed = true; }
                if (!indexData.Recognized)
                {
                    failures++;
                    courseFailed = true;
                    logger.LogWarning("Moodle assignment stage {Stage}: neither assignment links nor a recognized empty state appeared for course #{CourseOrdinal}; final URL {FinalUrl}; page title {PageTitle}.",
                        "assignment-index-dom", courseOrdinal, SafeUrl(page.Url), await page.TitleAsync());
                    coursesFailed++;
                    continue;
                }
                recognizedCoursePages++;
                coursesInspected++;
                assignmentsDiscovered += indexData.Links.Length;
                if (indexData.Links.Length == 0 && !courseFailed) coursesWithoutAssignments++;
                logger.LogInformation("Moodle assignment stage {Stage}: detected {AssignmentLinkCount} assignment activities in course #{CourseOrdinal}.",
                    "assignment-link-discovery", indexData.Links.Length, courseOrdinal);
                foreach (var link in indexData.Links)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    stage = "assignment-details";
                    try
                    {
                        var assignment = await ReadAssignmentDetailAsync(page, link, course.Name, cancellationToken);
                        if (assignment is not null)
                        {
                            collected.Add(assignment);
                            assignmentDetailsInspected++;
                        }
                        else
                        {
                            failures++;
                            assignmentDetailsFailed++;
                            if (page.Url.Contains("/login/", StringComparison.OrdinalIgnoreCase))
                            {
                                courseFailed = true;
                                sessionExpiredDuringScan = true;
                                logger.LogWarning("Moodle session expired while inspecting assignment details for course #{CourseOrdinal}; stopping further navigation and preserving collected assignments.", courseOrdinal);
                                break;
                            }
                        }
                    }
                    catch (Exception exception) when (exception is not OperationCanceledException)
                    {
                        failures++;
                        assignmentDetailsFailed++;
                        logger.LogWarning("Moodle assignment details failed for course #{CourseOrdinal} ({ExceptionType}); safe message: {SafeMessage}.",
                            courseOrdinal, exception.GetType().Name, SafeExceptionMessage(exception));
                    }
                }
                if (courseFailed) coursesFailed++;
                if (sessionExpiredDuringScan) break;
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                failures++;
                coursesFailed++;
                logger.LogWarning("Moodle assignment retrieval failed at stage {Stage} for course #{CourseOrdinal} ({ExceptionType}); safe message: {SafeMessage}. Stack trace: {StackTrace}",
                    stage, courseOrdinal, exception.GetType().Name, SafeExceptionMessage(exception), exception.StackTrace);
            }
        }

        if (sessionExpiredDuringScan) coursesFailed += distinctCourses.Count - courseOrdinal;

        var assignments = MoodleAssignmentExtractor.Deduplicate(collected);
        var summary = new MoodleAssignmentRetrievalSummary(selectedCourseId.HasValue ? 1 : distinctCourses.Count,
            selectedCourseId.HasValue || !coursePaginationTruncated && !coursePaginationFailed,
            coursesInspected, coursesWithoutAssignments, coursesFailed, assignmentsDiscovered, assignmentDetailsInspected, assignmentDetailsFailed);
        var result = MoodleAssignmentExtractor.CreateResult(assignments, failures, recognizedCoursePages > 0) with { Summary = summary };
        logger.LogInformation("Moodle assignment retrieval completed: result {ResultKind}; courses total {TotalCourses}, inspected {CoursesInspected}, empty {CoursesWithoutAssignments}, failed {CoursesFailed}; assignments discovered {AssignmentsDiscovered}, detail pages inspected {DetailsInspected}, failed {DetailsFailed}; extracted {ExtractedAssignmentCount}; other failures {FailureCount}.",
            result.Kind, summary.TotalCourses, summary.CoursesInspected, summary.CoursesWithoutAssignments, summary.CoursesFailed,
            summary.AssignmentsDiscovered, summary.AssignmentDetailsInspected, summary.AssignmentDetailsFailed, result.Assignments.Count, failures);
        return result;
    }

    private async Task<AssignmentLink[]> ReadVisibleAssignmentLinksAsync(IPage page, bool waitForVisible = true)
    {
        var selector = "a[href*='/mod/assign/view.php?id=']";
        var links = page.Locator(selector).Visible;
        if (waitForVisible)
        {
            try { await links.First.WaitForAsync(new() { State = WaitForSelectorState.Visible, Timeout = 5_000 }); }
            catch (Exception exception) when (exception is PlaywrightException or TimeoutException)
            {
                // No activity links may be valid on a course page; the assignment index is checked afterward.
            }
        }
        var found = await links.EvaluateAllAsync<AssignmentLink[]>("els => els.map(a => ({ href: a.href, title: (a.innerText || a.getAttribute('aria-label') || a.getAttribute('title') || '').trim() })).filter(x => x.href)");
        return MoodleAssignmentExtractor.NormalizeLinks(
                found.Select(link => new MoodleAssignmentLink(link.Href, link.Title)), options.Value.BaseUrl)
            .Select(link => new AssignmentLink { Href = link.Href, Title = link.Title })
            .ToArray();
    }

    private async Task<AssignmentIndexPageResult?> ReadAssignmentLinksAsync(IPage page, Uri firstPage, CancellationToken cancellationToken)
    {
        var found = new List<AssignmentLink>();
        var visited = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var recognized = false;
        Uri? next = firstPage;
        for (var pageNumber = 0; next is not null && pageNumber < 50; pageNumber++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var current = next;
            next = null;
            if (!visited.Add(current.AbsoluteUri)) break;
            var response = await page.GotoAsync(current.ToString(), new() { WaitUntil = WaitUntilState.DOMContentLoaded, Timeout = 20_000 });
            logger.LogInformation("Moodle assignment index page loaded; HTTP status {StatusCode}; pagination page {PageNumber}.", response?.Status.ToString() ?? "unavailable", pageNumber + 1);
            if (!IsUniversityHost(page.Url) || page.Url.Contains("/login/", StringComparison.OrdinalIgnoreCase)) return null;
            if (response?.Status is >= 400) throw new InvalidOperationException("Moodle assignment index returned an unsuccessful HTTP status.");
            await page.Locator("a[href*='/mod/assign/view.php?id='], .generaltable, [data-region='assignments'], .activity-list").First.WaitForAsync(new() { State = WaitForSelectorState.Visible, Timeout = 7_000 }).ContinueOnTimeout();
            var pageLinks = await ReadVisibleAssignmentLinksAsync(page, waitForVisible: false);
            found.AddRange(pageLinks);
            var hasAssignmentTable = await page.Locator(".generaltable, [data-region='assignments'], .activity-list").CountAsync() > 0;
            var hasEmptyMessage = await HasExplicitEmptyAssignmentMessageAsync(page);
            recognized |= pageLinks.Length > 0 || hasAssignmentTable || hasEmptyMessage;
            var nextLink = page.Locator(".pagination a[rel='next'], nav[aria-label*='pagination' i] a[aria-label*='next' i], nav[aria-label*='التصفح'] a[aria-label*='التالي'], a[aria-label='Next page'], a[aria-label='الصفحة التالية']");
            var nextHref = await nextLink.CountAsync() > 0 ? await nextLink.First.GetAttributeAsync("href") : null;
            if (!string.IsNullOrWhiteSpace(nextHref) && Uri.TryCreate(current, nextHref, out var nextUri) && IsUniversityHost(nextUri.ToString())) next = nextUri;
        }
        var truncated = next is not null;
        if (truncated) logger.LogWarning("Moodle assignment index pagination reached its safety limit; collected records may be partial.");
        var links = MoodleAssignmentExtractor.NormalizeLinks(
                found.Select(link => new MoodleAssignmentLink(link.Href, link.Title)), options.Value.BaseUrl)
            .Select(link => new AssignmentLink { Href = link.Href, Title = link.Title }).ToArray();
        return new AssignmentIndexPageResult(links, truncated, recognized);
    }

    private static async Task<bool> HasExplicitEmptyAssignmentMessageAsync(IPage page)
    {
        var body = await page.Locator("body").InnerTextAsync();
        return body.Contains("no assignments", StringComparison.OrdinalIgnoreCase)
            || body.Contains("no activities", StringComparison.OrdinalIgnoreCase)
            || body.Contains("there are no assignments", StringComparison.OrdinalIgnoreCase)
            || body.Contains("لا توجد واجبات", StringComparison.OrdinalIgnoreCase)
            || body.Contains("لا يوجد واجبات", StringComparison.OrdinalIgnoreCase)
            || body.Contains("لا توجد تكليفات", StringComparison.OrdinalIgnoreCase)
            || body.Contains("لا توجد أنشطة", StringComparison.OrdinalIgnoreCase);
    }

    private async Task<MoodleAssignment?> ReadAssignmentDetailAsync(IPage page, AssignmentLink link, string courseName, CancellationToken cancellationToken)
    {
        if (!Uri.TryCreate(link.Href, UriKind.Absolute, out var uri) || !IsUniversityHost(uri.ToString())) return null;
        var response = await page.GotoAsync(uri.ToString(), new() { WaitUntil = WaitUntilState.DOMContentLoaded, Timeout = 20_000 });
        logger.LogInformation("Moodle assignment details page loaded; HTTP status {StatusCode}.", response?.Status.ToString() ?? "unavailable");
        if (!IsUniversityHost(page.Url) || page.Url.Contains("/login/", StringComparison.OrdinalIgnoreCase) || response?.Status is >= 400) return null;
        try { await page.Locator("#page-mod-assign-view, [data-region='assignment-info'], .activity-header, .submissionstatustable, .activity-dates").First.WaitForAsync(new() { State = WaitForSelectorState.Visible, Timeout = 5_000 }); }
        catch (PlaywrightException) { /* Still inspect the page snapshot; course details may use another theme layout. */ }
        await page.WaitForLoadStateAsync(LoadState.NetworkIdle, new() { Timeout = 4_000 }).ContinueOnTimeout();

        var dataJson = await page.EvaluateAsync<string>("""
        () => {
          const text = e => (e?.innerText || e?.textContent || '').replace(/\s+/g, ' ').trim();
          const title = text(document.querySelector('h1')) || text(document.querySelector('[data-region="activity-header"] h1'));
          const body = text(document.body);
          const bodyLines = (document.body?.innerText || '').split(/\r?\n/).flatMap(line => line.split('\t')).map(line => line.replace(/\s+/g, ' ').trim()).filter(Boolean);
          const timeValues = [...document.querySelectorAll('time[datetime], time[data-timestamp]')].map(e => ({ label: text(e.parentElement?.parentElement || e.parentElement), value: e.getAttribute('datetime') || e.getAttribute('data-timestamp') }));
          // Moodle themes/plugins may omit the standard table classes; include all semantic table rows.
          const rowElements = [...new Set(document.querySelectorAll('tr, [role="row"]'))];
          const rows = rowElements.map(r => {
            const cells = [...r.querySelectorAll(':scope > th, :scope > td, :scope > [role="rowheader"], :scope > [role="cell"]')];
            const labelCell = cells.find(c => c.tagName === 'TH' || c.getAttribute('role') === 'rowheader') || cells[0];
            const valueCells = cells.filter(c => c !== labelCell);
            const label = text(labelCell) || text(r.querySelector('.col-1, dt'));
            const value = valueCells.map(text).filter(Boolean).join(' ') || text(r.querySelector('.col-2, dd'));
            const time = r.querySelector('time[datetime], time[data-timestamp]');
            const timestamp = r.querySelector('[data-timestamp]')?.getAttribute('data-timestamp');
            return { label, value, iso: time?.getAttribute('datetime') || time?.getAttribute('data-timestamp') || timestamp || null };
          }).filter(r => r.label);
          for (const term of document.querySelectorAll('dl')) {
            const terms = [...term.querySelectorAll(':scope > dt')];
            for (const dt of terms) {
              const dd = dt.nextElementSibling;
              if (dd?.tagName === 'DD') rows.push({ label: text(dt), value: text(dd), iso: dd.querySelector('time[datetime]')?.getAttribute('datetime') || dd.querySelector('[data-timestamp]')?.getAttribute('data-timestamp') || null });
            }
          }
          // Moodle themes can render assignment dates in the activity header rather than
          // the submission status table. Read each labelled activity-dates item separately.
          for (const container of document.querySelectorAll('.activity-dates, [data-region="activity-dates"]')) {
            const entries = [...container.children].length ? [...container.children] : [container];
            for (const entry of entries) {
              const labelNode = entry.querySelector('strong, b, dt, .activity-date-label');
              let label = text(labelNode).replace(/[：:]$/, '').trim();
              let value = '';
              if (labelNode) {
                const copy = entry.cloneNode(true);
                const copyLabel = copy.querySelector('strong, b, dt, .activity-date-label');
                copyLabel?.remove();
                value = text(copy);
              } else {
                const parts = text(entry).split(/[：:]/, 2);
                if (parts.length === 2) [label, value] = parts.map(part => part.trim());
              }
              const time = entry.querySelector('time[datetime], time[data-timestamp]');
              const timestamp = entry.querySelector('[data-timestamp]')?.getAttribute('data-timestamp');
              if (label && (value || time || timestamp))
                rows.push({ label, value: value || text(time), iso: time?.getAttribute('datetime') || time?.getAttribute('data-timestamp') || timestamp || null });
            }
          }
          for (const element of document.querySelectorAll('[data-due-date], [data-duedate], [data-cutoff-date], [data-cutoffdate]')) {
            const due = element.getAttribute('data-due-date') || element.getAttribute('data-duedate');
            const cutoff = element.getAttribute('data-cutoff-date') || element.getAttribute('data-cutoffdate');
            if (due) rows.push({ label: 'Due date', value: '', iso: due });
            if (cutoff) rows.push({ label: 'Cut-off date', value: '', iso: cutoff });
          }
          const description = text(document.querySelector('.activity-description, [data-region="activity-description"], #intro'));
          return JSON.stringify({ title, body, bodyLines, description, timeValues, rows });
        }
        """);
        var data = MoodleAssignmentExtractor.ParsePageSnapshot(dataJson);
        logger.LogInformation("Moodle assignment page snapshot contains {RowCount} table rows and {BodyLineCount} text lines; submission status label detected {SubmissionStatusDetected}.",
            data.Rows.Count, data.BodyLines.Count, data.Rows.Any(row => row.Label.Contains("submission status", StringComparison.OrdinalIgnoreCase)
                || row.Label.Contains("حالة التسليم", StringComparison.OrdinalIgnoreCase)
                || row.Label.Contains("حالة الإرسال", StringComparison.OrdinalIgnoreCase)
                || row.Label.Contains("حالة التقديم", StringComparison.OrdinalIgnoreCase)
                || data.BodyLines.Any(line => line.Contains("submission status", StringComparison.OrdinalIgnoreCase)
                    || line.Contains("حالة التسليم", StringComparison.OrdinalIgnoreCase)
                    || line.Contains("حالة الإرسال", StringComparison.OrdinalIgnoreCase)
                    || line.Contains("حالة التقديم", StringComparison.OrdinalIgnoreCase))));
        return MoodleAssignmentExtractor.ParseAssignmentPage(data, SafeAssignmentUrl(page.Url), courseName, options.Value.TimeZoneId);
    }

    private sealed class CourseLink { public string Href { get; set; } = ""; public string Name { get; set; } = ""; }
    private sealed class AssignmentLink { public string Href { get; set; } = ""; public string Title { get; set; } = ""; }
    private sealed record AssignmentIndexPageResult(AssignmentLink[] Links, bool Truncated, bool Recognized);
    private static string SafeAssignmentUrl(string value)
    {
        if (!Uri.TryCreate(value, UriKind.Absolute, out var uri)) return value;
        var id = System.Web.HttpUtility.ParseQueryString(uri.Query).Get("id");
        var builder = new UriBuilder(uri) { Query = string.IsNullOrWhiteSpace(id) ? string.Empty : "id=" + Uri.EscapeDataString(id), Fragment = string.Empty };
        return builder.Uri.AbsoluteUri;
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

    public static async Task<string?> ContinueWithOnTimeout(this Task<string?> task)
    {
        try { return await task; }
        catch (PlaywrightException) { return null; }
    }
}
