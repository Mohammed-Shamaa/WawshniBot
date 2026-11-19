using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Wawshni.Bot.Moodle;
using Wawshni.Bot.Services;
using Xunit;

namespace Wawshni.Bot.Tests.Integration;

public sealed class MoodleLiveSessionFactAttribute : FactAttribute
{
    public MoodleLiveSessionFactAttribute()
    {
        if ((string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("ConnectionStrings__DefaultConnection"))
                && string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("DATABASE_URL")))
            || string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("Moodle__SessionEncryptionKey"))
            || string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("TelegramBot__AuthorizedUserId")))
            Skip = "Requires Neon connection, Moodle session key, authorized Telegram user ID, and a saved Moodle session.";
    }
}

public sealed class MoodleCoursesLiveSessionTests
{
    [MoodleLiveSessionFact]
    public async Task Restores_saved_session_opens_courses_page_and_extracts_real_dom_data()
    {
        var configuration = new ConfigurationBuilder().AddEnvironmentVariables().Build();
        Assert.True(long.TryParse(configuration["TelegramBot:AuthorizedUserId"], out var telegramUserId));
        var options = new MoodleOptions();
        configuration.GetSection(MoodleOptions.SectionName).Bind(options);
        await using var database = new NeonDatabaseReadiness(configuration, NullLogger<NeonDatabaseReadiness>.Instance);

        var repository = new MoodleSessionRepository(database);
        var crypto = new MoodleSessionCrypto(Options.Create(options));
        var browser = new MoodleBrowserService(Options.Create(options), repository, crypto, NullLogger<MoodleBrowserService>.Instance);
        var result = await browser.GetCoursesAsync(telegramUserId, CancellationToken.None);

        Assert.True(result.Kind == MoodleLinkResultKind.NoCourses
            || result.Kind == MoodleLinkResultKind.Success && result.CoursesRetrieved && result.Courses.Count > 0,
            $"Expected an explicit Moodle empty state or real course links; received {result.Kind}.");
    }
}
