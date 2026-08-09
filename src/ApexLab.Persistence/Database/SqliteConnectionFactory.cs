using ApexLab.Application.Storage;
using Microsoft.Data.Sqlite;

namespace ApexLab.Persistence.Database;

public sealed class SqliteConnectionFactory
{
    public SqliteConnectionFactory(string databaseFilePath)
    {
        if (string.IsNullOrWhiteSpace(databaseFilePath))
        {
            throw new ArgumentException("A database file path is required.", nameof(databaseFilePath));
        }

        if (!Path.IsPathFullyQualified(databaseFilePath))
        {
            throw new ArgumentException(
                "The database file path must be absolute.",
                nameof(databaseFilePath));
        }

        var fileName = Path.GetFileName(databaseFilePath);
        var directoryPath = Path.GetDirectoryName(databaseFilePath);
        if (string.IsNullOrEmpty(fileName) || string.IsNullOrEmpty(directoryPath))
        {
            throw new ArgumentException(
                "The database file path must identify a file below a data directory.",
                nameof(databaseFilePath));
        }

        if (OperatingSystem.IsWindows() && !WindowsPathSegment.IsSafe(fileName))
        {
            throw new ArgumentException(
                "The database file name is not safe on Windows.",
                nameof(databaseFilePath));
        }

        var paths = ApplicationPaths.FromRoot(directoryPath);
        DatabaseFilePath = Path.Combine(paths.RootDirectory, fileName);
    }

    public string DatabaseFilePath { get; }

    public SqliteConnection CreateWritableConnection() =>
        CreateConnection(SqliteOpenMode.ReadWriteCreate);

    public SqliteConnection CreateReadOnlyInspectionConnection() =>
        CreateConnection(SqliteOpenMode.ReadOnly);

    private SqliteConnection CreateConnection(SqliteOpenMode mode)
    {
        var builder = new SqliteConnectionStringBuilder
        {
            DataSource = DatabaseFilePath,
            Mode = mode,
            Cache = SqliteCacheMode.Private,
            Pooling = false,
        };

        return new SqliteConnection(builder.ConnectionString);
    }
}
