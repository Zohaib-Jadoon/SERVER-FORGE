using System.Data;
using System.Globalization;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace ServerForge.Infrastructure.Persistence;

public sealed record SqliteMigrationResult(
    int PreviousVersion,
    int CurrentVersion,
    IReadOnlyList<int> AppliedVersions,
    string? BackupFilePath,
    int NormalizedTimestampCount,
    bool WasFreshDatabase);

public sealed class SqliteMigrationException : Exception
{
    public SqliteMigrationException(string message, int targetVersion, string? backupFilePath, Exception innerException)
        : base(message, innerException)
    {
        TargetVersion = targetVersion;
        BackupFilePath = backupFilePath;
    }

    public int TargetVersion { get; }
    public string? BackupFilePath { get; }
}

/// <summary>
/// Safe, versioned SQLite metadata schema initializer and migrator.
/// <para>
/// Safety guarantees:
/// <list type="bullet">
/// <item>Never deletes or recreates an existing ServerForge metadata database.</item>
/// <item>Creates a consistent SQLite backup file via <see cref="SqliteConnection.BackupDatabase(SqliteConnection)"/> before applying any pending upgrade to an existing file-backed database.</item>
/// <item>Executes pending upgrades atomically inside a SQLite transaction and rolls back completely if any step fails.</item>
/// <item>Preserves paired servers, certificate thumbprints, encrypted DPAPI credentials, applications, deployments, environment variables, and audit logs.</item>
/// </list>
/// </para>
/// </summary>
public static class SqliteSchemaMigrator
{
    public const int LatestSchemaVersion = 2;

    private const string VersionTableSql = """
        CREATE TABLE IF NOT EXISTS "__ServerForgeSchemaVersions" (
            "Version" INTEGER NOT NULL CONSTRAINT "PK___ServerForgeSchemaVersions" PRIMARY KEY,
            "Name" TEXT NOT NULL,
            "AppliedAtUtc" TEXT NOT NULL,
            "BackupPath" TEXT NULL
        );
        """;

    private static readonly (string Table, string PrimaryKeyColumn, string[] TimestampColumns)[] TimestampAuditTargets =
    [
        ("AdminProfiles", "Id", ["CreatedAtUtc"]),
        ("VaultSecrets", "Key", ["UpdatedAtUtc"]),
        ("Projects", "Id", ["CreatedAtUtc"]),
        ("Servers", "Id", ["LastSeenUtc", "CreatedAtUtc"]),
        ("Applications", "Id", ["CreatedAtUtc", "UpdatedAtUtc"]),
        ("Deployments", "Id", ["QueuedAtUtc", "StartedAtUtc", "CompletedAtUtc"]),
        ("EnvironmentVariables", "Id", ["UpdatedAtUtc"]),
        ("DomainBindings", "Id", ["CertificateExpiresUtc", "LastDnsCheckUtc"]),
        ("DatabaseServers", "Id", ["CreatedAtUtc"]),
        ("BackupJobs", "Id", ["LastRunUtc"]),
        ("AuditLogs", "Id", ["TimestampUtc"])
    ];

    public static async Task<int> GetSchemaVersionAsync(ServerForgeDbContext dbContext, CancellationToken ct = default)
    {
        if (dbContext.Database.GetDbConnection() is not SqliteConnection sqliteConn)
            throw new InvalidOperationException("SqliteSchemaMigrator requires an EF Core SQLite connection.");

        var openedHere = sqliteConn.State != ConnectionState.Open;
        if (openedHere)
            await dbContext.Database.OpenConnectionAsync(ct).ConfigureAwait(false);

        try
        {
            if (!await TableExistsAsync(sqliteConn, null, "__ServerForgeSchemaVersions", ct).ConfigureAwait(false))
                return 0;

            return await ReadCurrentVersionAsync(sqliteConn, null, ct).ConfigureAwait(false);
        }
        finally
        {
            if (openedHere && !IsInMemoryConnection(sqliteConn))
                await dbContext.Database.CloseConnectionAsync().ConfigureAwait(false);
        }
    }

    public static async Task<SqliteMigrationResult> MigrateAsync(ServerForgeDbContext dbContext, CancellationToken ct = default)
    {
        if (dbContext.Database.GetDbConnection() is not SqliteConnection sqliteConn)
            throw new InvalidOperationException("SqliteSchemaMigrator requires an EF Core SQLite connection.");

        var isInMemory = IsInMemoryConnection(sqliteConn);

        await dbContext.Database.OpenConnectionAsync(ct).ConfigureAwait(false);
        var userTableCount = await CountUserTablesAsync(sqliteConn, ct).ConfigureAwait(false);

        if (userTableCount == 0)
        {
            // Fresh installation: let EF Core create the authoritative schema, then stamp schema versions.
            if (!isInMemory)
                await dbContext.Database.CloseConnectionAsync().ConfigureAwait(false);

            await dbContext.Database.EnsureCreatedAsync(ct).ConfigureAwait(false);
            await dbContext.Database.OpenConnectionAsync(ct).ConfigureAwait(false);

            await ExecuteNonQueryAsync(sqliteConn, null, VersionTableSql, ct).ConfigureAwait(false);
            await using (var tx = (SqliteTransaction)await sqliteConn.BeginTransactionAsync(ct).ConfigureAwait(false))
            {
                await RecordVersionAsync(sqliteConn, tx, 1, "BaselineSchema", null, ct).ConfigureAwait(false);
                await RecordVersionAsync(sqliteConn, tx, 2, "NormalizeUtcIso8601Timestamps", null, ct).ConfigureAwait(false);
                await tx.CommitAsync(ct).ConfigureAwait(false);
            }

            await ApplyConnectionPragmasAsync(sqliteConn, ct).ConfigureAwait(false);
            return new SqliteMigrationResult(
                PreviousVersion: 0,
                CurrentVersion: LatestSchemaVersion,
                AppliedVersions: [1, 2],
                BackupFilePath: null,
                NormalizedTimestampCount: 0,
                WasFreshDatabase: true);
        }

        // Existing database: inspect schema version read-only BEFORE taking backup or modifying anything.
        var hasVersionTable = await TableExistsAsync(sqliteConn, null, "__ServerForgeSchemaVersions", ct).ConfigureAwait(false);
        var currentVersion = hasVersionTable
            ? await ReadCurrentVersionAsync(sqliteConn, null, ct).ConfigureAwait(false)
            : 0;

        if (currentVersion >= LatestSchemaVersion)
        {
            await ApplyConnectionPragmasAsync(sqliteConn, ct).ConfigureAwait(false);
            return new SqliteMigrationResult(
                PreviousVersion: currentVersion,
                CurrentVersion: currentVersion,
                AppliedVersions: [],
                BackupFilePath: null,
                NormalizedTimestampCount: 0,
                WasFreshDatabase: false);
        }

        // Existing database needs upgrade: create a consistent backup BEFORE modifying anything.
        string? backupPath = null;
        if (!isInMemory && !string.IsNullOrWhiteSpace(sqliteConn.DataSource) && File.Exists(sqliteConn.DataSource))
        {
            backupPath = CreatePreUpgradeBackup(sqliteConn, LatestSchemaVersion);
        }

        var appliedVersions = new List<int>();
        var normalizedCount = 0;

        await using var migrationTx = (SqliteTransaction)await sqliteConn.BeginTransactionAsync(ct).ConfigureAwait(false);
        try
        {
            await ExecuteNonQueryAsync(sqliteConn, migrationTx, VersionTableSql, ct).ConfigureAwait(false);

            if (currentVersion < 1)
            {
                await EnsureBaselineTablesAndIndexesAsync(sqliteConn, migrationTx, ct).ConfigureAwait(false);
                await RecordVersionAsync(sqliteConn, migrationTx, 1, "BaselineSchema", backupPath, ct).ConfigureAwait(false);
                appliedVersions.Add(1);
            }

            if (currentVersion < 2)
            {
                normalizedCount = await NormalizeAllTimestampsAsync(sqliteConn, migrationTx, ct).ConfigureAwait(false);
                await EnsureTimestampIndexesAsync(sqliteConn, migrationTx, ct).ConfigureAwait(false);
                await RecordVersionAsync(sqliteConn, migrationTx, 2, "NormalizeUtcIso8601Timestamps", backupPath, ct).ConfigureAwait(false);
                appliedVersions.Add(2);
            }

            await migrationTx.CommitAsync(ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            await SafeRollbackAsync(migrationTx).ConfigureAwait(false);
            throw;
        }
        catch (Exception ex)
        {
            await SafeRollbackAsync(migrationTx).ConfigureAwait(false);
            var backupNote = backupPath is not null
                ? $"Pre-upgrade backup preserved at '{backupPath}'."
                : "No existing database files were deleted.";
            throw new SqliteMigrationException(
                $"SQLite metadata migration from v{currentVersion} to v{LatestSchemaVersion} failed and was rolled back safely. {backupNote} Cause: {ex.Message}",
                LatestSchemaVersion,
                backupPath,
                ex);
        }

        // PRAGMA journal_mode and foreign_keys must run outside transactions in SQLite.
        await ApplyConnectionPragmasAsync(sqliteConn, ct).ConfigureAwait(false);

        return new SqliteMigrationResult(
            PreviousVersion: currentVersion,
            CurrentVersion: LatestSchemaVersion,
            AppliedVersions: appliedVersions,
            BackupFilePath: backupPath,
            NormalizedTimestampCount: normalizedCount,
            WasFreshDatabase: false);
    }

    private static string CreatePreUpgradeBackup(SqliteConnection sourceConnection, int targetVersion)
    {
        var fullDbPath = Path.GetFullPath(sourceConnection.DataSource);
        var dbDir = Path.GetDirectoryName(fullDbPath) ?? ".";
        var backupDir = Path.Combine(dbDir, "backups");
        Directory.CreateDirectory(backupDir);

        var fileName = Path.GetFileName(fullDbPath);
        var stamp = DateTimeOffset.UtcNow.ToString("yyyyMMddHHmmssfff", CultureInfo.InvariantCulture);
        var backupPath = Path.Combine(backupDir, $"{fileName}.pre-v{targetVersion}.{stamp}.bak");

        // Pooling=False ensures Windows releases the backup file handle immediately upon disposal.
        using (var backupConn = new SqliteConnection($"Data Source={backupPath};Pooling=False"))
        {
            backupConn.Open();
            sourceConnection.BackupDatabase(backupConn);
        }

        var info = new FileInfo(backupPath);
        if (!info.Exists || info.Length == 0)
            throw new IOException($"Pre-upgrade SQLite backup did not produce a valid file at '{backupPath}'.");

        return backupPath;
    }

    private static async Task EnsureBaselineTablesAndIndexesAsync(
        SqliteConnection conn,
        SqliteTransaction tx,
        CancellationToken ct)
    {
        // Idempotent additive DDL only: creates missing baseline tables/indexes without touching existing user data.
        const string baselineDdl = """
            CREATE TABLE IF NOT EXISTS "AdminProfiles" (
                "Id" TEXT NOT NULL CONSTRAINT "PK_AdminProfiles" PRIMARY KEY,
                "DisplayName" TEXT NOT NULL,
                "Username" TEXT NOT NULL,
                "PasswordHash" TEXT NOT NULL,
                "PasswordSalt" TEXT NOT NULL,
                "DarkThemeEnabled" INTEGER NOT NULL,
                "DemoModeEnabled" INTEGER NOT NULL,
                "CreatedAtUtc" TEXT NOT NULL
            );
            CREATE UNIQUE INDEX IF NOT EXISTS "IX_AdminProfiles_Username" ON "AdminProfiles" ("Username");

            CREATE TABLE IF NOT EXISTS "VaultSecrets" (
                "Key" TEXT NOT NULL CONSTRAINT "PK_VaultSecrets" PRIMARY KEY,
                "ProtectedValue" TEXT NOT NULL,
                "UpdatedAtUtc" TEXT NOT NULL
            );

            CREATE TABLE IF NOT EXISTS "Projects" (
                "Id" TEXT NOT NULL CONSTRAINT "PK_Projects" PRIMARY KEY,
                "Name" TEXT NOT NULL,
                "Slug" TEXT NOT NULL,
                "Description" TEXT NOT NULL,
                "CreatedAtUtc" TEXT NOT NULL
            );
            CREATE UNIQUE INDEX IF NOT EXISTS "IX_Projects_Slug" ON "Projects" ("Slug");

            CREATE TABLE IF NOT EXISTS "Servers" (
                "Id" TEXT NOT NULL CONSTRAINT "PK_Servers" PRIMARY KEY,
                "AgentId" TEXT NOT NULL,
                "DisplayName" TEXT NOT NULL,
                "HostAddress" TEXT NOT NULL,
                "AgentPort" INTEGER NOT NULL,
                "RequirePrivateNetworkOrVpn" INTEGER NOT NULL,
                "PinnedServerCertThumbprint" TEXT NOT NULL,
                "ClientCertThumbprint" TEXT NOT NULL,
                "Status" INTEGER NOT NULL,
                "OsVersion" TEXT NULL,
                "IisVersion" TEXT NULL,
                "LastSeenUtc" TEXT NULL,
                "CreatedAtUtc" TEXT NOT NULL
            );
            CREATE UNIQUE INDEX IF NOT EXISTS "IX_Servers_AgentId" ON "Servers" ("AgentId");

            CREATE TABLE IF NOT EXISTS "Applications" (
                "Id" TEXT NOT NULL CONSTRAINT "PK_Applications" PRIMARY KEY,
                "ProjectGroupId" TEXT NOT NULL,
                "ServerId" TEXT NOT NULL,
                "Name" TEXT NOT NULL,
                "Slug" TEXT NOT NULL,
                "RuntimeType" INTEGER NOT NULL,
                "ManagementState" INTEGER NOT NULL,
                "RepositoryFullName" TEXT NOT NULL,
                "Branch" TEXT NOT NULL,
                "WorkingDirectory" TEXT NOT NULL,
                "AutoDeployEnabled" INTEGER NOT NULL,
                "RepositoryExplicitlyAuthorized" INTEGER NOT NULL,
                "SourceMode" INTEGER NOT NULL,
                "GitHubWorkflowFileName" TEXT NULL,
                "BuildCommand" TEXT NOT NULL,
                "StartCommand" TEXT NOT NULL,
                "RuntimeVersion" TEXT NOT NULL,
                "InternalPort" INTEGER NOT NULL,
                "ServiceAccountName" TEXT NOT NULL,
                "IisSiteName" TEXT NOT NULL,
                "IisAppPoolName" TEXT NOT NULL,
                "WindowsServiceName" TEXT NOT NULL,
                "PrimaryDomain" TEXT NOT NULL,
                "HealthCheckPath" TEXT NOT NULL,
                "HealthCheckTimeoutSeconds" INTEGER NOT NULL,
                "RetainHealthyReleasesCount" INTEGER NOT NULL,
                "AutoRollbackOnFailure" INTEGER NOT NULL,
                "ReleasesRootPath" TEXT NOT NULL,
                "PersistentDataPath" TEXT NOT NULL,
                "ActiveReleasePath" TEXT NULL,
                "LastKnownGoodReleasePath" TEXT NULL,
                "LastDeployedCommitSha" TEXT NULL,
                "CreatedAtUtc" TEXT NOT NULL,
                "UpdatedAtUtc" TEXT NOT NULL,
                CONSTRAINT "FK_Applications_Projects_ProjectGroupId" FOREIGN KEY ("ProjectGroupId") REFERENCES "Projects" ("Id") ON DELETE RESTRICT
            );
            CREATE INDEX IF NOT EXISTS "IX_Applications_ProjectGroupId" ON "Applications" ("ProjectGroupId");
            CREATE UNIQUE INDEX IF NOT EXISTS "IX_Applications_ServerId_Slug" ON "Applications" ("ServerId", "Slug");

            CREATE TABLE IF NOT EXISTS "Deployments" (
                "Id" TEXT NOT NULL CONSTRAINT "PK_Deployments" PRIMARY KEY,
                "ApplicationId" TEXT NOT NULL,
                "ServerId" TEXT NOT NULL,
                "State" INTEGER NOT NULL,
                "CommitSha" TEXT NOT NULL,
                "Branch" TEXT NOT NULL,
                "CommitAuthor" TEXT NOT NULL,
                "CommitMessage" TEXT NOT NULL,
                "ArtifactSha256" TEXT NULL,
                "ReleaseDirectory" TEXT NULL,
                "PreviousReleaseDirectory" TEXT NULL,
                "TriggeredAutomatically" INTEGER NOT NULL,
                "ZeroDowntimeAchieved" INTEGER NOT NULL,
                "ZeroDowntimeNote" TEXT NULL,
                "ErrorSummary" TEXT NULL,
                "TriggeredBy" TEXT NOT NULL,
                "QueuedAtUtc" TEXT NOT NULL,
                "StartedAtUtc" TEXT NULL,
                "CompletedAtUtc" TEXT NULL
            );
            CREATE INDEX IF NOT EXISTS "IX_Deployments_ApplicationId_QueuedAtUtc" ON "Deployments" ("ApplicationId", "QueuedAtUtc");
            CREATE INDEX IF NOT EXISTS "IX_Deployments_ApplicationId_CommitSha" ON "Deployments" ("ApplicationId", "CommitSha");

            CREATE TABLE IF NOT EXISTS "EnvironmentVariables" (
                "Id" TEXT NOT NULL CONSTRAINT "PK_EnvironmentVariables" PRIMARY KEY,
                "ApplicationId" TEXT NOT NULL,
                "Tier" INTEGER NOT NULL,
                "Key" TEXT NOT NULL,
                "IsSecret" INTEGER NOT NULL,
                "IsRequired" INTEGER NOT NULL,
                "ProtectedValue" TEXT NOT NULL,
                "UpdatedAtUtc" TEXT NOT NULL
            );
            CREATE UNIQUE INDEX IF NOT EXISTS "IX_EnvironmentVariables_ApplicationId_Tier_Key" ON "EnvironmentVariables" ("ApplicationId", "Tier", "Key");

            CREATE TABLE IF NOT EXISTS "DomainBindings" (
                "Id" TEXT NOT NULL CONSTRAINT "PK_DomainBindings" PRIMARY KEY,
                "ApplicationId" TEXT NOT NULL,
                "ServerId" TEXT NOT NULL,
                "Hostname" TEXT NOT NULL,
                "HttpPort" INTEGER NOT NULL,
                "HttpsPort" INTEGER NOT NULL,
                "HttpsEnabled" INTEGER NOT NULL,
                "UseWinAcmeLetsEncrypt" INTEGER NOT NULL,
                "CertificateThumbprint" TEXT NULL,
                "CertificateExpiresUtc" TEXT NULL,
                "DnsVerified" INTEGER NOT NULL,
                "ResolvedIpAddresses" TEXT NULL,
                "LastDnsCheckUtc" TEXT NULL
            );
            CREATE INDEX IF NOT EXISTS "IX_DomainBindings_ServerId_Hostname_HttpPort" ON "DomainBindings" ("ServerId", "Hostname", "HttpPort");

            CREATE TABLE IF NOT EXISTS "DatabaseServers" (
                "Id" TEXT NOT NULL CONSTRAINT "PK_DatabaseServers" PRIMARY KEY,
                "ServerId" TEXT NULL,
                "DisplayName" TEXT NOT NULL,
                "Engine" INTEGER NOT NULL,
                "HostOrInstance" TEXT NOT NULL,
                "Port" INTEGER NOT NULL,
                "IsExternalManagedInfrastructure" INTEGER NOT NULL,
                "ExposePublicly" INTEGER NOT NULL,
                "ProtectedConnectionString" TEXT NOT NULL,
                "ManagementState" INTEGER NOT NULL,
                "CreatedAtUtc" TEXT NOT NULL
            );

            CREATE TABLE IF NOT EXISTS "BackupJobs" (
                "Id" TEXT NOT NULL CONSTRAINT "PK_BackupJobs" PRIMARY KEY,
                "DatabaseServerId" TEXT NULL,
                "ApplicationId" TEXT NULL,
                "TargetName" TEXT NOT NULL,
                "BackupType" TEXT NOT NULL,
                "DestinationPath" TEXT NOT NULL,
                "ScheduleCron" TEXT NOT NULL,
                "RetentionDays" INTEGER NOT NULL,
                "LastRunUtc" TEXT NULL,
                "LastStatus" TEXT NULL,
                "LastSizeBytes" INTEGER NULL
            );

            CREATE TABLE IF NOT EXISTS "AuditLogs" (
                "Id" INTEGER NOT NULL CONSTRAINT "PK_AuditLogs" PRIMARY KEY AUTOINCREMENT,
                "TimestampUtc" TEXT NOT NULL,
                "Actor" TEXT NOT NULL,
                "Action" TEXT NOT NULL,
                "TargetResource" TEXT NOT NULL,
                "Outcome" TEXT NOT NULL,
                "Details" TEXT NOT NULL,
                "ServerId" TEXT NULL,
                "ApplicationId" TEXT NULL
            );
            CREATE INDEX IF NOT EXISTS "IX_AuditLogs_TimestampUtc" ON "AuditLogs" ("TimestampUtc");
            """;

        await ExecuteNonQueryAsync(conn, tx, baselineDdl, ct).ConfigureAwait(false);
    }

    private static async Task EnsureTimestampIndexesAsync(
        SqliteConnection conn,
        SqliteTransaction tx,
        CancellationToken ct)
    {
        const string indexDdl = """
            CREATE INDEX IF NOT EXISTS "IX_Deployments_ApplicationId_QueuedAtUtc" ON "Deployments" ("ApplicationId", "QueuedAtUtc");
            CREATE INDEX IF NOT EXISTS "IX_AuditLogs_TimestampUtc" ON "AuditLogs" ("TimestampUtc");
            """;
        await ExecuteNonQueryAsync(conn, tx, indexDdl, ct).ConfigureAwait(false);
    }

    private static async Task<int> NormalizeAllTimestampsAsync(
        SqliteConnection conn,
        SqliteTransaction tx,
        CancellationToken ct)
    {
        var updatedTotal = 0;

        foreach (var (table, pkCol, timestampCols) in TimestampAuditTargets)
        {
            if (!await TableExistsAsync(conn, tx, table, ct).ConfigureAwait(false))
                continue;

            var existingColumns = await GetTableColumnsAsync(conn, tx, table, ct).ConfigureAwait(false);
            if (!existingColumns.Contains(pkCol))
                continue;

            foreach (var col in timestampCols)
            {
                if (!existingColumns.Contains(col))
                    continue;

                var pendingUpdates = new List<(object PkValue, string CanonicalUtc)>();

                await using (var selectCmd = conn.CreateCommand())
                {
                    selectCmd.Transaction = tx;
                    selectCmd.CommandText = $"SELECT \"{pkCol}\", \"{col}\" FROM \"{table}\" WHERE \"{col}\" IS NOT NULL;";
                    await using var reader = await selectCmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
                    while (await reader.ReadAsync(ct).ConfigureAwait(false))
                    {
                        var pkValue = reader.GetValue(0);
                        var rawValue = reader.GetString(1);
                        if (UtcIso8601DateTimeOffsetConverter.IsCanonicalUtcFormat(rawValue))
                            continue;

                        if (!UtcIso8601DateTimeOffsetConverter.TryParseUtc(rawValue, out var parsedUtc))
                        {
                            throw new FormatException(
                                $"Unparseable timestamp '{rawValue}' in {table}.{col} (key={pkValue}).");
                        }

                        var canonical = UtcIso8601DateTimeOffsetConverter.FormatUtc(parsedUtc);
                        if (!string.Equals(rawValue, canonical, StringComparison.Ordinal))
                            pendingUpdates.Add((pkValue, canonical));
                    }
                }

                if (pendingUpdates.Count == 0)
                    continue;

                await using var updateCmd = conn.CreateCommand();
                updateCmd.Transaction = tx;
                updateCmd.CommandText = $"UPDATE \"{table}\" SET \"{col}\" = $canonical WHERE \"{pkCol}\" = $pk;";
                var canonicalParam = updateCmd.Parameters.Add("$canonical", SqliteType.Text);
                var pkParam = updateCmd.Parameters.Add("$pk", SqliteType.Text);

                foreach (var (pkValue, canonicalUtc) in pendingUpdates)
                {
                    canonicalParam.Value = canonicalUtc;
                    pkParam.SqliteType = pkValue is long or int ? SqliteType.Integer : SqliteType.Text;
                    pkParam.Value = pkValue;
                    await updateCmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
                    updatedTotal++;
                }
            }
        }

        return updatedTotal;
    }

    private static async Task ApplyConnectionPragmasAsync(SqliteConnection conn, CancellationToken ct)
    {
        await ExecuteNonQueryAsync(conn, null, "PRAGMA journal_mode=WAL; PRAGMA foreign_keys=ON;", ct).ConfigureAwait(false);
    }

    private static async Task<int> CountUserTablesAsync(SqliteConnection conn, CancellationToken ct)
    {
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT COUNT(*) FROM "sqlite_master"
            WHERE "type" = 'table'
              AND "name" NOT LIKE 'sqlite_%'
              AND "name" <> '__ServerForgeSchemaVersions';
            """;
        return Convert.ToInt32(await cmd.ExecuteScalarAsync(ct).ConfigureAwait(false), CultureInfo.InvariantCulture);
    }

    private static async Task<bool> TableExistsAsync(
        SqliteConnection conn,
        SqliteTransaction? tx,
        string tableName,
        CancellationToken ct)
    {
        await using var cmd = conn.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = "SELECT COUNT(*) FROM \"sqlite_master\" WHERE \"type\" = 'table' AND \"name\" = $name;";
        cmd.Parameters.AddWithValue("$name", tableName);
        return Convert.ToInt32(await cmd.ExecuteScalarAsync(ct).ConfigureAwait(false), CultureInfo.InvariantCulture) > 0;
    }

    private static async Task<HashSet<string>> GetTableColumnsAsync(
        SqliteConnection conn,
        SqliteTransaction? tx,
        string tableName,
        CancellationToken ct)
    {
        var cols = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        await using var cmd = conn.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = $"PRAGMA table_info(\"{tableName}\");";
        await using var reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
        while (await reader.ReadAsync(ct).ConfigureAwait(false))
            cols.Add(reader.GetString(1));
        return cols;
    }

    private static async Task<int> ReadCurrentVersionAsync(
        SqliteConnection conn,
        SqliteTransaction? tx,
        CancellationToken ct)
    {
        await using var cmd = conn.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = "SELECT COALESCE(MAX(\"Version\"), 0) FROM \"__ServerForgeSchemaVersions\";";
        return Convert.ToInt32(await cmd.ExecuteScalarAsync(ct).ConfigureAwait(false), CultureInfo.InvariantCulture);
    }

    private static async Task RecordVersionAsync(
        SqliteConnection conn,
        SqliteTransaction tx,
        int version,
        string name,
        string? backupPath,
        CancellationToken ct)
    {
        await using var cmd = conn.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = """
            INSERT OR REPLACE INTO "__ServerForgeSchemaVersions" ("Version", "Name", "AppliedAtUtc", "BackupPath")
            VALUES ($version, $name, $appliedAtUtc, $backupPath);
            """;
        cmd.Parameters.AddWithValue("$version", version);
        cmd.Parameters.AddWithValue("$name", name);
        cmd.Parameters.AddWithValue("$appliedAtUtc", UtcIso8601DateTimeOffsetConverter.FormatUtc(DateTimeOffset.UtcNow));
        cmd.Parameters.AddWithValue("$backupPath", (object?)backupPath ?? DBNull.Value);
        await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
    }

    private static async Task ExecuteNonQueryAsync(
        SqliteConnection conn,
        SqliteTransaction? tx,
        string sql,
        CancellationToken ct)
    {
        await using var cmd = conn.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = sql;
        await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
    }

    private static async Task SafeRollbackAsync(SqliteTransaction tx)
    {
        try
        {
            await tx.RollbackAsync(CancellationToken.None).ConfigureAwait(false);
        }
        catch
        {
            // Preserve the primary migration failure exception.
        }
    }

    private static bool IsInMemoryConnection(SqliteConnection conn)
    {
        var ds = conn.DataSource;
        return string.IsNullOrWhiteSpace(ds) ||
               ds.Equals(":memory:", StringComparison.OrdinalIgnoreCase) ||
               ds.StartsWith("file::memory:", StringComparison.OrdinalIgnoreCase);
    }
}
