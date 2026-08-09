using System.Buffers.Binary;
using System.Diagnostics;
using System.Globalization;
using System.Runtime.ExceptionServices;
using Microsoft.Data.Sqlite;

namespace ApexLab.Persistence.Database;

public sealed class DatabaseMigrator
{
    private readonly SqliteConnectionFactory _connectionFactory;
    private readonly DatabaseMigratorHooks _hooks;
    private readonly SemaphoreSlim _migrationGate = new(1, 1);

    public DatabaseMigrator(SqliteConnectionFactory connectionFactory)
        : this(connectionFactory, DatabaseMigratorHooks.None)
    {
    }

    internal DatabaseMigrator(
        SqliteConnectionFactory connectionFactory,
        DatabaseMigratorHooks hooks)
    {
        ArgumentNullException.ThrowIfNull(connectionFactory);
        ArgumentNullException.ThrowIfNull(hooks);

        _connectionFactory = connectionFactory;
        _hooks = hooks;
    }

    public async Task MigrateAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        _hooks.BeforeMigrationGateWait();
        await _migrationGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            await InspectExistingDatabaseAsync(cancellationToken).ConfigureAwait(false);

            await using var connection = _connectionFactory.CreateWritableConnection();
            await connection.OpenAsync(cancellationToken).ConfigureAwait(false);

            var databaseVersion = await ReadUserVersionAsync(
                connection,
                cancellationToken).ConfigureAwait(false);
            EnsureVersionIsSupported(databaseVersion);

            if (databaseVersion == DatabaseSchema.CurrentVersion)
            {
                await ValidateCurrentSchemaAsync(
                    connection,
                    cancellationToken).ConfigureAwait(false);
            }

            await ConfigureConnectionAsync(connection, cancellationToken).ConfigureAwait(false);
            if (databaseVersion == DatabaseSchema.CurrentVersion)
            {
                return;
            }

            await _hooks.BeforeMigrationTransactionAsync(
                connection,
                cancellationToken).ConfigureAwait(false);
            await ApplyInitialMigrationAsync(connection, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _migrationGate.Release();
        }
    }

    private async Task InspectExistingDatabaseAsync(CancellationToken cancellationToken)
    {
        if (!File.Exists(_connectionFactory.DatabaseFilePath))
        {
            return;
        }

        var existingSidecars = GetSidecarPaths(_connectionFactory.DatabaseFilePath)
            .Where(File.Exists)
            .ToArray();
        if (existingSidecars.Length != 0)
        {
            await InspectIsolatedSnapshotAsync(
                existingSidecars,
                cancellationToken).ConfigureAwait(false);
            return;
        }

        var databaseVersion = await ReadUserVersionFromHeaderAsync(
            _connectionFactory.DatabaseFilePath,
            cancellationToken).ConfigureAwait(false);
        EnsureVersionIsSupported(databaseVersion);

        if (databaseVersion == DatabaseSchema.CurrentVersion)
        {
            await using var inspection = _connectionFactory.CreateReadOnlyInspectionConnection();
            await inspection.OpenAsync(cancellationToken).ConfigureAwait(false);
            await ValidateCurrentSchemaAsync(inspection, cancellationToken).ConfigureAwait(false);
        }
    }

    private async Task InspectIsolatedSnapshotAsync(
        IReadOnlyList<string> existingSidecars,
        CancellationToken cancellationToken)
    {
        var snapshotDirectory = Path.Combine(
            Path.GetTempPath(),
            $"apexlab-database-inspection-{Guid.NewGuid():N}");
        Directory.CreateDirectory(snapshotDirectory);
        try
        {
            var snapshotDatabasePath = Path.Combine(
                snapshotDirectory,
                Path.GetFileName(_connectionFactory.DatabaseFilePath));
            await CopyFileForInspectionAsync(
                _connectionFactory.DatabaseFilePath,
                snapshotDatabasePath,
                cancellationToken).ConfigureAwait(false);
            foreach (var sidecarPath in existingSidecars.Where(
                static path => !path.EndsWith("-shm", StringComparison.OrdinalIgnoreCase)))
            {
                await CopyFileForInspectionAsync(
                    sidecarPath,
                    Path.Combine(snapshotDirectory, Path.GetFileName(sidecarPath)),
                    cancellationToken).ConfigureAwait(false);
            }

            var snapshotFactory = new SqliteConnectionFactory(snapshotDatabasePath);
            await using var snapshot = snapshotFactory.CreateWritableConnection();
            await snapshot.OpenAsync(cancellationToken).ConfigureAwait(false);
            var databaseVersion = await ReadUserVersionAsync(
                snapshot,
                cancellationToken).ConfigureAwait(false);
            EnsureVersionIsSupported(databaseVersion);
            if (databaseVersion == DatabaseSchema.CurrentVersion)
            {
                await ValidateCurrentSchemaAsync(
                    snapshot,
                    cancellationToken).ConfigureAwait(false);
            }
        }
        catch (Exception primaryFailure)
        {
            var cleanupFailures = DeleteSnapshotDirectory(snapshotDirectory);
            ThrowPreservingPrimary(primaryFailure, cleanupFailures);
        }

        var finalCleanupFailures = DeleteSnapshotDirectory(snapshotDirectory);
        if (finalCleanupFailures.Count == 1)
        {
            ExceptionDispatchInfo.Capture(finalCleanupFailures[0]).Throw();
        }

        if (finalCleanupFailures.Count > 1)
        {
            throw new AggregateException(
                "The isolated database inspection completed but snapshot cleanup failed.",
                finalCleanupFailures);
        }
    }

    private async Task ApplyInitialMigrationAsync(
        SqliteConnection connection,
        CancellationToken cancellationToken)
    {
        SqliteTransaction? transaction = (SqliteTransaction)await connection
            .BeginTransactionAsync(cancellationToken)
            .ConfigureAwait(false);

        try
        {
            await ExecuteNonQueryAsync(
                connection,
                transaction,
                $"""
                {DatabaseSchema.MigrationHistoryTableDefinition};

                {DatabaseSchema.ApplicationMetadataTableDefinition};
                """,
                cancellationToken).ConfigureAwait(false);

            await InsertMigrationHistoryAsync(
                connection,
                transaction,
                cancellationToken).ConfigureAwait(false);
            await InsertApplicationMetadataAsync(
                connection,
                transaction,
                cancellationToken).ConfigureAwait(false);
            await ExecuteNonQueryAsync(
                connection,
                transaction,
                $"PRAGMA user_version = {DatabaseSchema.CurrentVersion};",
                cancellationToken).ConfigureAwait(false);

            await _hooks.AfterMigrationAppliedAsync(
                connection,
                transaction,
                cancellationToken).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (Exception primaryFailure)
        {
            var cleanupFailures = new List<Exception>();
            try
            {
                await _hooks.RollbackAsync(
                    transaction,
                    CancellationToken.None).ConfigureAwait(false);
            }
            catch (Exception rollbackFailure)
            {
                cleanupFailures.Add(rollbackFailure);
            }

            try
            {
                await transaction.DisposeAsync().ConfigureAwait(false);
            }
            catch (Exception disposalFailure)
            {
                cleanupFailures.Add(disposalFailure);
            }

            transaction = null;
            ThrowPreservingPrimary(primaryFailure, cleanupFailures);
        }
        finally
        {
            if (transaction is not null)
            {
                await transaction.DisposeAsync().ConfigureAwait(false);
            }
        }
    }

    private static async Task InsertMigrationHistoryAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText =
            $"""
            INSERT INTO {DatabaseSchema.MigrationHistoryTable} (
                {DatabaseSchema.VersionColumn},
                {DatabaseSchema.NameColumn},
                {DatabaseSchema.AppliedUtcColumn})
            VALUES ($version, $name, $appliedUtc);
            """;
        command.Parameters.AddWithValue("$version", DatabaseSchema.CurrentVersion);
        command.Parameters.AddWithValue("$name", DatabaseSchema.InitialMigrationName);
        command.Parameters.AddWithValue(
            "$appliedUtc",
            DateTimeOffset.UtcNow.ToString("O", CultureInfo.InvariantCulture));
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private static async Task InsertApplicationMetadataAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText =
            $"""
            INSERT INTO {DatabaseSchema.ApplicationMetadataTable} (
                {DatabaseSchema.KeyColumn},
                {DatabaseSchema.ValueColumn})
            VALUES ($applicationNameKey, $applicationNameValue),
                   ($schemaVersionKey, $schemaVersionValue);
            """;
        command.Parameters.AddWithValue("$applicationNameKey", "application_name");
        command.Parameters.AddWithValue("$applicationNameValue", DatabaseSchema.ApplicationName);
        command.Parameters.AddWithValue("$schemaVersionKey", "database_schema_version");
        command.Parameters.AddWithValue(
            "$schemaVersionValue",
            DatabaseSchema.CurrentVersion.ToString(CultureInfo.InvariantCulture));
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private async Task ConfigureConnectionAsync(
        SqliteConnection connection,
        CancellationToken cancellationToken)
    {
        await ExecuteNonQueryAsync(
            connection,
            transaction: null,
            "PRAGMA foreign_keys = ON;",
            cancellationToken).ConfigureAwait(false);
        await ExecuteNonQueryAsync(
            connection,
            transaction: null,
            $"PRAGMA busy_timeout = {DatabaseSchema.BusyTimeoutMilliseconds};",
            cancellationToken).ConfigureAwait(false);
        _ = await ExecuteScalarAsync(
            connection,
            "PRAGMA journal_mode = WAL;",
            cancellationToken).ConfigureAwait(false);

        await _hooks.AfterConnectionPragmasAppliedAsync(
            connection,
            cancellationToken).ConfigureAwait(false);
        await VerifyIntegerPragmaAsync(
            connection,
            "foreign_keys",
            expectedValue: 1,
            cancellationToken).ConfigureAwait(false);
        await VerifyIntegerPragmaAsync(
            connection,
            "busy_timeout",
            DatabaseSchema.BusyTimeoutMilliseconds,
            cancellationToken).ConfigureAwait(false);

        var journalMode = await ExecuteScalarAsync(
            connection,
            "PRAGMA journal_mode;",
            cancellationToken).ConfigureAwait(false);
        if (!string.Equals(
            Convert.ToString(journalMode, CultureInfo.InvariantCulture),
            "wal",
            StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDatabaseConfigurationException(
                "journal_mode",
                "wal",
                Convert.ToString(journalMode, CultureInfo.InvariantCulture));
        }
    }

    private static async Task ValidateCurrentSchemaAsync(
        SqliteConnection connection,
        CancellationToken cancellationToken)
    {
        try
        {
            var schemaManifest = await ReadRowsAsync(
                connection,
                """
                SELECT type, name, tbl_name, COALESCE(sql, '')
                FROM sqlite_schema
                WHERE name NOT GLOB 'sqlite_*'
                ORDER BY type, name;
                """,
                fieldCount: 4,
                cancellationToken).ConfigureAwait(false);
            var expectedManifest = new[]
            {
                new[]
                {
                    "table",
                    DatabaseSchema.ApplicationMetadataTable,
                    DatabaseSchema.ApplicationMetadataTable,
                    DatabaseSchema.ApplicationMetadataTableDefinition,
                },
                new[]
                {
                    "table",
                    DatabaseSchema.MigrationHistoryTable,
                    DatabaseSchema.MigrationHistoryTable,
                    DatabaseSchema.MigrationHistoryTableDefinition,
                },
            };
            if (!schemaManifest.Select(static row => string.Join('|', row)).SequenceEqual(
                expectedManifest.Select(static row => string.Join('|', row))))
            {
                throw new InvalidDatabaseSchemaException(
                    DatabaseSchema.CurrentVersion,
                    "The SQLite schema manifest does not match schema version 1.");
            }

            var tables = await ReadRowsAsync(
                connection,
                """
                SELECT name
                FROM sqlite_schema
                WHERE type = 'table' AND name NOT GLOB 'sqlite_*'
                ORDER BY name;
                """,
                fieldCount: 1,
                cancellationToken).ConfigureAwait(false);
            var expectedTables = new[]
            {
                DatabaseSchema.ApplicationMetadataTable,
                DatabaseSchema.MigrationHistoryTable,
            };
            if (!tables.Select(static row => row[0]).SequenceEqual(expectedTables))
            {
                throw new InvalidDatabaseSchemaException(
                    DatabaseSchema.CurrentVersion,
                    "The application table set does not match schema version 1.");
            }

            await ValidateTableColumnsAsync(
                connection,
                DatabaseSchema.MigrationHistoryTable,
                [
                    $"{DatabaseSchema.VersionColumn}|INTEGER|1|1",
                    $"{DatabaseSchema.NameColumn}|TEXT|1|0",
                    $"{DatabaseSchema.AppliedUtcColumn}|TEXT|1|0",
                ],
                cancellationToken).ConfigureAwait(false);
            await ValidateTableColumnsAsync(
                connection,
                DatabaseSchema.ApplicationMetadataTable,
                [
                    $"{DatabaseSchema.KeyColumn}|TEXT|1|1",
                    $"{DatabaseSchema.ValueColumn}|TEXT|1|0",
                ],
                cancellationToken).ConfigureAwait(false);

            var historyRows = await ReadRowsAsync(
                connection,
                $"""
                SELECT {DatabaseSchema.VersionColumn},
                       {DatabaseSchema.NameColumn},
                       {DatabaseSchema.AppliedUtcColumn}
                FROM {DatabaseSchema.MigrationHistoryTable}
                ORDER BY {DatabaseSchema.VersionColumn};
                """,
                fieldCount: 3,
                cancellationToken).ConfigureAwait(false);
            if (historyRows.Count != 1
                || historyRows[0][0] != DatabaseSchema.CurrentVersion.ToString(
                    CultureInfo.InvariantCulture)
                || historyRows[0][1] != DatabaseSchema.InitialMigrationName
                || !DateTimeOffset.TryParseExact(
                    historyRows[0][2],
                    "O",
                    CultureInfo.InvariantCulture,
                    DateTimeStyles.RoundtripKind,
                    out _))
            {
                throw new InvalidDatabaseSchemaException(
                    DatabaseSchema.CurrentVersion,
                    "The migration history does not match schema version 1.");
            }

            var metadataRows = await ReadRowsAsync(
                connection,
                $"""
                SELECT {DatabaseSchema.KeyColumn}, {DatabaseSchema.ValueColumn}
                FROM {DatabaseSchema.ApplicationMetadataTable}
                ORDER BY {DatabaseSchema.KeyColumn};
                """,
                fieldCount: 2,
                cancellationToken).ConfigureAwait(false);
            var expectedMetadata = new[]
            {
                new[] { "application_name", DatabaseSchema.ApplicationName },
                new[]
                {
                    "database_schema_version",
                    DatabaseSchema.CurrentVersion.ToString(CultureInfo.InvariantCulture),
                },
            };
            if (!metadataRows.Select(static row => string.Join('=', row)).SequenceEqual(
                expectedMetadata.Select(static row => string.Join('=', row))))
            {
                throw new InvalidDatabaseSchemaException(
                    DatabaseSchema.CurrentVersion,
                    "The application metadata does not match schema version 1.");
            }
        }
        catch (SqliteException exception)
        {
            throw new InvalidDatabaseSchemaException(
                DatabaseSchema.CurrentVersion,
                "The schema cannot be read as version 1 metadata.",
                exception);
        }
    }

    private static async Task ValidateTableColumnsAsync(
        SqliteConnection connection,
        string tableName,
        IReadOnlyList<string> expectedColumns,
        CancellationToken cancellationToken)
    {
        var rows = await ReadRowsAsync(
            connection,
            $"PRAGMA table_info({tableName});",
            fieldCount: 6,
            cancellationToken).ConfigureAwait(false);
        var actualColumns = rows.Select(
            static row => $"{row[1]}|{row[2]}|{row[3]}|{row[5]}");
        if (!actualColumns.SequenceEqual(expectedColumns))
        {
            throw new InvalidDatabaseSchemaException(
                DatabaseSchema.CurrentVersion,
                $"Table {tableName} does not match schema version 1.");
        }
    }

    private static async Task<IReadOnlyList<string[]>> ReadRowsAsync(
        SqliteConnection connection,
        string commandText,
        int fieldCount,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = commandText;
        var rows = new List<string[]>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            var row = new string[fieldCount];
            for (var index = 0; index < fieldCount; index++)
            {
                row[index] = Convert.ToString(
                    reader.GetValue(index),
                    CultureInfo.InvariantCulture) ?? string.Empty;
            }

            rows.Add(row);
        }

        return rows;
    }

    private static async Task<int> ReadUserVersionFromHeaderAsync(
        string databaseFilePath,
        CancellationToken cancellationToken)
    {
        const int headerLength = 100;
        var header = new byte[headerLength];
        await using var stream = new FileStream(
            databaseFilePath,
            FileMode.Open,
            FileAccess.Read,
            FileShare.ReadWrite | FileShare.Delete,
            bufferSize: headerLength,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        var bytesRead = await stream.ReadAtLeastAsync(
            header,
            headerLength,
            throwOnEndOfStream: false,
            cancellationToken).ConfigureAwait(false);
        var expectedMagic = "SQLite format 3\0"u8;
        if (bytesRead != headerLength || !header.AsSpan(0, expectedMagic.Length).SequenceEqual(expectedMagic))
        {
            throw new InvalidDatabaseSchemaException(
                databaseVersion: -1,
                "The file does not contain a valid SQLite database header.");
        }

        return BinaryPrimitives.ReadInt32BigEndian(header.AsSpan(60, sizeof(int)));
    }

    private static IReadOnlyList<Exception> DeleteSnapshotDirectory(string snapshotDirectory)
    {
        try
        {
            Directory.Delete(snapshotDirectory, recursive: true);
            return [];
        }
        catch (Exception exception)
        {
            return [exception];
        }
    }

    private static async Task CopyFileForInspectionAsync(
        string sourcePath,
        string destinationPath,
        CancellationToken cancellationToken)
    {
        await using var source = new FileStream(
            sourcePath,
            FileMode.Open,
            FileAccess.Read,
            FileShare.ReadWrite | FileShare.Delete,
            bufferSize: 81_920,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        await using var destination = new FileStream(
            destinationPath,
            FileMode.CreateNew,
            FileAccess.Write,
            FileShare.None,
            bufferSize: 81_920,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        await source.CopyToAsync(destination, cancellationToken).ConfigureAwait(false);
        await destination.FlushAsync(cancellationToken).ConfigureAwait(false);
    }

    private static IEnumerable<string> GetSidecarPaths(string databaseFilePath)
    {
        yield return $"{databaseFilePath}-wal";
        yield return $"{databaseFilePath}-shm";
        yield return $"{databaseFilePath}-journal";
    }

    private static async Task VerifyIntegerPragmaAsync(
        SqliteConnection connection,
        string pragmaName,
        int expectedValue,
        CancellationToken cancellationToken)
    {
        var value = await ExecuteScalarAsync(
            connection,
            $"PRAGMA {pragmaName};",
            cancellationToken).ConfigureAwait(false);
        var actualValue = Convert.ToInt32(value, CultureInfo.InvariantCulture);
        if (actualValue != expectedValue)
        {
            throw new InvalidDatabaseConfigurationException(
                pragmaName,
                expectedValue.ToString(CultureInfo.InvariantCulture),
                actualValue.ToString(CultureInfo.InvariantCulture));
        }
    }

    private static async Task<object?> ExecuteScalarAsync(
        SqliteConnection connection,
        string commandText,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = commandText;
        return await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
    }

    private static async Task<int> ReadUserVersionAsync(
        SqliteConnection connection,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = "PRAGMA user_version;";
        var value = await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
        return Convert.ToInt32(value, CultureInfo.InvariantCulture);
    }

    private static async Task ExecuteNonQueryAsync(
        SqliteConnection connection,
        SqliteTransaction? transaction,
        string commandText,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = commandText;
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private static void EnsureVersionIsSupported(int databaseVersion)
    {
        if (databaseVersion < 0)
        {
            throw new InvalidDatabaseSchemaException(databaseVersion);
        }

        if (databaseVersion > DatabaseSchema.CurrentVersion)
        {
            throw new UnsupportedDatabaseVersionException(
                databaseVersion,
                DatabaseSchema.CurrentVersion);
        }
    }

    private static void ThrowPreservingPrimary(
        Exception primaryFailure,
        IReadOnlyList<Exception> cleanupFailures)
    {
        if (cleanupFailures.Count == 0)
        {
            ExceptionDispatchInfo.Capture(primaryFailure).Throw();
            throw new UnreachableException();
        }

        throw new AggregateException(
            "The database migration failed and cleanup also reported failures.",
            new[] { primaryFailure }.Concat(cleanupFailures));
    }
}

public sealed class UnsupportedDatabaseVersionException : Exception
{
    public UnsupportedDatabaseVersionException(int databaseVersion, int supportedVersion)
        : base(
            $"Database schema version {databaseVersion} is newer than supported version {supportedVersion}.")
    {
        DatabaseVersion = databaseVersion;
        SupportedVersion = supportedVersion;
    }

    public int DatabaseVersion { get; }

    public int SupportedVersion { get; }
}

public sealed class InvalidDatabaseSchemaException : Exception
{
    public InvalidDatabaseSchemaException(int databaseVersion)
        : this(databaseVersion, $"Database schema version {databaseVersion} is invalid.")
    {
    }

    public InvalidDatabaseSchemaException(int databaseVersion, string message)
        : base(message)
    {
        DatabaseVersion = databaseVersion;
    }

    public InvalidDatabaseSchemaException(
        int databaseVersion,
        string message,
        Exception innerException)
        : base(message, innerException)
    {
        DatabaseVersion = databaseVersion;
    }

    public int DatabaseVersion { get; }
}

public sealed class InvalidDatabaseConfigurationException : Exception
{
    public InvalidDatabaseConfigurationException(
        string settingName,
        string expectedValue,
        string? actualValue)
        : base(
            $"SQLite setting {settingName} must be {expectedValue}, but the effective value was {actualValue ?? "null"}.")
    {
        SettingName = settingName;
        ExpectedValue = expectedValue;
        ActualValue = actualValue;
    }

    public string SettingName { get; }

    public string ExpectedValue { get; }

    public string? ActualValue { get; }
}

internal sealed class DatabaseMigratorHooks
{
    internal static DatabaseMigratorHooks None { get; } = new();

    internal Action BeforeMigrationGateWait { get; init; } = static () => { };

    internal Func<SqliteConnection, CancellationToken, ValueTask> BeforeMigrationTransactionAsync
    { get; init; } = static (_, _) => ValueTask.CompletedTask;

    internal Func<SqliteConnection, CancellationToken, ValueTask>
        AfterConnectionPragmasAppliedAsync
    { get; init; } = static (_, _) => ValueTask.CompletedTask;

    internal Func<SqliteConnection, SqliteTransaction, CancellationToken, ValueTask>
        AfterMigrationAppliedAsync
    { get; init; } = static (_, _, _) => ValueTask.CompletedTask;

    internal Func<SqliteTransaction, CancellationToken, ValueTask> RollbackAsync
    { get; init; } = static (transaction, cancellationToken) =>
        new ValueTask(transaction.RollbackAsync(cancellationToken));
}
