namespace Wawshni.Bot.Moodle;

public enum MoodleAssignmentSubmissionKind { Ready, Expired, Unavailable, Unsupported }
public enum MoodleAssignmentSubmissionMethod { OnlineText, File }

public sealed record MoodleAssignmentSubmissionInfo(
    MoodleAssignmentSubmissionKind Kind,
    string AssignmentTitle,
    bool AllowsOnlineText,
    bool AllowsFiles,
    IReadOnlyList<string> AcceptedExtensions,
    long? MaxFileBytes,
    int MaxFiles,
    string? Message = null);

public sealed record MoodleAssignmentSubmissionResult(bool Success, string Message);
