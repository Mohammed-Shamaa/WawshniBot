namespace Wawshni.Bot.Moodle;

public enum MoodleLinkResultKind { Success, InvalidCredentials, ChallengeRequired, IdentityUnverified, NotConfigured, Unavailable, Expired, NoCourses, CoursesNotDetected, UnexpectedRedirect, DashboardRedirect }
public sealed record MoodleLinkResult(MoodleLinkResultKind Kind, IReadOnlyList<string> Courses, bool CoursesRetrieved);
public sealed record MoodleStatusResult(bool Connected, bool Expired, bool Unavailable = false);

public sealed class MoodleBrowserService
{
    public Task<MoodleLinkResult> LinkAsync(long userId, string studentId, string password, CancellationToken cancellationToken) =>
        Task.FromResult(new MoodleLinkResult(MoodleLinkResultKind.Unavailable, [], false));
    public Task<MoodleStatusResult> GetStatusAsync(long userId, CancellationToken cancellationToken) =>
        Task.FromResult(new MoodleStatusResult(false, false));
    public Task<MoodleLinkResult> GetCoursesAsync(long userId, CancellationToken cancellationToken) =>
        Task.FromResult(new MoodleLinkResult(MoodleLinkResultKind.Unavailable, [], false));
    public Task<MoodleAssignmentsResult> GetAssignmentsAsync(long userId, CancellationToken cancellationToken) =>
        Task.FromResult(new MoodleAssignmentsResult(MoodleAssignmentsResultKind.Unavailable, []));
    public Task<MoodleAssignmentCourseListResult> GetAssignmentCoursesAsync(long userId, CancellationToken cancellationToken) =>
        Task.FromResult(new MoodleAssignmentCourseListResult(MoodleAssignmentsResultKind.Unavailable, []));
    public Task<MoodleAssignmentsResult> GetAssignmentsForCourseAsync(long userId, int courseId, CancellationToken cancellationToken) =>
        Task.FromResult(new MoodleAssignmentsResult(MoodleAssignmentsResultKind.Unavailable, []));
}

public sealed class MoodleSessionRepository
{
    public Task DeleteAsync(long userId, CancellationToken cancellationToken) => Task.CompletedTask;
}
