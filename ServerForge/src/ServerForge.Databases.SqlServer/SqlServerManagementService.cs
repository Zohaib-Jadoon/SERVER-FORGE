using System.Diagnostics;
using System.Text.RegularExpressions;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Logging;
using Microsoft.Win32;
using ServerForge.Contracts.Dtos;
using ServerForge.Contracts.Enums;
using ServerForge.Core.Entities;
using ServerForge.Core.Interfaces;

namespace ServerForge.Databases.SqlServer;

/// <summary>
/// Manages Microsoft SQL Server instances as standalone database services (never hosted inside IIS).
/// Enforces identifier validation, non-destructive safety checks, and explicit approval for restores.
/// </summary>
public sealed partial class SqlServerManagementService : ISqlServerManager
{
    private readonly ILogger<SqlServerManagementService> _logger;

    public SqlServerManagementService(ILogger<SqlServerManagementService> logger) => _logger = logger;

    [GeneratedRegex(@"^[A-Za-z_][A-Za-z0-9_]{0,127}$", RegexOptions.Compiled)]
    private static partial Regex SafeSqlIdentifierRegex();

    public Task<IReadOnlyList<string>> DiscoverLocalInstancesAsync(CancellationToken ct)
    {
        var instances = new List<string>();
        if (OperatingSystem.IsWindows())
        {
            try
            {
                using var key = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Microsoft\Microsoft SQL Server\Instance Names\SQL");
                if (key is not null)
                {
                    foreach (var name in key.GetValueNames())
                    {
                        instances.Add(name.Equals("MSSQLSERVER", StringComparison.OrdinalIgnoreCase)
                            ? Environment.MachineName
                            : $@"{Environment.MachineName}\{name}");
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Unable to read local SQL Server instance registry key.");
            }
        }
        return Task.FromResult<IReadOnlyList<string>>(instances);
    }

    public async Task<DatabaseHealthDto> TestConnectionAsync(DatabaseServerRecord server, string decryptedConnString, CancellationToken ct)
    {
        var sw = Stopwatch.StartNew();
        try
        {
            await using var conn = new SqlConnection(decryptedConnString);
            await conn.OpenAsync(ct).ConfigureAwait(false);
            await using var cmd = new SqlCommand("SELECT 1", conn);
            await cmd.ExecuteScalarAsync(ct).ConfigureAwait(false);
            sw.Stop();
            return new DatabaseHealthDto(server.Id, server.DisplayName, DatabaseEngineType.SqlServer, true, sw.Elapsed.TotalMilliseconds, null);
        }
        catch (Exception ex)
        {
            sw.Stop();
            return new DatabaseHealthDto(server.Id, server.DisplayName, DatabaseEngineType.SqlServer, false, sw.Elapsed.TotalMilliseconds, ex.Message);
        }
    }

    public async Task<IReadOnlyList<string>> ListDatabasesAsync(string decryptedConnString, CancellationToken ct)
    {
        var dbs = new List<string>();
        await using var conn = new SqlConnection(decryptedConnString);
        await conn.OpenAsync(ct).ConfigureAwait(false);
        await using var cmd = new SqlCommand(
            "SELECT name FROM sys.databases WHERE database_id > 4 ORDER BY name;", conn);
        await using var reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
        while (await reader.ReadAsync(ct).ConfigureAwait(false))
            dbs.Add(reader.GetString(0));
        return dbs;
    }

    public async Task CreateDatabaseAsync(
        string decryptedConnString,
        string databaseName,
        string? scopedLoginName,
        string? scopedPassword,
        CancellationToken ct)
    {
        ValidateIdentifier(databaseName, nameof(databaseName));
        if (!string.IsNullOrWhiteSpace(scopedLoginName))
            ValidateIdentifier(scopedLoginName, nameof(scopedLoginName));

        await using var conn = new SqlConnection(decryptedConnString);
        await conn.OpenAsync(ct).ConfigureAwait(false);

        await using (var createDb = new SqlCommand($"CREATE DATABASE [{databaseName}];", conn))
            await createDb.ExecuteNonQueryAsync(ct).ConfigureAwait(false);

        if (!string.IsNullOrWhiteSpace(scopedLoginName) && !string.IsNullOrWhiteSpace(scopedPassword))
        {
            var escapedPwd = scopedPassword.Replace("'", "''", StringComparison.Ordinal);
            var sql = $"""
                IF NOT EXISTS (SELECT 1 FROM sys.server_principals WHERE name = N'{scopedLoginName}')
                    CREATE LOGIN [{scopedLoginName}] WITH PASSWORD = N'{escapedPwd}', CHECK_POLICY = ON;
                USE [{databaseName}];
                CREATE USER [{scopedLoginName}] FOR LOGIN [{scopedLoginName}];
                ALTER ROLE [db_datareader] ADD MEMBER [{scopedLoginName}];
                ALTER ROLE [db_datawriter] ADD MEMBER [{scopedLoginName}];
                ALTER ROLE [db_ddladmin] ADD MEMBER [{scopedLoginName}];
                """;
            await using var userCmd = new SqlCommand(sql, conn);
            await userCmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
        }

        _logger.LogInformation("Created SQL Server database {DatabaseName}", databaseName);
    }

    public async Task<string> BackupDatabaseAsync(
        string decryptedConnString,
        string databaseName,
        string backupDirectory,
        CancellationToken ct)
    {
        ValidateIdentifier(databaseName, nameof(databaseName));
        Directory.CreateDirectory(backupDirectory);
        var filePath = Path.Combine(backupDirectory, $"{databaseName}_{DateTimeOffset.UtcNow:yyyyMMdd_HHmmss}.bak");

        await using var conn = new SqlConnection(decryptedConnString);
        await conn.OpenAsync(ct).ConfigureAwait(false);
        await using var cmd = new SqlCommand(
            $"BACKUP DATABASE [{databaseName}] TO DISK = @path WITH COPY_ONLY, CHECKSUM, INIT;", conn);
        cmd.Parameters.AddWithValue("@path", filePath);
        cmd.CommandTimeout = 600;
        await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);

        _logger.LogInformation("Completed COPY_ONLY checksum backup for SQL database {Database} -> {File}", databaseName, filePath);
        return filePath;
    }

    public async Task RestoreDatabaseAsync(
        string decryptedConnString,
        string databaseName,
        string backupFilePath,
        bool explicitOperatorApproval,
        CancellationToken ct)
    {
        if (!explicitOperatorApproval)
            throw new UnauthorizedAccessException("SQL Server database restore requires explicit operator approval.");

        ValidateIdentifier(databaseName, nameof(databaseName));
        if (!File.Exists(backupFilePath))
            throw new FileNotFoundException("Backup file not found.", backupFilePath);

        await using var conn = new SqlConnection(decryptedConnString);
        await conn.OpenAsync(ct).ConfigureAwait(false);
        await using var cmd = new SqlCommand(
            $"USE master; ALTER DATABASE [{databaseName}] SET SINGLE_USER WITH ROLLBACK IMMEDIATE; " +
            $"RESTORE DATABASE [{databaseName}] FROM DISK = @path WITH REPLACE, RECOVERY; " +
            $"ALTER DATABASE [{databaseName}] SET MULTI_USER;", conn);
        cmd.Parameters.AddWithValue("@path", backupFilePath);
        cmd.CommandTimeout = 900;
        await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
    }

    public static void ValidateIdentifier(string identifier, string paramName)
    {
        if (string.IsNullOrWhiteSpace(identifier) || !SafeSqlIdentifierRegex().IsMatch(identifier))
            throw new ArgumentException($"Invalid SQL identifier '{identifier}'. Use letters, digits, and underscores only.", paramName);
    }
}
