using Npgsql;
using NpgsqlTypes;
using Wawshni.Bot.Services;

namespace Wawshni.Bot.Moodle;

public sealed record StoredMoodleSession(long TelegramUserId, string StudentId, EncryptedMoodleSession Encrypted, DateTimeOffset UpdatedAt);

public sealed class MoodleSessionRepository(NeonDatabaseReadiness database)
{
    private const string EnsureTableSql = """
        CREATE TABLE IF NOT EXISTS moodle_sessions (
            telegram_user_id BIGINT PRIMARY KEY,
            student_id TEXT NOT NULL,
            encrypted_state BYTEA NOT NULL,
            nonce BYTEA NOT NULL CHECK (octet_length(nonce) = 12),
            auth_tag BYTEA NOT NULL CHECK (octet_length(auth_tag) = 16),
            key_version SMALLINT NOT NULL,
            created_at TIMESTAMPTZ NOT NULL DEFAULT now(),
            updated_at TIMESTAMPTZ NOT NULL DEFAULT now()
        );
        """;

    public async Task SaveAsync(long telegramUserId, string studentId, EncryptedMoodleSession session, CancellationToken cancellationToken)
    {
        var source = RequireDataSource();
        await using var connection = await source.OpenConnectionAsync(cancellationToken);
        await using (var schema = new NpgsqlCommand(EnsureTableSql, connection))
            await schema.ExecuteNonQueryAsync(cancellationToken);
        const string sql = """
            INSERT INTO moodle_sessions (telegram_user_id, student_id, encrypted_state, nonce, auth_tag, key_version)
            VALUES (@telegram, @student, @state, @nonce, @tag, @version)
            ON CONFLICT (telegram_user_id) DO UPDATE SET student_id = EXCLUDED.student_id,
                encrypted_state = EXCLUDED.encrypted_state, nonce = EXCLUDED.nonce,
                auth_tag = EXCLUDED.auth_tag, key_version = EXCLUDED.key_version, updated_at = now();
            """;
        await using var command = new NpgsqlCommand(sql, connection);
        command.Parameters.AddWithValue("telegram", telegramUserId);
        command.Parameters.AddWithValue("student", studentId);
        command.Parameters.AddWithValue("state", NpgsqlDbType.Bytea, session.Ciphertext);
        command.Parameters.AddWithValue("nonce", NpgsqlDbType.Bytea, session.Nonce);
        command.Parameters.AddWithValue("tag", NpgsqlDbType.Bytea, session.Tag);
        command.Parameters.AddWithValue("version", session.KeyVersion);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task EnsureSchemaAsync(CancellationToken cancellationToken)
    {
        await using var connection = await RequireDataSource().OpenConnectionAsync(cancellationToken);
        await using var schema = new NpgsqlCommand(EnsureTableSql, connection);
        await schema.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task<StoredMoodleSession?> GetAsync(long telegramUserId, CancellationToken cancellationToken)
    {
        var source = RequireDataSource();
        await using var connection = await source.OpenConnectionAsync(cancellationToken);
        await using (var schema = new NpgsqlCommand(EnsureTableSql, connection))
            await schema.ExecuteNonQueryAsync(cancellationToken);
        const string sql = """
            SELECT student_id, encrypted_state, nonce, auth_tag, key_version, updated_at
            FROM moodle_sessions WHERE telegram_user_id = @telegram;
            """;
        await using var command = new NpgsqlCommand(sql, connection);
        command.Parameters.AddWithValue("telegram", telegramUserId);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken)) return null;
        return new StoredMoodleSession(telegramUserId, reader.GetString(0),
            new EncryptedMoodleSession((byte[])reader[1], (byte[])reader[2], (byte[])reader[3], reader.GetInt16(4)),
            reader.GetFieldValue<DateTimeOffset>(5));
    }

    public async Task DeleteAsync(long telegramUserId, CancellationToken cancellationToken)
    {
        var source = RequireDataSource();
        await using var connection = await source.OpenConnectionAsync(cancellationToken);
        await using (var schema = new NpgsqlCommand(EnsureTableSql, connection))
            await schema.ExecuteNonQueryAsync(cancellationToken);
        await using var command = new NpgsqlCommand("DELETE FROM moodle_sessions WHERE telegram_user_id = @telegram", connection);
        command.Parameters.AddWithValue("telegram", telegramUserId);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private NpgsqlDataSource RequireDataSource() => database.DataSource
        ?? throw new InvalidOperationException("Neon database is not configured.");
}
