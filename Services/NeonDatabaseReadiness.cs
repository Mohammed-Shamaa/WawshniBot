using System.Web;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Npgsql;

namespace Wawshni.Bot.Services;

public sealed class NeonDatabaseReadiness : IAsyncDisposable
{
    private readonly NpgsqlDataSource? _dataSource;
    private readonly ILogger<NeonDatabaseReadiness> _logger;

    public NeonDatabaseReadiness(IConfiguration configuration, ILogger<NeonDatabaseReadiness> logger)
    {
        _logger = logger;
        var connectionString = configuration.GetConnectionString("DefaultConnection")
            ?? configuration["DATABASE_URL"];

        if (string.IsNullOrWhiteSpace(connectionString))
        {
            _logger.LogWarning("Neon connection is not configured; database readiness is unavailable.");
            return;
        }

        try
        {
            _dataSource = NpgsqlDataSource.Create(ToNpgsqlConnectionString(connectionString));
        }
        catch (Exception exception) when (exception is ArgumentException or UriFormatException)
        {
            _logger.LogError("Neon connection configuration is invalid ({ExceptionType}); check the connection variable.", exception.GetType().Name);
        }
    }

    public async Task<bool> CanConnectAsync(CancellationToken cancellationToken)
    {
        if (_dataSource is null) return false;

        try
        {
            await using var command = _dataSource.CreateCommand("SELECT 1");
            var result = await command.ExecuteScalarAsync(cancellationToken);
            return result is int value && value == 1;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            _logger.LogWarning("Neon readiness query failed ({ExceptionType}).", exception.GetType().Name);
            return false;
        }
    }

    public ValueTask DisposeAsync() => _dataSource?.DisposeAsync() ?? ValueTask.CompletedTask;

    private static string ToNpgsqlConnectionString(string value)
    {
        if (Uri.TryCreate(value, UriKind.Absolute, out var uri) &&
            (uri.Scheme.Equals("postgresql", StringComparison.OrdinalIgnoreCase) ||
             uri.Scheme.Equals("postgres", StringComparison.OrdinalIgnoreCase)))
        {
            var credentials = uri.UserInfo.Split(':', 2);
            if (credentials.Length != 2 || string.IsNullOrWhiteSpace(uri.Host))
                throw new ArgumentException("Invalid PostgreSQL URI.");

            var query = HttpUtility.ParseQueryString(uri.Query);
            var sslMode = query["sslmode"]?.ToLowerInvariant() switch
            {
                "verify-full" => SslMode.VerifyFull,
                "verify-ca" => SslMode.VerifyCA,
                _ => SslMode.Require
            };

            return new NpgsqlConnectionStringBuilder
            {
                Host = uri.Host,
                Port = uri.IsDefaultPort ? 5432 : uri.Port,
                Database = Uri.UnescapeDataString(uri.AbsolutePath.Trim('/')),
                Username = Uri.UnescapeDataString(credentials[0]),
                Password = Uri.UnescapeDataString(credentials[1]),
                SslMode = sslMode,
                Pooling = true,
                Timeout = 15,
                CommandTimeout = 10,
                MaxPoolSize = 10
            }.ConnectionString;
        }

        var builder = new NpgsqlConnectionStringBuilder(value)
        {
            Pooling = true,
            Timeout = 15,
            CommandTimeout = 10,
            MaxPoolSize = 10
        };
        if (builder.SslMode is SslMode.Disable or SslMode.Prefer)
            builder.SslMode = SslMode.Require;
        return builder.ConnectionString;
    }
}
