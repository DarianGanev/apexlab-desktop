using System.Buffers.Binary;
using System.Globalization;
using System.Text;
using ApexLab.Persistence.Database;
using Microsoft.Data.Sqlite;

namespace ApexLab.Persistence.Tests.Database;

[TestClass]
public sealed class DatabaseMigratorTests
{
    [TestMethod]
    public async Task FreshDatabase_CreatesOnlyApprovedMetadataSchema()
    {
        using var database = TemporaryDatabase.Create();
        var subject = new DatabaseMigrator(new SqliteConnectionFactory(database.FilePath));

        await subject.MigrateAsync(TestContext.CancellationToken);

        await using var connection = await database.OpenAsync(TestContext.CancellationToken);
        Assert.AreEqual(DatabaseSchema.CurrentVersion, await ReadUserVersionAsync(connection));
        CollectionAssert.AreEqual(
            new[]
            {
                DatabaseSchema.ApplicationMetadataTable,
                DatabaseSchema.MigrationHistoryTable,
            },
            (await ReadApplicationTableNamesAsync(connection)).ToArray());
    }

    [TestMethod]
    public async Task FreshDatabase_WritesExactMigrationAndApplicationMetadata()
    {
        using var database = TemporaryDatabase.Create();
        var subject = new DatabaseMigrator(new SqliteConnectionFactory(database.FilePath));

        await subject.MigrateAsync(TestContext.CancellationToken);

        await using var connection = await database.OpenAsync(TestContext.CancellationToken);
        await using var history = connection.CreateCommand();
        history.CommandText =
            $"SELECT version, name, applied_utc FROM {DatabaseSchema.MigrationHistoryTable};";
        await using var reader = await history.ExecuteReaderAsync(TestContext.CancellationToken);
        Assert.IsTrue(await reader.ReadAsync(TestContext.CancellationToken));
        Assert.AreEqual(1L, reader.GetInt64(0));
        Assert.AreEqual(DatabaseSchema.InitialMigrationName, reader.GetString(1));
        Assert.IsTrue(
            DateTimeOffset.TryParseExact(
                reader.GetString(2),
                "O",
                CultureInfo.InvariantCulture,
                DateTimeStyles.RoundtripKind,
                out _),
            "Migration timestamp must be an invariant round-trip timestamp.");
        Assert.IsFalse(await reader.ReadAsync(TestContext.CancellationToken));
        await reader.DisposeAsync();

        CollectionAssert.AreEqual(
            new[]
            {
                $"application_name={DatabaseSchema.ApplicationName}",
                $"database_schema_version={DatabaseSchema.CurrentVersion}",
            },
            (await ReadMetadataRowsAsync(connection)).ToArray());
    }

    [TestMethod]
    public async Task SupportedDatabase_ConfiguresConnectionBeforeMigrationTransaction()
    {
        using var database = TemporaryDatabase.Create();
        var hookObserved = false;
        var hooks = new DatabaseMigratorHooks
        {
            BeforeMigrationTransactionAsync = async (connection, cancellationToken) =>
            {
                Assert.AreEqual(1L, await ReadPragmaAsync(connection, "foreign_keys", cancellationToken));
                Assert.AreEqual(
                    DatabaseSchema.BusyTimeoutMilliseconds,
                    await ReadPragmaAsync(connection, "busy_timeout", cancellationToken));
                Assert.AreEqual(
                    "wal",
                    await ReadTextPragmaAsync(connection, "journal_mode", cancellationToken));
                hookObserved = true;
            },
        };
        var subject = new DatabaseMigrator(
            new SqliteConnectionFactory(database.FilePath),
            hooks);

        await subject.MigrateAsync(TestContext.CancellationToken);

        Assert.IsTrue(hookObserved, "The migration transaction hook was not reached.");
        await using var verification = await database.OpenAsync(TestContext.CancellationToken);
        Assert.AreEqual("wal", await ReadTextPragmaAsync(
            verification,
            "journal_mode",
            TestContext.CancellationToken));
    }

    [TestMethod]
    public async Task IneffectiveConnectionConfiguration_IsRejectedBeforeMigrationTransaction()
    {
        using var database = TemporaryDatabase.Create();
        var transactionHookReached = false;
        var hooks = new DatabaseMigratorHooks
        {
            AfterConnectionPragmasAppliedAsync = async (connection, cancellationToken) =>
            {
                await using var command = connection.CreateCommand();
                command.CommandText = "PRAGMA journal_mode = DELETE;";
                await command.ExecuteNonQueryAsync(cancellationToken);
            },
            BeforeMigrationTransactionAsync = (_, _) =>
            {
                transactionHookReached = true;
                return ValueTask.CompletedTask;
            },
        };
        var subject = new DatabaseMigrator(
            new SqliteConnectionFactory(database.FilePath),
            hooks);

        var exception = await Assert.ThrowsExactlyAsync<InvalidDatabaseConfigurationException>(
            () => subject.MigrateAsync(TestContext.CancellationToken));

        Assert.AreEqual("journal_mode", exception.SettingName);
        Assert.AreEqual("wal", exception.ExpectedValue);
        Assert.AreEqual("delete", exception.ActualValue);
        Assert.IsFalse(transactionHookReached);
        await AssertDatabaseIsUnmigratedAsync(database, TestContext.CancellationToken);
    }

    [TestMethod]
    public async Task MigrationOne_RepeatedRunPreservesSchemaAndRows()
    {
        using var database = TemporaryDatabase.Create();
        var subject = new DatabaseMigrator(new SqliteConnectionFactory(database.FilePath));
        await subject.MigrateAsync(TestContext.CancellationToken);
        var before = await ReadDatabaseSnapshotAsync(database, TestContext.CancellationToken);

        await subject.MigrateAsync(TestContext.CancellationToken);

        var after = await ReadDatabaseSnapshotAsync(database, TestContext.CancellationToken);
        Assert.AreEqual(before, after);
    }

    [TestMethod]
    public async Task CurrentVersionWithoutRequiredMetadata_IsRejectedWithoutMutation()
    {
        using var database = TemporaryDatabase.Create();
        await database.CreateAtVersionAsync(
            DatabaseSchema.CurrentVersion,
            TestContext.CancellationToken);
        var before = await File.ReadAllBytesAsync(database.FilePath, TestContext.CancellationToken);
        database.AssertNoSidecars();
        var subject = new DatabaseMigrator(new SqliteConnectionFactory(database.FilePath));

        var exception = await Assert.ThrowsExactlyAsync<InvalidDatabaseSchemaException>(
            () => subject.MigrateAsync(TestContext.CancellationToken));

        Assert.AreEqual(DatabaseSchema.CurrentVersion, exception.DatabaseVersion);
        CollectionAssert.AreEqual(
            before,
            await File.ReadAllBytesAsync(database.FilePath, TestContext.CancellationToken));
        database.AssertNoSidecars();
    }

    [TestMethod]
    public async Task CurrentVersionWithWildcardMimickingSchemaObject_IsRejected()
    {
        using var database = TemporaryDatabase.Create();
        var migrated = new DatabaseMigrator(new SqliteConnectionFactory(database.FilePath));
        await migrated.MigrateAsync(TestContext.CancellationToken);
        await using (var connection = await database.OpenAsync(TestContext.CancellationToken))
        {
            await using var command = connection.CreateCommand();
            command.CommandText =
                $"CREATE INDEX sqliteEvil ON {DatabaseSchema.ApplicationMetadataTable} ({DatabaseSchema.ValueColumn});";
            await command.ExecuteNonQueryAsync(TestContext.CancellationToken);
        }

        var subject = new DatabaseMigrator(new SqliteConnectionFactory(database.FilePath));

        var exception = await Assert.ThrowsExactlyAsync<InvalidDatabaseSchemaException>(
            () => subject.MigrateAsync(TestContext.CancellationToken));

        Assert.AreEqual(DatabaseSchema.CurrentVersion, exception.DatabaseVersion);
    }

    [TestMethod]
    public async Task NewerDatabase_IsRejectedWithoutChangingFileOrCreatingSidecars()
    {
        using var database = TemporaryDatabase.Create();
        await database.CreateAtVersionAsync(
            DatabaseSchema.CurrentVersion + 1,
            TestContext.CancellationToken);
        var before = await File.ReadAllBytesAsync(database.FilePath, TestContext.CancellationToken);
        database.AssertNoSidecars();
        var subject = new DatabaseMigrator(new SqliteConnectionFactory(database.FilePath));

        var exception = await Assert.ThrowsExactlyAsync<UnsupportedDatabaseVersionException>(
            () => subject.MigrateAsync(TestContext.CancellationToken));

        Assert.AreEqual(DatabaseSchema.CurrentVersion + 1, exception.DatabaseVersion);
        Assert.AreEqual(DatabaseSchema.CurrentVersion, exception.SupportedVersion);
        CollectionAssert.AreEqual(
            before,
            await File.ReadAllBytesAsync(database.FilePath, TestContext.CancellationToken));
        database.AssertNoSidecars();
    }

    [TestMethod]
    public async Task NewerWalDatabase_IsRejectedWithoutChangingFileOrCreatingSidecars()
    {
        using var database = TemporaryDatabase.Create();
        await database.CreateAtVersionAsync(
            DatabaseSchema.CurrentVersion + 1,
            TestContext.CancellationToken,
            useWriteAheadLog: true);
        var before = await File.ReadAllBytesAsync(database.FilePath, TestContext.CancellationToken);
        database.AssertNoSidecars();
        var subject = new DatabaseMigrator(new SqliteConnectionFactory(database.FilePath));

        await Assert.ThrowsExactlyAsync<UnsupportedDatabaseVersionException>(
            () => subject.MigrateAsync(TestContext.CancellationToken));

        CollectionAssert.AreEqual(
            before,
            await File.ReadAllBytesAsync(database.FilePath, TestContext.CancellationToken));
        database.AssertNoSidecars();
    }

    [TestMethod]
    public async Task NewerVersionOnlyInUncheckpointedWal_IsRejectedWithoutChangingAnyOriginalFile()
    {
        using var database = TemporaryDatabase.Create();
        await using var walOwner = await database.OpenUncheckpointedWalAtVersionAsync(
            DatabaseSchema.CurrentVersion + 1,
            TestContext.CancellationToken);
        var mainHeader = await TemporaryDatabase.ReadFileBytesSharedAsync(
            database.FilePath,
            TestContext.CancellationToken);
        Assert.AreNotEqual(
            DatabaseSchema.CurrentVersion + 1,
            BinaryPrimitives.ReadInt32BigEndian(mainHeader.AsSpan(60, sizeof(int))),
            "The fixture must leave the future version in WAL rather than checkpointing page 1.");
        var before = await database.ReadExistingFilesAsync(TestContext.CancellationToken);
        var subject = new DatabaseMigrator(new SqliteConnectionFactory(database.FilePath));

        var exception = await Assert.ThrowsExactlyAsync<UnsupportedDatabaseVersionException>(
            () => subject.MigrateAsync(TestContext.CancellationToken));

        Assert.AreEqual(DatabaseSchema.CurrentVersion + 1, exception.DatabaseVersion);
        var after = await database.ReadExistingFilesAsync(TestContext.CancellationToken);
        CollectionAssert.AreEquivalent(before.Keys.ToArray(), after.Keys.ToArray());
        foreach (var path in before.Keys)
        {
            CollectionAssert.AreEqual(before[path], after[path], path);
        }
    }

    [TestMethod]
    public async Task NegativeDatabaseVersion_IsRejectedDeterministically()
    {
        using var database = TemporaryDatabase.Create();
        await database.CreateAtVersionAsync(-1, TestContext.CancellationToken);
        var before = await File.ReadAllBytesAsync(database.FilePath, TestContext.CancellationToken);
        var subject = new DatabaseMigrator(new SqliteConnectionFactory(database.FilePath));

        var exception = await Assert.ThrowsExactlyAsync<InvalidDatabaseSchemaException>(
            () => subject.MigrateAsync(TestContext.CancellationToken));

        Assert.AreEqual(-1, exception.DatabaseVersion);
        CollectionAssert.AreEqual(
            before,
            await File.ReadAllBytesAsync(database.FilePath, TestContext.CancellationToken));
        database.AssertNoSidecars();
    }

    [TestMethod]
    public async Task ForcedPreCommitFailure_RollsBackEntireMigrationAndCanRecover()
    {
        using var database = TemporaryDatabase.Create();
        var hooks = new DatabaseMigratorHooks
        {
            AfterMigrationAppliedAsync = static (_, _, _) =>
                ValueTask.FromException(new ForcedMigrationException()),
        };
        var failing = new DatabaseMigrator(
            new SqliteConnectionFactory(database.FilePath),
            hooks);

        await Assert.ThrowsExactlyAsync<ForcedMigrationException>(
            () => failing.MigrateAsync(TestContext.CancellationToken));

        await AssertDatabaseIsUnmigratedAsync(database, TestContext.CancellationToken);
        var recovered = new DatabaseMigrator(new SqliteConnectionFactory(database.FilePath));
        await recovered.MigrateAsync(TestContext.CancellationToken);
        await using var connection = await database.OpenAsync(TestContext.CancellationToken);
        Assert.AreEqual(DatabaseSchema.CurrentVersion, await ReadUserVersionAsync(connection));
    }

    [TestMethod]
    public async Task RollbackFailure_PreservesPrimaryFailureAndDisposesTransaction()
    {
        using var database = TemporaryDatabase.Create();
        var hooks = new DatabaseMigratorHooks
        {
            AfterMigrationAppliedAsync = static (_, _, _) =>
                ValueTask.FromException(new ForcedMigrationException()),
            RollbackAsync = static (_, _) =>
                ValueTask.FromException(new ForcedRollbackException()),
        };
        var subject = new DatabaseMigrator(
            new SqliteConnectionFactory(database.FilePath),
            hooks);

        var exception = await Assert.ThrowsExactlyAsync<AggregateException>(
            () => subject.MigrateAsync(TestContext.CancellationToken));

        Assert.HasCount(2, exception.InnerExceptions);
        Assert.IsInstanceOfType<ForcedMigrationException>(exception.InnerExceptions[0]);
        Assert.IsInstanceOfType<ForcedRollbackException>(exception.InnerExceptions[1]);
        await AssertDatabaseIsUnmigratedAsync(database, TestContext.CancellationToken);
        database.DeleteDatabaseFileImmediately();
    }

    [TestMethod]
    public void InvalidPaths_AreRejectedBeforeFilesystemMutation()
    {
        using var database = TemporaryDatabase.Create();
        var unsafeRoot = Path.Combine(database.DirectoryPath, "unsafe-target");
        var paths = new[]
        {
            Path.Combine("relative", "apexlab.db"),
            @"\\server\share\apexlab.db",
            @"\\?\C:\apexlab\apexlab.db",
            @"\??\C:\apexlab\apexlab.db",
            Path.Combine(database.DirectoryPath, "safe", "..", "unsafe-target", "apexlab.db"),
        };

        foreach (var path in paths)
        {
            Assert.ThrowsExactly<ArgumentException>(() => new SqliteConnectionFactory(path), path);
        }

        Assert.IsFalse(Directory.Exists(unsafeRoot));
        Assert.IsFalse(File.Exists(Path.Combine(unsafeRoot, "apexlab.db")));
    }

    [TestMethod]
    public void ConnectionFactory_UsesPrivateUnpooledBuilderConnections()
    {
        using var database = TemporaryDatabase.Create();
        var subject = new SqliteConnectionFactory(database.FilePath);

        using var writable = subject.CreateWritableConnection();
        using var inspection = subject.CreateReadOnlyInspectionConnection();

        AssertConnectionString(writable, SqliteOpenMode.ReadWriteCreate);
        AssertConnectionString(inspection, SqliteOpenMode.ReadOnly);
    }

    [TestMethod]
    public async Task CompletedMigration_ReleasesDatabaseFileForImmediateDeletion()
    {
        using var database = TemporaryDatabase.Create();
        var subject = new DatabaseMigrator(new SqliteConnectionFactory(database.FilePath));

        await subject.MigrateAsync(TestContext.CancellationToken);

        database.DeleteDatabaseFileImmediately();
    }

    [TestMethod]
    public async Task FailedMigration_ReleasesDatabaseFileForImmediateDeletion()
    {
        using var database = TemporaryDatabase.Create();
        var subject = new DatabaseMigrator(
            new SqliteConnectionFactory(database.FilePath),
            new DatabaseMigratorHooks
            {
                AfterMigrationAppliedAsync = static (_, _, _) =>
                    ValueTask.FromException(new ForcedMigrationException()),
            });
        await Assert.ThrowsExactlyAsync<ForcedMigrationException>(
            () => subject.MigrateAsync(TestContext.CancellationToken));

        database.DeleteDatabaseFileImmediately();
    }

    [TestMethod]
    public async Task UnsupportedMigration_ReleasesDatabaseFileForImmediateDeletion()
    {
        using var database = TemporaryDatabase.Create();
        await database.CreateAtVersionAsync(
            DatabaseSchema.CurrentVersion + 1,
            TestContext.CancellationToken);
        var subject = new DatabaseMigrator(new SqliteConnectionFactory(database.FilePath));
        await Assert.ThrowsExactlyAsync<UnsupportedDatabaseVersionException>(
            () => subject.MigrateAsync(TestContext.CancellationToken));

        database.DeleteDatabaseFileImmediately();
    }

    [TestMethod]
    public async Task PreCanceledMigration_DoesNotCreateDatabase()
    {
        using var database = TemporaryDatabase.Create();
        var subject = new DatabaseMigrator(new SqliteConnectionFactory(database.FilePath));
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        await Assert.ThrowsExactlyAsync<OperationCanceledException>(
            () => subject.MigrateAsync(cancellation.Token));

        Assert.IsFalse(File.Exists(database.FilePath));
        database.AssertNoSidecars();
    }

    [TestMethod]
    [Timeout(10_000, CooperativeCancellation = true)]
    public async Task CancellationDuringMigration_RollsBackEntireMigration()
    {
        using var database = TemporaryDatabase.Create();
        var hookEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var hooks = new DatabaseMigratorHooks
        {
            AfterMigrationAppliedAsync = async (_, _, cancellationToken) =>
            {
                hookEntered.TrySetResult();
                await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            },
        };
        var subject = new DatabaseMigrator(
            new SqliteConnectionFactory(database.FilePath),
            hooks);
        using var cancellation = new CancellationTokenSource();

        var migration = subject.MigrateAsync(cancellation.Token);
        await hookEntered.Task.WaitAsync(TimeSpan.FromSeconds(5), TestContext.CancellationToken);
        await cancellation.CancelAsync();
        await Assert.ThrowsExactlyAsync<TaskCanceledException>(() => migration);

        await AssertDatabaseIsUnmigratedAsync(database, TestContext.CancellationToken);
        database.DeleteDatabaseFileImmediately();
    }

    [TestMethod]
    [Timeout(10_000, CooperativeCancellation = true)]
    public async Task ConcurrentCalls_OnOneMigrator_AreSerializedAndWriteOneHistoryRow()
    {
        using var database = TemporaryDatabase.Create();
        var firstInsideMigration = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseFirstMigration = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var secondGateAttempt = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var gateAttempts = 0;
        var concurrentHooks = 0;
        var maximumConcurrentHooks = 0;
        var hooks = new DatabaseMigratorHooks
        {
            BeforeMigrationGateWait = () =>
            {
                if (Interlocked.Increment(ref gateAttempts) == 2)
                {
                    secondGateAttempt.TrySetResult();
                }
            },
            AfterMigrationAppliedAsync = async (_, _, cancellationToken) =>
            {
                var current = Interlocked.Increment(ref concurrentHooks);
                InterlockedExtensions.Max(ref maximumConcurrentHooks, current);
                firstInsideMigration.TrySetResult();
                try
                {
                    await releaseFirstMigration.Task.WaitAsync(cancellationToken);
                }
                finally
                {
                    Interlocked.Decrement(ref concurrentHooks);
                }
            },
        };
        var subject = new DatabaseMigrator(
            new SqliteConnectionFactory(database.FilePath),
            hooks);

        var first = subject.MigrateAsync(TestContext.CancellationToken);
        await firstInsideMigration.Task.WaitAsync(TimeSpan.FromSeconds(5), TestContext.CancellationToken);
        var second = subject.MigrateAsync(TestContext.CancellationToken);
        await secondGateAttempt.Task.WaitAsync(TimeSpan.FromSeconds(5), TestContext.CancellationToken);
        Assert.AreEqual(1, maximumConcurrentHooks);
        releaseFirstMigration.TrySetResult();

        await Task.WhenAll(first, second);
        Assert.AreEqual(1, maximumConcurrentHooks);
        await using var connection = await database.OpenAsync(TestContext.CancellationToken);
        Assert.AreEqual(1L, await ExecuteScalarInt64Async(
            connection,
            $"SELECT COUNT(*) FROM {DatabaseSchema.MigrationHistoryTable};",
            TestContext.CancellationToken));
        Assert.AreEqual(DatabaseSchema.CurrentVersion, await ReadUserVersionAsync(connection));
    }

    public TestContext TestContext { get; set; }

    private static void AssertConnectionString(SqliteConnection connection, SqliteOpenMode mode)
    {
        var builder = new SqliteConnectionStringBuilder(connection.ConnectionString);
        Assert.AreEqual(mode, builder.Mode);
        Assert.AreEqual(SqliteCacheMode.Private, builder.Cache);
        Assert.IsFalse(builder.Pooling);
        Assert.IsTrue(Path.IsPathFullyQualified(builder.DataSource));
    }

    private static async Task AssertDatabaseIsUnmigratedAsync(
        TemporaryDatabase database,
        CancellationToken cancellationToken)
    {
        await using var connection = await database.OpenAsync(cancellationToken);
        Assert.AreEqual(0L, await ReadUserVersionAsync(connection));
        Assert.IsEmpty(await ReadApplicationTableNamesAsync(connection));
    }

    private static async Task<string> ReadDatabaseSnapshotAsync(
        TemporaryDatabase database,
        CancellationToken cancellationToken)
    {
        await using var connection = await database.OpenAsync(cancellationToken);
        var result = new StringBuilder();
        await using (var schema = connection.CreateCommand())
        {
            schema.CommandText =
                "SELECT type, name, tbl_name, COALESCE(sql, '') FROM sqlite_schema ORDER BY type, name;";
            await using var reader = await schema.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
            {
                result.Append(reader.GetString(0)).Append('|')
                    .Append(reader.GetString(1)).Append('|')
                    .Append(reader.GetString(2)).Append('|')
                    .Append(reader.GetString(3)).AppendLine();
            }
        }

        result.AppendLine($"version={await ReadUserVersionAsync(connection)}");
        foreach (var row in await ReadMetadataRowsAsync(connection))
        {
            result.AppendLine(row);
        }

        await using var history = connection.CreateCommand();
        history.CommandText =
            $"SELECT version, name, applied_utc FROM {DatabaseSchema.MigrationHistoryTable} ORDER BY version;";
        await using var historyReader = await history.ExecuteReaderAsync(cancellationToken);
        while (await historyReader.ReadAsync(cancellationToken))
        {
            result.Append(historyReader.GetInt64(0)).Append('|')
                .Append(historyReader.GetString(1)).Append('|')
                .Append(historyReader.GetString(2)).AppendLine();
        }

        return result.ToString();
    }

    private static async Task<IReadOnlyList<string>> ReadMetadataRowsAsync(SqliteConnection connection)
    {
        await using var command = connection.CreateCommand();
        command.CommandText =
            $"SELECT key, value FROM {DatabaseSchema.ApplicationMetadataTable} ORDER BY key;";
        var rows = new List<string>();
        await using var reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            rows.Add($"{reader.GetString(0)}={reader.GetString(1)}");
        }

        return rows;
    }

    private static async Task<long> ReadUserVersionAsync(SqliteConnection connection)
    {
        return await ReadPragmaAsync(connection, "user_version", CancellationToken.None);
    }

    private static async Task<long> ReadPragmaAsync(
        SqliteConnection connection,
        string pragma,
        CancellationToken cancellationToken)
    {
        return await ExecuteScalarInt64Async(
            connection,
            $"PRAGMA {pragma};",
            cancellationToken);
    }

    private static async Task<string> ReadTextPragmaAsync(
        SqliteConnection connection,
        string pragma,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = $"PRAGMA {pragma};";
        return (string)(await command.ExecuteScalarAsync(cancellationToken))!;
    }

    private static async Task<long> ExecuteScalarInt64Async(
        SqliteConnection connection,
        string sql,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        return (long)(await command.ExecuteScalarAsync(cancellationToken))!;
    }

    private static async Task<IReadOnlyList<string>> ReadApplicationTableNamesAsync(
        SqliteConnection connection)
    {
        await using var command = connection.CreateCommand();
        command.CommandText =
            """
            SELECT name
            FROM sqlite_schema
            WHERE type = 'table' AND name NOT LIKE 'sqlite_%'
            ORDER BY name;
            """;

        var names = new List<string>();
        await using var reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            names.Add(reader.GetString(0));
        }

        return names;
    }

    private sealed class ForcedMigrationException : Exception;

    private sealed class ForcedRollbackException : Exception;
}

internal sealed class TemporaryDatabase : IDisposable
{
    private readonly string _directoryPath;

    private TemporaryDatabase(string directoryPath)
    {
        _directoryPath = directoryPath;
        FilePath = Path.Combine(directoryPath, "apexlab.db");
    }

    public string DirectoryPath => _directoryPath;

    public string FilePath { get; }

    public static TemporaryDatabase Create()
    {
        var directoryPath = Path.Combine(
            Path.GetTempPath(),
            $"apexlab-sqlite-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directoryPath);
        return new TemporaryDatabase(directoryPath);
    }

    public async Task CreateAtVersionAsync(
        int version,
        CancellationToken cancellationToken,
        bool useWriteAheadLog = false)
    {
        await using var connection = await OpenAsync(
            cancellationToken,
            SqliteOpenMode.ReadWriteCreate);
        await using var command = connection.CreateCommand();
        command.CommandText = useWriteAheadLog
            ? $"""
              PRAGMA journal_mode = WAL;
              PRAGMA user_version = {version.ToString(CultureInfo.InvariantCulture)};
              """
            : $"PRAGMA user_version = {version.ToString(CultureInfo.InvariantCulture)};";
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task<SqliteConnection> OpenUncheckpointedWalAtVersionAsync(
        int version,
        CancellationToken cancellationToken)
    {
        var connection = await OpenAsync(cancellationToken, SqliteOpenMode.ReadWriteCreate);
        try
        {
            await using var command = connection.CreateCommand();
            command.CommandText =
                $"""
                PRAGMA journal_mode = WAL;
                PRAGMA wal_autocheckpoint = 0;
                CREATE TABLE wal_evidence (value INTEGER NOT NULL);
                INSERT INTO wal_evidence (value) VALUES (1);
                PRAGMA user_version = {version.ToString(CultureInfo.InvariantCulture)};
                """;
            await command.ExecuteNonQueryAsync(cancellationToken);
            return connection;
        }
        catch
        {
            await connection.DisposeAsync();
            throw;
        }
    }

    public async Task<IReadOnlyDictionary<string, byte[]>> ReadExistingFilesAsync(
        CancellationToken cancellationToken)
    {
        var files = new Dictionary<string, byte[]>(StringComparer.OrdinalIgnoreCase);
        foreach (var path in new[] { FilePath }.Concat(SidecarPaths()))
        {
            if (File.Exists(path))
            {
                files.Add(path, await ReadFileBytesSharedAsync(path, cancellationToken));
            }
        }

        return files;
    }

    public static async Task<byte[]> ReadFileBytesSharedAsync(
        string path,
        CancellationToken cancellationToken)
    {
        await using var stream = new FileStream(
            path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.ReadWrite | FileShare.Delete,
            bufferSize: 4096,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        using var memory = new MemoryStream();
        await stream.CopyToAsync(memory, cancellationToken);
        return memory.ToArray();
    }

    public async Task<SqliteConnection> OpenAsync(
        CancellationToken cancellationToken,
        SqliteOpenMode mode = SqliteOpenMode.ReadWrite)
    {
        var builder = new SqliteConnectionStringBuilder
        {
            DataSource = FilePath,
            Mode = mode,
            Cache = SqliteCacheMode.Private,
            Pooling = false,
        };
        var connection = new SqliteConnection(builder.ConnectionString);
        await connection.OpenAsync(cancellationToken);
        return connection;
    }

    public void AssertNoSidecars()
    {
        foreach (var path in SidecarPaths())
        {
            Assert.IsFalse(File.Exists(path), $"Unexpected SQLite sidecar: {path}");
        }
    }

    public void DeleteDatabaseFileImmediately()
    {
        File.Delete(FilePath);
        Assert.IsFalse(File.Exists(FilePath));
    }

    public void Dispose()
    {
        foreach (var path in new[] { FilePath }.Concat(SidecarPaths()))
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }

        Directory.Delete(_directoryPath, recursive: true);
    }

    private IEnumerable<string> SidecarPaths()
    {
        yield return $"{FilePath}-wal";
        yield return $"{FilePath}-shm";
        yield return $"{FilePath}-journal";
    }
}

internal static class InterlockedExtensions
{
    public static void Max(ref int location, int value)
    {
        var current = Volatile.Read(ref location);
        while (current < value)
        {
            var observed = Interlocked.CompareExchange(ref location, value, current);
            if (observed == current)
            {
                return;
            }

            current = observed;
        }
    }
}
