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
    public Task<MoodleAssignmentSubmissionInfo> GetAssignmentSubmissionInfoAsync(long userId, MoodleAssignment assignment, CancellationToken cancellationToken) =>
        Task.FromResult(new MoodleAssignmentSubmissionInfo(MoodleAssignmentSubmissionKind.Unavailable, assignment.Title, false, false, [], null, 0));
    public Task<MoodleAssignmentSubmissionResult> SubmitAssignmentAsync(long userId, MoodleAssignment assignment,
        MoodleAssignmentSubmissionMethod method, string? text, string? fileName, string? mimeType, byte[]? fileBytes, CancellationToken cancellationToken) =>
        Task.FromResult(new MoodleAssignmentSubmissionResult(false, "unavailable"));
    public Task<MoodleQuizResult> GetQuizzesAsync(long userId, int? courseId, CancellationToken cancellationToken) =>
        Task.FromResult(new MoodleQuizResult(MoodleQuizResultKind.Unavailable, [], 0, 0, 0, 0, 0, 0));
}

public sealed class MoodleSessionRepository
{
    public Task DeleteAsync(long userId, CancellationToken cancellationToken) => Task.CompletedTask;
}
