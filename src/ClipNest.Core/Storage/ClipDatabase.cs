using System;
using System.Globalization;
using System.IO;
using ClipNest.Core.Diagnostics;
using Microsoft.Data.Sqlite;

namespace ClipNest.Core.Storage;

public enum DatabaseOpenStatus
{
    Opened,
    RecoveredFromCorruption,
}

public sealed record DatabaseOpenResult(DatabaseOpenStatus Status, string? BackupPath = null);

/// <summary>
/// thrown when the history database cannot be used.
/// </summary>
public sealed class HistoryStorageException : Exception
{
    public HistoryStorageException(string message, Exception? inner = null)
        : base(message, inner)
    {
    }
}

/// <summary>
/// owns the sqlite file: creating tables, upgrades, and opening connections.
/// </summary>
public sealed class ClipDatabase
{
    private const int SchemaVersion = 1;
    private const int SqliteCorrupt = 11;
    private const int SqliteNotADatabase = 26;

    private readonly string _connectionString;
    private readonly TimeProvider _time;
    private readonly bool _allowFullTextSearch;

    public ClipDatabase(string databasePath, TimeProvider? time = null, bool allowFullTextSearch = true)
    {
        ArgumentException.ThrowIfNullOrEmpty(databasePath);
        DatabasePath = databasePath;
        _time = time ?? TimeProvider.System;
        _allowFullTextSearch = allowFullTextSearch;
        _connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = databasePath,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Cache = SqliteCacheMode.Private,
            Pooling = true,
            DefaultTimeout = 5,
        }.ToString();
    }

    public string DatabasePath { get; }

    /// <summary>
    /// true when the fast search index could be set up.
    /// </summary>
    public bool FullTextSearchAvailable { get; private set; }

    /// <summary>
    /// creates or upgrades the database. a damaged file is moved aside
    /// and a new empty one is created.
    /// </summary>
    public DatabaseOpenResult Initialize()
    {
        var folder = Path.GetDirectoryName(DatabasePath);
        if (!string.IsNullOrEmpty(folder))
        {
            Directory.CreateDirectory(folder);
        }

        try
        {
            CreateOrUpgrade();
            return new DatabaseOpenResult(DatabaseOpenStatus.Opened);
        }
        catch (Exception ex) when (IsCorruption(ex))
        {
            AppLog.Error("history database is damaged, starting a new one", ex);
            var backup = MoveDamagedFiles();
            CreateOrUpgrade();
            return new DatabaseOpenResult(DatabaseOpenStatus.RecoveredFromCorruption, backup);
        }
    }

    public SqliteConnection Open()
    {
        var connection = new SqliteConnection(_connectionString);
        connection.Open();
        using var command = connection.CreateCommand();

        // secure delete overwrites removed rows so deleted text does not linger in the file
        command.CommandText = "PRAGMA busy_timeout = 5000; PRAGMA secure_delete = ON;";
        command.ExecuteNonQuery();
        return connection;
    }

    /// <summary>
    /// closes pooled connections so the file can be moved or the app can exit cleanly.
    /// </summary>
    public static void ReleaseAllConnections() => SqliteConnection.ClearAllPools();

    private void CreateOrUpgrade()
    {
        using var connection = Open();

        // this only works on a brand new file, before any table exists
        Execute(connection, "PRAGMA auto_vacuum = INCREMENTAL;");
        Execute(connection, "PRAGMA journal_mode = WAL;");

        var check = Convert.ToString(Scalar(connection, "PRAGMA quick_check;"), CultureInfo.InvariantCulture);
        if (!string.Equals(check, "ok", StringComparison.OrdinalIgnoreCase))
        {
            throw new SqliteException("database failed its integrity check", SqliteCorrupt);
        }

        var version = Convert.ToInt32(Scalar(connection, "PRAGMA user_version;"), CultureInfo.InvariantCulture);
        if (version > SchemaVersion)
        {
            throw new HistoryStorageException(
                "The history database was created by a newer version of ClipNest.");
        }

        if (version < 1)
        {
            using var transaction = connection.BeginTransaction();
            Execute(connection, """
                CREATE TABLE IF NOT EXISTS entries (
                    id            INTEGER PRIMARY KEY AUTOINCREMENT,
                    kind          INTEGER NOT NULL,
                    text_content  TEXT NULL,
                    search_text   TEXT NOT NULL DEFAULT '',
                    line_count    INTEGER NOT NULL DEFAULT 0,
                    content_hash  TEXT NOT NULL,
                    image_file    TEXT NULL,
                    thumb_file    TEXT NULL,
                    image_width   INTEGER NOT NULL DEFAULT 0,
                    image_height  INTEGER NOT NULL DEFAULT 0,
                    byte_size     INTEGER NOT NULL DEFAULT 0,
                    created_at    INTEGER NOT NULL,
                    last_used_at  INTEGER NOT NULL,
                    is_pinned     INTEGER NOT NULL DEFAULT 0,
                    pin_title     TEXT NULL,
                    pinned_at     INTEGER NULL,
                    source_app    TEXT NULL,
                    code_language TEXT NULL
                );
                CREATE INDEX IF NOT EXISTS ix_entries_recent ON entries(is_pinned, last_used_at DESC);
                CREATE INDEX IF NOT EXISTS ix_entries_hash ON entries(content_hash);
                PRAGMA user_version = 1;
                """, transaction);
            transaction.Commit();
        }

        SetUpSearchIndex(connection);
    }

    private void SetUpSearchIndex(SqliteConnection connection)
    {
        if (!_allowFullTextSearch)
        {
            // used by tests to check the slower search path
            FullTextSearchAvailable = false;
            return;
        }

        try
        {
            // check that this sqlite build supports the fast search index
            Execute(connection, "CREATE VIRTUAL TABLE temp.fts_probe USING fts5(x, tokenize = 'trigram'); DROP TABLE temp.fts_probe;");
        }
        catch (SqliteException ex)
        {
            AppLog.Warn("trigram search is not available, using a slower search: " + ex.SqliteErrorCode);
            FullTextSearchAvailable = false;

            // old triggers would break inserts if the index module is missing
            TryExecute(connection, "DROP TRIGGER IF EXISTS entries_ai; DROP TRIGGER IF EXISTS entries_ad; DROP TRIGGER IF EXISTS entries_au;");
            return;
        }

        var existed = Convert.ToInt64(
            Scalar(connection, "SELECT count(*) FROM sqlite_master WHERE name = 'entries_fts';"),
            CultureInfo.InvariantCulture) > 0;

        Execute(connection, """
            CREATE VIRTUAL TABLE IF NOT EXISTS entries_fts USING fts5(
                search_text, content = 'entries', content_rowid = 'id', tokenize = 'trigram');
            CREATE TRIGGER IF NOT EXISTS entries_ai AFTER INSERT ON entries BEGIN
                INSERT INTO entries_fts(rowid, search_text) VALUES (new.id, new.search_text);
            END;
            CREATE TRIGGER IF NOT EXISTS entries_ad AFTER DELETE ON entries BEGIN
                INSERT INTO entries_fts(entries_fts, rowid, search_text) VALUES ('delete', old.id, old.search_text);
            END;
            CREATE TRIGGER IF NOT EXISTS entries_au AFTER UPDATE OF search_text ON entries BEGIN
                INSERT INTO entries_fts(entries_fts, rowid, search_text) VALUES ('delete', old.id, old.search_text);
                INSERT INTO entries_fts(rowid, search_text) VALUES (new.id, new.search_text);
            END;
            """);

        if (!existed)
        {
            // fill the index for rows that were saved before it existed
            Execute(connection, "INSERT INTO entries_fts(entries_fts) VALUES ('rebuild');");
        }

        FullTextSearchAvailable = true;
    }

    private string? MoveDamagedFiles()
    {
        ReleaseAllConnections();
        var stamp = _time.GetLocalNow().ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture);
        var backup = Path.Combine(
            Path.GetDirectoryName(DatabasePath) ?? ".",
            $"{Path.GetFileNameWithoutExtension(DatabasePath)}.damaged-{stamp}.db");

        foreach (var suffix in new[] { "", "-wal", "-shm" })
        {
            var source = DatabasePath + suffix;
            if (!File.Exists(source))
            {
                continue;
            }

            try
            {
                File.Move(source, backup + suffix, overwrite: true);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                AppLog.Error("could not move damaged database file", ex);
                File.Delete(source);
            }
        }

        return File.Exists(backup) ? backup : null;
    }

    private static bool IsCorruption(Exception ex) =>
        ex is SqliteException sqlite
        && (sqlite.SqliteErrorCode == SqliteCorrupt || sqlite.SqliteErrorCode == SqliteNotADatabase);

    internal static void Execute(SqliteConnection connection, string sql, SqliteTransaction? transaction = null)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = sql;
        command.ExecuteNonQuery();
    }

    private static void TryExecute(SqliteConnection connection, string sql)
    {
        try
        {
            Execute(connection, sql);
        }
        catch (SqliteException ex)
        {
            AppLog.Warn("cleanup statement failed: " + ex.SqliteErrorCode);
        }
    }

    internal static object? Scalar(SqliteConnection connection, string sql)
    {
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        return command.ExecuteScalar();
    }
}
