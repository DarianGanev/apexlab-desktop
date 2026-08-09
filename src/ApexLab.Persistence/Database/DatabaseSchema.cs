namespace ApexLab.Persistence.Database;

public static class DatabaseSchema
{
    public const int CurrentVersion = 1;

    public const int BusyTimeoutMilliseconds = 5_000;

    public const string ApplicationName = "ApexLab";

    public const string InitialMigrationName = "initialize_application_metadata";

    public const string ApplicationMetadataTable = "application_metadata";

    public const string MigrationHistoryTable = "migration_history";

    public const string VersionColumn = "version";

    public const string NameColumn = "name";

    public const string AppliedUtcColumn = "applied_utc";

    public const string KeyColumn = "key";

    public const string ValueColumn = "value";

    // Version 1 defines no explicit indexes. SQLite owns the primary-key autoindexes.

    public static string MigrationHistoryTableDefinition { get; } =
        $"""
        CREATE TABLE {MigrationHistoryTable} (
            {VersionColumn} INTEGER NOT NULL PRIMARY KEY,
            {NameColumn} TEXT NOT NULL,
            {AppliedUtcColumn} TEXT NOT NULL
        )
        """;

    public static string ApplicationMetadataTableDefinition { get; } =
        $"""
        CREATE TABLE {ApplicationMetadataTable} (
            {KeyColumn} TEXT NOT NULL PRIMARY KEY,
            {ValueColumn} TEXT NOT NULL
        )
        """;
}
