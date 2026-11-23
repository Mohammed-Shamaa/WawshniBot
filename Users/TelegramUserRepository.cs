using Npgsql;
using Wawshni.Bot.Services;

namespace Wawshni.Bot.Users;

public sealed class TelegramUserRepository(NeonDatabaseReadiness database)
{
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
        const string sql = """
            INSERT INTO telegram_users (request_id, telegram_user_id, first_name, last_name, username, status)
            VALUES (@request, @user, @first, @last, @username, 'Pending')
            ON CONFLICT (telegram_user_id) DO NOTHING
            RETURNING request_id, telegram_user_id, first_name, last_name, username, registered_at, status, status_updated_at, decision_at, decision_by;
            """;
        await using var connection = await RequireDataSource().OpenConnectionAsync(cancellationToken);
        await using var command = new NpgsqlCommand(sql, connection);
        var requestId = Guid.NewGuid();
        command.Parameters.AddWithValue("request", requestId);
        command.Parameters.AddWithValue("user", userId);
        command.Parameters.AddWithValue("first", (object?)firstName ?? DBNull.Value);
        command.Parameters.AddWithValue("last", (object?)lastName ?? DBNull.Value);
        command.Parameters.AddWithValue("username", (object?)username ?? DBNull.Value);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (await reader.ReadAsync(cancellationToken))
            return new(Read(reader), true);
        await reader.DisposeAsync();
        return new((await GetByTelegramIdAsync(userId, cancellationToken))
            ?? throw new InvalidOperationException("Registration conflict occurred but the existing user could not be loaded."), false);
    }

    public async Task<TelegramUserRegistration?> GetByTelegramIdAsync(long userId, CancellationToken cancellationToken)
    {
        const string sql = "SELECT request_id, telegram_user_id, first_name, last_name, username, registered_at, status, status_updated_at, decision_at, decision_by FROM telegram_users WHERE telegram_user_id=@user";
        await using var connection = await RequireDataSource().OpenConnectionAsync(cancellationToken);
        await using var command = new NpgsqlCommand(sql, connection);
        command.Parameters.AddWithValue("user", userId);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        return await reader.ReadAsync(cancellationToken) ? Read(reader) : null;
    }

    public async Task<TelegramUserRegistration?> GetByRequestIdAsync(Guid requestId, CancellationToken cancellationToken)
    {
        const string sql = "SELECT request_id, telegram_user_id, first_name, last_name, username, registered_at, status, status_updated_at, decision_at, decision_by FROM telegram_users WHERE request_id=@request";
        await using var connection = await RequireDataSource().OpenConnectionAsync(cancellationToken);
        await using var command = new NpgsqlCommand(sql, connection);
        command.Parameters.AddWithValue("request", requestId);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        return await reader.ReadAsync(cancellationToken) ? Read(reader) : null;
    }

    public async Task<IReadOnlyList<TelegramUserRegistration>> GetPendingAsync(CancellationToken cancellationToken)
    {
        const string sql = "SELECT request_id, telegram_user_id, first_name, last_name, username, registered_at, status, status_updated_at, decision_at, decision_by FROM telegram_users WHERE status='Pending' ORDER BY registered_at";
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

    public async Task<TelegramUserRegistration?> SetStatusAsync(long userId, TelegramRegistrationStatus target, long administratorId, IReadOnlyCollection<TelegramRegistrationStatus> allowedCurrentStates, CancellationToken cancellationToken)
    {
        if (allowedCurrentStates.Count == 0 || allowedCurrentStates.Any(current => !TelegramRegistrationPolicy.CanTransition(current, target)))
            throw new ArgumentException("The requested registration status transition is not allowed.", nameof(allowedCurrentStates));
        const string sql = """
            UPDATE telegram_users SET status=@target, status_updated_at=now(), decision_at=now(), decision_by=@admin
            WHERE telegram_user_id=@user AND status = ANY(@allowed)
            RETURNING request_id, telegram_user_id, first_name, last_name, username, registered_at, status, status_updated_at, decision_at, decision_by;
            """;
        await using var connection = await RequireDataSource().OpenConnectionAsync(cancellationToken);
        await using var command = new NpgsqlCommand(sql, connection);
        command.Parameters.AddWithValue("target", target.ToDatabaseValue());
        command.Parameters.AddWithValue("admin", administratorId);
        command.Parameters.AddWithValue("user", userId);
        command.Parameters.AddWithValue("allowed", allowedCurrentStates.Select(s => s.ToDatabaseValue()).ToArray());
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        return await reader.ReadAsync(cancellationToken) ? Read(reader) : null;
    }

    public async Task<TelegramUserRegistration?> TransitionPendingAsync(Guid requestId, TelegramRegistrationStatus target, long administratorId, CancellationToken cancellationToken)
    {
        const string sql = """
            UPDATE telegram_users SET status=@target, status_updated_at=now(), decision_at=now(), decision_by=@admin
            WHERE request_id=@request AND status='Pending'
            RETURNING request_id, telegram_user_id, first_name, last_name, username, registered_at, status, status_updated_at, decision_at, decision_by;
            """;
        await using var connection = await RequireDataSource().OpenConnectionAsync(cancellationToken);
        await using var command = new NpgsqlCommand(sql, connection);
        command.Parameters.AddWithValue("target", target.ToDatabaseValue());
        command.Parameters.AddWithValue("admin", administratorId);
        command.Parameters.AddWithValue("request", requestId);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        return await reader.ReadAsync(cancellationToken) ? Read(reader) : null;
    }

    private NpgsqlDataSource RequireDataSource() => database.DataSource ?? throw new InvalidOperationException("Neon database is not configured.");

    private static TelegramUserRegistration Read(NpgsqlDataReader reader) => new(
        reader.GetGuid(0), reader.GetInt64(1), reader.IsDBNull(2) ? null : reader.GetString(2),
        reader.IsDBNull(3) ? null : reader.GetString(3), reader.IsDBNull(4) ? null : reader.GetString(4),
        reader.GetFieldValue<DateTimeOffset>(5), TelegramRegistrationStatusExtensions.ParseDatabaseValue(reader.GetString(6)),
        reader.GetFieldValue<DateTimeOffset>(7), reader.IsDBNull(8) ? null : reader.GetFieldValue<DateTimeOffset>(8),
        reader.IsDBNull(9) ? null : reader.GetInt64(9));
}
