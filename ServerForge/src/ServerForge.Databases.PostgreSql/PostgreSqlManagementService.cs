using System.Diagnostics;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;
using Npgsql;
using ServerForge.Contracts.Dtos;
using ServerForge.Core.Entities;
using ServerForge.Core.Interfaces;
using ServerForge.Security.Execution;

namespace ServerForge.Databases.PostgreSql;

/// <summary>
/// Manages local, remote, and external managed PostgreSQL instances (e.g., Neon).
/// Clearly distinguishes external managed infrastructure that ServerForge connects to vs owns.
/// </summary>
public sealed partial class PostgreSqlManagementService : IPostgreSqlManager
{
    private readonly ILogger<PostgreSqlManagementService> _logger;

    public PostgreSqlManagementService(ILogger<PostgreSqlManagementService> logger) => _logger = logger;

    [GeneratedRegex(@"^[a-z_][a-z0-9_]{0,62}$", RegexOptions.Compiled)]
    private static partial Regex SafePgIdentifierRegex();

    public async Task<DatabaseHealthDto> TestConnectionAsync(DatabaseServerRecord server, string decryptedConnString, CancellationToken ct)
    {
        var sw = Stopwatch.StartNew();
        try
        {
            await using var conn = new NpgsqlConnection(decryptedConnString);
            await conn.OpenAsync(ct).ConfigureAwait(false);
            await using var cmd = new NpgsqlCommand("SELECT 1;", conn);
            await cmd.ExecuteScalarAsync(ct).ConfigureAwait(false);
            sw.Stop();
            return new DatabaseHealthDto(server.Id, server.DisplayName, server.Engine, true, sw.Elapsed.TotalMilliseconds, null);
        }
        catch (Exception ex)
        {
            sw.Stop();
            return new DatabaseHealthDto(server.Id, server.DisplayName, server.Engine, false, sw.Elapsed.TotalMilliseconds, ex.Message);
        }
    }

    public async Task<IReadOnlyList<string>> ListDatabasesAsync(string decryptedConnString, CancellationToken ct)
    {
        var list = new List<string>();
        await using var conn = new NpgsqlConnection(decryptedConnString);
        await conn.OpenAsync(ct).ConfigureAwait(false);
        await using var cmd = new NpgsqlCommand(
            "SELECT datname FROM pg_database WHERE datistemplate = false ORDER BY datname;", conn);
        await using var reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
        while (await reader.ReadAsync(ct).ConfigureAwait(false))
            list.Add(reader.GetString(0));
        return list;
    }

    public async Task CreateDatabaseAsync(
        DatabaseServerRecord server,
        string decryptedConnString,
        string databaseName,
        string? ownerRole,
        CancellationToken ct)
    {
        ValidateIdentifier(databaseName, nameof(databaseName));
        if (!string.IsNullOrWhiteSpace(ownerRole))
            ValidateIdentifier(ownerRole, nameof(ownerRole));

        await using var conn = new NpgsqlConnection(decryptedConnString);
        await conn.OpenAsync(ct).ConfigureAwait(false);
        var ownerClause = string.IsNullOrWhiteSpace(ownerRole) ? "" : $" OWNER \"{ownerRole}\"";
        await using var cmd = new NpgsqlCommand($"CREATE DATABASE \"{databaseName}\"{ownerClause};", conn);
        await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);

        _logger.LogInformation("Created PostgreSQL database {Database} (ExternalManaged={IsExternal})",
            databaseName, server.IsExternalManagedInfrastructure);
    }

    public async Task<string> BackupDatabaseAsync(
        string decryptedConnString,
        string databaseName,
        string backupDirectory,
        CancellationToken ct)
    {
        ValidateIdentifier(databaseName, nameof(databaseName));
        Directory.CreateDirectory(backupDirectory);

        var csb = new NpgsqlConnectionStringBuilder(decryptedConnString) { Database = databaseName };
        var dumpFile = Path.Combine(backupDirectory, $"{databaseName}_{DateTimeOffset.UtcNow:yyyyMMdd_HHmmss}.dump");

        var env = new Dictionary<string, string>();
        if (!string.IsNullOrEmpty(csb.Password))
            env["PGPASSWORD"] = csb.Password;

        var args = new[]
        {
            "-h", csb.Host ?? "localhost",
            "-p", csb.Port.ToString(),
            "-U", csb.Username ?? "postgres",
            "-F", "c",
            "-f", dumpFile,
            databaseName
        };

        var res = await SafeProcessRunner.RunAsync("pg_dump", args, environmentVariables: env, cancellationToken: ct);
        if (res.ExitCode != 0)
            throw new InvalidOperationException($"pg_dump failed for '{databaseName}': {res.StandardError}");

        return dumpFile;
    }

    public static void ValidateIdentifier(string identifier, string paramName)
    {
        if (string.IsNullOrWhiteSpace(identifier) || !SafePgIdentifierRegex().IsMatch(identifier))
            throw new ArgumentException($"Invalid PostgreSQL identifier '{identifier}'. Use lowercase letters, digits, and underscores.", paramName);
    }
}
