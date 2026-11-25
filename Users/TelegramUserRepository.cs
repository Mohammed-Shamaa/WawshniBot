using Npgsql;
using NpgsqlTypes;
using Wawshni.Bot.Services;

namespace Wawshni.Bot.Users;

public sealed class TelegramUserRepository(NeonDatabaseReadiness database)
{
    private const string UserColumns = "request_id, telegram_user_id, first_name, last_name, username, registered_at, status, status_updated_at, decision_at, decision_by, is_blocked, blocked_at, block_reason, blocked_by, unblocked_at, unblocked_by, last_activity_at";
    private const string EnsureSchemaSql = """
        CREATE TABLE IF NOT EXISTS telegram_users (
            request_id UUID NOT NULL UNIQUE,
            telegram_user_id BIGINT PRIMARY KEY,
            first_name TEXT NULL,
            last_name TEXT NULL,
            username TEXT NULL,
            registered_at TIMESTAMPTZ NOT NULL DEFAULT now(),
            status TEXT NOT NULL CHECK (status IN ('Pending', 'Approved', 'Rejected')),
            status_updated_at TIMESTAMPTZ NOT NULL DEFAULT now(),
            decision_at TIMESTAMPTZ NULL,
            decision_by BIGINT NULL
        );
        CREATE INDEX IF NOT EXISTS ix_telegram_users_status_registered
            ON telegram_users (status, registered_at);
        ALTER TABLE telegram_users ADD COLUMN IF NOT EXISTS is_blocked BOOLEAN NOT NULL DEFAULT FALSE;
        ALTER TABLE telegram_users ADD COLUMN IF NOT EXISTS blocked_at TIMESTAMPTZ NULL;
        ALTER TABLE telegram_users ADD COLUMN IF NOT EXISTS block_reason TEXT NULL;
        ALTER TABLE telegram_users ADD COLUMN IF NOT EXISTS blocked_by TEXT NULL;
        ALTER TABLE telegram_users ADD COLUMN IF NOT EXISTS unblocked_at TIMESTAMPTZ NULL;
        ALTER TABLE telegram_users ADD COLUMN IF NOT EXISTS unblocked_by TEXT NULL;
        ALTER TABLE telegram_users ADD COLUMN IF NOT EXISTS last_activity_at TIMESTAMPTZ NULL;
        CREATE INDEX IF NOT EXISTS ix_telegram_users_blocked_registered ON telegram_users (is_blocked, registered_at DESC);
        CREATE TABLE IF NOT EXISTS telegram_user_audit_log (
            id BIGSERIAL PRIMARY KEY,
            target_telegram_user_id BIGINT NOT NULL,
            action_type TEXT NOT NULL CHECK (action_type IN ('Registered','Approved','Rejected','Blocked','Unblocked','Revoked')),
            administrator TEXT NOT NULL,
            occurred_at TIMESTAMPTZ NOT NULL DEFAULT now(),
            reason TEXT NULL,
            previous_state TEXT NULL,
            new_state TEXT NULL
        );
        CREATE INDEX IF NOT EXISTS ix_telegram_user_audit_occurred ON telegram_user_audit_log (occurred_at DESC);
        CREATE INDEX IF NOT EXISTS ix_telegram_user_audit_target ON telegram_user_audit_log (target_telegram_user_id, occurred_at DESC);
        """;

    public async Task EnsureSchemaAsync(long administratorId, CancellationToken cancellationToken)
    {
        await using var connection = await RequireDataSource().OpenConnectionAsync(cancellationToken);
        await using (var schema = new NpgsqlCommand(EnsureSchemaSql, connection))
            await schema.ExecuteNonQueryAsync(cancellationToken);

        const string seedAdmin = """
            INSERT INTO telegram_users (request_id, telegram_user_id, status, decision_at, decision_by)
            VALUES (@request, @admin, 'Approved', now(), @admin)
            ON CONFLICT (telegram_user_id) DO UPDATE SET
                status = 'Approved', status_updated_at = now(), decision_at = COALESCE(telegram_users.decision_at, now()),
                decision_by = COALESCE(telegram_users.decision_by, EXCLUDED.decision_by);
            """;
        await using var command = new NpgsqlCommand(seedAdmin, connection);
        command.Parameters.AddWithValue("request", Guid.NewGuid());
        command.Parameters.AddWithValue("admin", administratorId);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task<TelegramRegistrationResult> RegisterPendingAsync(long userId, string? firstName, string? lastName, string? username, CancellationToken cancellationToken)
    {
        var sql = $"""
            INSERT INTO telegram_users (request_id, telegram_user_id, first_name, last_name, username, status)
            VALUES (@request, @user, @first, @last, @username, 'Pending')
            ON CONFLICT (telegram_user_id) DO NOTHING
            RETURNING {UserColumns};
            """;
        await using var connection = await RequireDataSource().OpenConnectionAsync(cancellationToken);
        await using var command = new NpgsqlCommand(sql, connection);
        var requestId = Guid.NewGuid();
        command.Parameters.AddWithValue("request", requestId);
        command.Parameters.AddWithValue("user", userId);
        command.Parameters.AddWithValue("first", (object?)firstName ?? DBNull.Value);
        command.Parameters.AddWithValue("last", (object?)lastName ?? DBNull.Value);
        command.Parameters.AddWithValue("username", (object?)username ?? DBNull.Value);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        command.Transaction = transaction;
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (await reader.ReadAsync(cancellationToken))
        {
            var registered = Read(reader);
            await reader.DisposeAsync();
            await AddAuditAsync(connection, transaction, registered.TelegramUserId, "Registered", userId.ToString(), null, null, "Pending", cancellationToken);
            await transaction.CommitAsync(cancellationToken);
            return new(registered, true);
        }
        await reader.DisposeAsync();
        await transaction.RollbackAsync(cancellationToken);
        return new((await GetByTelegramIdAsync(userId, cancellationToken))
            ?? throw new InvalidOperationException("Registration conflict occurred but the existing user could not be loaded."), false);
    }

    public async Task<TelegramUserRegistration?> GetByTelegramIdAsync(long userId, CancellationToken cancellationToken)
    {
        var sql = $"SELECT {UserColumns} FROM telegram_users WHERE telegram_user_id=@user";
        await using var connection = await RequireDataSource().OpenConnectionAsync(cancellationToken);
        await using var command = new NpgsqlCommand(sql, connection);
        command.Parameters.AddWithValue("user", userId);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        return await reader.ReadAsync(cancellationToken) ? Read(reader) : null;
    }

    public async Task<TelegramUserRegistration?> GetByRequestIdAsync(Guid requestId, CancellationToken cancellationToken)
    {
        var sql = $"SELECT {UserColumns} FROM telegram_users WHERE request_id=@request";
        await using var connection = await RequireDataSource().OpenConnectionAsync(cancellationToken);
        await using var command = new NpgsqlCommand(sql, connection);
        command.Parameters.AddWithValue("request", requestId);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        return await reader.ReadAsync(cancellationToken) ? Read(reader) : null;
    }

    public async Task<IReadOnlyList<TelegramUserRegistration>> GetPendingAsync(CancellationToken cancellationToken)
    {
        var sql = $"SELECT {UserColumns} FROM telegram_users WHERE status='Pending' ORDER BY registered_at";
        var users = new List<TelegramUserRegistration>();
        await using var connection = await RequireDataSource().OpenConnectionAsync(cancellationToken);
        await using var command = new NpgsqlCommand(sql, connection);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken)) users.Add(Read(reader));
        return users;
    }

    public async Task<(long Total, long Approved, long Pending, long Rejected)> GetCountsAsync(CancellationToken cancellationToken)
    {
        const string sql = "SELECT count(*), count(*) FILTER (WHERE status='Approved'), count(*) FILTER (WHERE status='Pending'), count(*) FILTER (WHERE status='Rejected') FROM telegram_users";
        await using var connection = await RequireDataSource().OpenConnectionAsync(cancellationToken);
        await using var command = new NpgsqlCommand(sql, connection);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        await reader.ReadAsync(cancellationToken);
        return (reader.GetInt64(0), reader.GetInt64(1), reader.GetInt64(2), reader.GetInt64(3));
    }

    public async Task<TelegramUserRegistration?> SetStatusAsync(long userId, TelegramRegistrationStatus target, long? administratorId, IReadOnlyCollection<TelegramRegistrationStatus> allowedCurrentStates, CancellationToken cancellationToken, string? reason = null, string? administratorName = null)
    {
        if (allowedCurrentStates.Count == 0 || allowedCurrentStates.Any(current => !TelegramRegistrationPolicy.CanTransition(current, target)))
            throw new ArgumentException("The requested registration status transition is not allowed.", nameof(allowedCurrentStates));
        await using var connection = await RequireDataSource().OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        var previous = await GetByTelegramIdForUpdateAsync(connection, transaction, userId, cancellationToken);
        if (previous is null || !allowedCurrentStates.Contains(previous.Status)) return null;
        var action = target == TelegramRegistrationStatus.Approved
            ? "Approved"
            : previous.Status == TelegramRegistrationStatus.Approved ? "Revoked" : "Rejected";
        var sql = $"""
            UPDATE telegram_users SET status=@target, status_updated_at=now(), decision_at=now(), decision_by=@admin
            WHERE telegram_user_id=@user AND status = ANY(@allowed)
            RETURNING {UserColumns};
            """;
        await using var command = new NpgsqlCommand(sql, connection, transaction);
        command.Parameters.AddWithValue("target", target.ToDatabaseValue());
        command.Parameters.AddWithValue("admin", (object?)administratorId ?? DBNull.Value);
        command.Parameters.AddWithValue("user", userId);
        command.Parameters.AddWithValue("allowed", allowedCurrentStates.Select(s => s.ToDatabaseValue()).ToArray());
        TelegramUserRegistration? updated;
        await using (var reader = await command.ExecuteReaderAsync(cancellationToken))
            updated = await reader.ReadAsync(cancellationToken) ? Read(reader) : null;
        if (updated is null) return null;
        await AddAuditAsync(connection, transaction, userId, action, administratorName ?? administratorId?.ToString() ?? "dashboard-admin", reason,
            previous.Status.ToDatabaseValue(), target.ToDatabaseValue(), cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return updated;
    }

    public async Task<TelegramUserRegistration?> TransitionPendingAsync(Guid requestId, TelegramRegistrationStatus target, long administratorId, CancellationToken cancellationToken)
    {
        var existing = await GetByRequestIdAsync(requestId, cancellationToken);
        if (existing is null) return null;
        return await SetStatusAsync(existing.TelegramUserId, target, administratorId,
            [TelegramRegistrationStatus.Pending], cancellationToken);
    }

    public async Task<bool> IsBlockedAsync(long userId, CancellationToken cancellationToken)
    {
        await using var connection = await RequireDataSource().OpenConnectionAsync(cancellationToken);
        await using var command = new NpgsqlCommand("SELECT is_blocked FROM telegram_users WHERE telegram_user_id=@user", connection);
        command.Parameters.AddWithValue("user", userId);
        return await command.ExecuteScalarAsync(cancellationToken) is true;
    }

    public async Task RecordActivityAsync(long userId, CancellationToken cancellationToken)
    {
        await using var connection = await RequireDataSource().OpenConnectionAsync(cancellationToken);
        await using var command = new NpgsqlCommand("UPDATE telegram_users SET last_activity_at=now() WHERE telegram_user_id=@user", connection);
        command.Parameters.AddWithValue("user", userId);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task<TelegramUserRegistration?> SetBlockedAsync(long userId, bool blocked, string administrator, string? reason, CancellationToken cancellationToken)
    {
        await using var connection = await RequireDataSource().OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        var current = await GetByTelegramIdForUpdateAsync(connection, transaction, userId, cancellationToken);
        if (current is null || current.IsBlocked == blocked) return null;
        var sql = blocked
            ? $"UPDATE telegram_users SET is_blocked=TRUE, blocked_at=now(), block_reason=@reason, blocked_by=@admin, unblocked_at=NULL, unblocked_by=NULL WHERE telegram_user_id=@user RETURNING {UserColumns}"
            : $"UPDATE telegram_users SET is_blocked=FALSE, unblocked_at=now(), unblocked_by=@admin WHERE telegram_user_id=@user RETURNING {UserColumns}";
        await using var command = new NpgsqlCommand(sql, connection, transaction);
        command.Parameters.AddWithValue("admin", administrator);
        command.Parameters.AddWithValue("user", userId);
        if (blocked) command.Parameters.AddWithValue("reason", (object?)reason ?? DBNull.Value);
        TelegramUserRegistration? updated;
        await using (var reader = await command.ExecuteReaderAsync(cancellationToken))
            updated = await reader.ReadAsync(cancellationToken) ? Read(reader) : null;
        if (updated is null) return null;
        await AddAuditAsync(connection, transaction, userId, blocked ? "Blocked" : "Unblocked", administrator, reason,
            current.IsBlocked ? "Blocked" : "Active", blocked ? "Blocked" : "Active", cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return updated;
    }

    public async Task<DashboardStatistics> GetDashboardStatisticsAsync(CancellationToken cancellationToken)
    {
        const string sql = """
            SELECT count(*), count(*) FILTER (WHERE status='Approved'), count(*) FILTER (WHERE status='Pending'),
                count(*) FILTER (WHERE status='Rejected'), count(*) FILTER (WHERE is_blocked),
                count(*) FILTER (WHERE NOT is_blocked AND status='Approved'),
                count(*) FILTER (WHERE EXISTS (SELECT 1 FROM moodle_sessions s WHERE s.telegram_user_id=u.telegram_user_id))
            FROM telegram_users u;
            """;
        await using var connection = await RequireDataSource().OpenConnectionAsync(cancellationToken);
        await using var command = new NpgsqlCommand(sql, connection);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        await reader.ReadAsync(cancellationToken);
        return new(reader.GetInt64(0), reader.GetInt64(1), reader.GetInt64(2), reader.GetInt64(3), reader.GetInt64(4), reader.GetInt64(5), reader.GetInt64(6));
    }

    public async Task<DashboardUserPage> GetDashboardUsersAsync(string? search, string? status, bool? blocked, string sort, int page, int pageSize, CancellationToken cancellationToken)
    {
        var normalizedSearch = string.IsNullOrWhiteSpace(search) ? null : search.Trim();
        var normalizedStatus = Enum.TryParse<TelegramRegistrationStatus>(status, true, out var parsedStatus) ? parsedStatus.ToDatabaseValue() : null;
        var orderBy = sort == "oldest" ? "registered_at ASC" : "registered_at DESC";
        const string where = "(@search IS NULL OR COALESCE(first_name,'') || ' ' || COALESCE(last_name,'') ILIKE '%' || @search || '%' OR username ILIKE '%' || @search || '%' OR telegram_user_id::text ILIKE '%' || @search || '%') AND (@status IS NULL OR status=@status) AND (@blocked IS NULL OR is_blocked=@blocked)";
        await using var connection = await RequireDataSource().OpenConnectionAsync(cancellationToken);
        await using var countCommand = new NpgsqlCommand($"SELECT count(*) FROM telegram_users WHERE {where}", connection);
        AddUserFilters(countCommand, normalizedSearch, normalizedStatus, blocked);
        var total = (long)(await countCommand.ExecuteScalarAsync(cancellationToken) ?? 0L);
        var sql = $"""
            SELECT {UserColumns}, EXISTS (SELECT 1 FROM moodle_sessions s WHERE s.telegram_user_id=u.telegram_user_id)
            FROM telegram_users u WHERE {where} ORDER BY {orderBy} LIMIT @limit OFFSET @offset
            """;
        await using var command = new NpgsqlCommand(sql, connection);
        AddUserFilters(command, normalizedSearch, normalizedStatus, blocked);
        command.Parameters.AddWithValue("limit", pageSize);
        command.Parameters.AddWithValue("offset", (page - 1) * pageSize);
        var users = new List<DashboardUser>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken)) users.Add(new(Read(reader), reader.GetBoolean(17)));
        return new(users, total, page, pageSize);
    }

    public async Task<IReadOnlyList<TelegramUserAuditEntry>> GetAuditLogAsync(string? action, long? target, DateTimeOffset? from, DateTimeOffset? to, int page, int pageSize, CancellationToken cancellationToken)
    {
        const string sql = """
            SELECT id, target_telegram_user_id, action_type, administrator, occurred_at, reason, previous_state, new_state
            FROM telegram_user_audit_log
            WHERE (@action IS NULL OR action_type=@action) AND (@target IS NULL OR target_telegram_user_id=@target)
              AND (@from IS NULL OR occurred_at>=@from) AND (@to IS NULL OR occurred_at<@to)
            ORDER BY occurred_at DESC LIMIT @limit OFFSET @offset;
            """;
        await using var connection = await RequireDataSource().OpenConnectionAsync(cancellationToken);
        await using var command = new NpgsqlCommand(sql, connection);
        command.Parameters.Add(new NpgsqlParameter("action", NpgsqlDbType.Text) { Value = (object?)action ?? DBNull.Value });
        command.Parameters.Add(new NpgsqlParameter("target", NpgsqlDbType.Bigint) { Value = (object?)target ?? DBNull.Value });
        command.Parameters.Add(new NpgsqlParameter("from", NpgsqlDbType.TimestampTz) { Value = (object?)from ?? DBNull.Value });
        command.Parameters.Add(new NpgsqlParameter("to", NpgsqlDbType.TimestampTz) { Value = (object?)to ?? DBNull.Value });
        command.Parameters.AddWithValue("limit", pageSize);
        command.Parameters.AddWithValue("offset", (page - 1) * pageSize);
        var entries = new List<TelegramUserAuditEntry>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken)) entries.Add(new(reader.GetInt64(0), reader.GetInt64(1), reader.GetString(2), reader.GetString(3), reader.GetFieldValue<DateTimeOffset>(4), reader.IsDBNull(5) ? null : reader.GetString(5), reader.IsDBNull(6) ? null : reader.GetString(6), reader.IsDBNull(7) ? null : reader.GetString(7)));
        return entries;
    }

    public async Task<IReadOnlyList<RegistrationPoint>> GetRegistrationTrendAsync(CancellationToken cancellationToken)
    {
        const string sql = "SELECT registered_at::date, count(*) FROM telegram_users WHERE registered_at >= now()-interval '29 days' GROUP BY registered_at::date ORDER BY registered_at::date";
        await using var connection = await RequireDataSource().OpenConnectionAsync(cancellationToken);
        await using var command = new NpgsqlCommand(sql, connection);
        var points = new List<RegistrationPoint>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken)) points.Add(new(DateOnly.FromDateTime(reader.GetDateTime(0)), reader.GetInt64(1)));
        return points;
    }

    private static void AddUserFilters(NpgsqlCommand command, string? search, string? status, bool? blocked)
    {
        command.Parameters.Add(new NpgsqlParameter("search", NpgsqlDbType.Text) { Value = (object?)search ?? DBNull.Value });
        command.Parameters.Add(new NpgsqlParameter("status", NpgsqlDbType.Text) { Value = (object?)status ?? DBNull.Value });
        command.Parameters.Add(new NpgsqlParameter("blocked", NpgsqlDbType.Boolean) { Value = (object?)blocked ?? DBNull.Value });
    }

    private async Task<TelegramUserRegistration?> GetByTelegramIdForUpdateAsync(NpgsqlConnection connection, NpgsqlTransaction transaction, long userId, CancellationToken cancellationToken)
    {
        var sql = $"SELECT {UserColumns} FROM telegram_users WHERE telegram_user_id=@user FOR UPDATE";
        await using var command = new NpgsqlCommand(sql, connection, transaction);
        command.Parameters.AddWithValue("user", userId);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        return await reader.ReadAsync(cancellationToken) ? Read(reader) : null;
    }

    private static async Task AddAuditAsync(NpgsqlConnection connection, NpgsqlTransaction? transaction, long userId, string action, string administrator, string? reason, string? previousState, string? newState, CancellationToken cancellationToken)
    {
        const string sql = "INSERT INTO telegram_user_audit_log (target_telegram_user_id, action_type, administrator, reason, previous_state, new_state) VALUES (@user,@action,@admin,@reason,@previous,@next)";
        await using var command = new NpgsqlCommand(sql, connection, transaction);
        command.Parameters.AddWithValue("user", userId);
        command.Parameters.AddWithValue("action", action);
        command.Parameters.AddWithValue("admin", administrator);
        command.Parameters.AddWithValue("reason", (object?)reason ?? DBNull.Value);
        command.Parameters.AddWithValue("previous", (object?)previousState ?? DBNull.Value);
        command.Parameters.AddWithValue("next", (object?)newState ?? DBNull.Value);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private NpgsqlDataSource RequireDataSource() => database.DataSource ?? throw new InvalidOperationException("Neon database is not configured.");

    private static TelegramUserRegistration Read(NpgsqlDataReader reader) => new(
        reader.GetGuid(0), reader.GetInt64(1), reader.IsDBNull(2) ? null : reader.GetString(2),
        reader.IsDBNull(3) ? null : reader.GetString(3), reader.IsDBNull(4) ? null : reader.GetString(4),
        reader.GetFieldValue<DateTimeOffset>(5), TelegramRegistrationStatusExtensions.ParseDatabaseValue(reader.GetString(6)),
        reader.GetFieldValue<DateTimeOffset>(7), reader.IsDBNull(8) ? null : reader.GetFieldValue<DateTimeOffset>(8),
        reader.IsDBNull(9) ? null : reader.GetInt64(9))
    {
        IsBlocked = reader.GetBoolean(10),
        BlockedAt = reader.IsDBNull(11) ? null : reader.GetFieldValue<DateTimeOffset>(11),
        BlockReason = reader.IsDBNull(12) ? null : reader.GetString(12),
        BlockedBy = reader.IsDBNull(13) ? null : reader.GetString(13),
        UnblockedAt = reader.IsDBNull(14) ? null : reader.GetFieldValue<DateTimeOffset>(14),
        UnblockedBy = reader.IsDBNull(15) ? null : reader.GetString(15),
        LastActivityAt = reader.IsDBNull(16) ? null : reader.GetFieldValue<DateTimeOffset>(16)
    };
}

public sealed record DashboardStatistics(long TotalUsers, long ApprovedUsers, long PendingUsers, long RejectedUsers, long BlockedUsers, long ActiveUsers, long MoodleLinkedUsers);
public sealed record DashboardUser(TelegramUserRegistration Registration, bool MoodleLinked);
public sealed record DashboardUserPage(IReadOnlyList<DashboardUser> Users, long Total, int Page, int PageSize);
public sealed record TelegramUserAuditEntry(long Id, long TargetTelegramUserId, string ActionType, string Administrator, DateTimeOffset OccurredAt, string? Reason, string? PreviousState, string? NewState);
public sealed record RegistrationPoint(DateOnly Date, long Count);
