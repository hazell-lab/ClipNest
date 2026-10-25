using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using ClipNest.Core.Capture;
using ClipNest.Core.Models;
using ClipNest.Core.Text;
using Microsoft.Data.Sqlite;

namespace ClipNest.Core.Storage;

public sealed record EntryQuery(bool Pinned, ClipKind? Kind, string? Search, int Offset, int Limit);

public sealed record NewEntry
{
    public required ClipKind Kind { get; init; }
    public string? Text { get; init; }
    public required string ContentHash { get; init; }
    public string? ImageFile { get; init; }
    public string? ThumbnailFile { get; init; }
    public int ImageWidth { get; init; }
    public int ImageHeight { get; init; }
    public long ByteSize { get; init; }
    public required DateTimeOffset CreatedAt { get; init; }
    public string? SourceApp { get; init; }
}

public sealed record RetentionLimits(int MaxUnpinnedItems, long MaxStorageBytes, int RetentionDays);

public sealed record DeletionResult(int DeletedCount, IReadOnlyList<string> ImageFiles)
{
    public static DeletionResult None { get; } = new(0, Array.Empty<string>());
}

public sealed record EntryCounts(int Total, int Pinned, long ApproximateBytes);

/// <summary>
/// all sql for clipboard entries. methods are synchronous and are
/// meant to be called from a background thread.
/// </summary>
public sealed class ClipRepository
{
    private const string SelectColumns = """
        id, kind, substr(text_content, 1, 1000) AS preview, length(text_content) AS text_length,
        line_count, content_hash, image_file, thumb_file, image_width, image_height, byte_size,
        created_at, last_used_at, is_pinned, pin_title, pinned_at, source_app, code_language
        """;

    private readonly ClipDatabase _database;

    public ClipRepository(ClipDatabase database)
    {
        _database = database ?? throw new ArgumentNullException(nameof(database));
    }

    public ClipEntry? GetById(long id)
    {
        using var connection = _database.Open();
        using var command = connection.CreateCommand();
        command.CommandText = $"SELECT {SelectColumns} FROM entries WHERE id = $id;";
        command.Parameters.AddWithValue("$id", id);
        return ReadSingle(command);
    }

    /// <summary>
    /// the entry that was captured or copied most recently.
    /// </summary>
    public ClipEntry? GetMostRecent()
    {
        using var connection = _database.Open();
        using var command = connection.CreateCommand();
        command.CommandText = $"SELECT {SelectColumns} FROM entries ORDER BY last_used_at DESC, id DESC LIMIT 1;";
        return ReadSingle(command);
    }

    public ClipEntry? FindByHash(string contentHash)
    {
        using var connection = _database.Open();
        using var command = connection.CreateCommand();
        command.CommandText = $"SELECT {SelectColumns} FROM entries WHERE content_hash = $hash ORDER BY last_used_at DESC, id DESC LIMIT 1;";
        command.Parameters.AddWithValue("$hash", contentHash);
        return ReadSingle(command);
    }

    public string? GetText(long id)
    {
        using var connection = _database.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT text_content FROM entries WHERE id = $id;";
        command.Parameters.AddWithValue("$id", id);
        return command.ExecuteScalar() as string;
    }

    public long Insert(NewEntry entry)
    {
        ArgumentNullException.ThrowIfNull(entry);
        var when = entry.CreatedAt.ToUnixTimeMilliseconds();

        using var connection = _database.Open();
        using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO entries (kind, text_content, search_text, line_count, content_hash, image_file, thumb_file,
                                 image_width, image_height, byte_size, created_at, last_used_at, is_pinned, source_app)
            VALUES ($kind, $text, $search, $lines, $hash, $image, $thumb, $width, $height, $size, $when, $when, 0, $source);
            SELECT last_insert_rowid();
            """;
        command.Parameters.AddWithValue("$kind", (int)entry.Kind);
        command.Parameters.AddWithValue("$text", (object?)entry.Text ?? DBNull.Value);
        command.Parameters.AddWithValue("$search", SearchText.BuildIndexText(null, entry.Text, CaptureLimits.SearchIndexChars));
        command.Parameters.AddWithValue("$lines", TextStats.CountLines(entry.Text));
        command.Parameters.AddWithValue("$hash", entry.ContentHash);
        command.Parameters.AddWithValue("$image", (object?)entry.ImageFile ?? DBNull.Value);
        command.Parameters.AddWithValue("$thumb", (object?)entry.ThumbnailFile ?? DBNull.Value);
        command.Parameters.AddWithValue("$width", entry.ImageWidth);
        command.Parameters.AddWithValue("$height", entry.ImageHeight);
        command.Parameters.AddWithValue("$size", entry.ByteSize);
        command.Parameters.AddWithValue("$when", when);
        command.Parameters.AddWithValue("$source", (object?)entry.SourceApp ?? DBNull.Value);
        return Convert.ToInt64(command.ExecuteScalar(), CultureInfo.InvariantCulture);
    }

    /// <summary>
    /// moves an entry to the top of the recent list.
    /// </summary>
    public bool Touch(long id, DateTimeOffset when)
    {
        using var connection = _database.Open();
        using var command = connection.CreateCommand();
        // always end up strictly newest, even if two events share the same millisecond
        command.CommandText = """
            UPDATE entries
            SET last_used_at = MAX($when, (SELECT COALESCE(MAX(last_used_at), 0) + 1 FROM entries WHERE id <> $id))
            WHERE id = $id;
            """;
        command.Parameters.AddWithValue("$when", when.ToUnixTimeMilliseconds());
        command.Parameters.AddWithValue("$id", id);
        return command.ExecuteNonQuery() > 0;
    }

    public IReadOnlyList<ClipEntry> Query(EntryQuery query)
    {
        ArgumentNullException.ThrowIfNull(query);
        var search = SearchText.NormalizeQuery(query.Search);

        var sql = new StringBuilder($"SELECT {SelectColumns} FROM entries WHERE is_pinned = $pinned");
        using var connection = _database.Open();
        using var command = connection.CreateCommand();
        command.Parameters.AddWithValue("$pinned", query.Pinned ? 1 : 0);

        if (query.Kind is { } kind)
        {
            sql.Append(" AND kind = $kind");
            command.Parameters.AddWithValue("$kind", (int)kind);
        }

        if (search is not null)
        {
            // the fast search index needs at least three characters
            if (_database.FullTextSearchAvailable && SearchText.CountCharacters(search) >= 3)
            {
                sql.Append(" AND id IN (SELECT rowid FROM entries_fts WHERE entries_fts MATCH $match)");
                command.Parameters.AddWithValue("$match", "\"" + search.Replace("\"", "\"\"", StringComparison.Ordinal) + "\"");
            }
            else
            {
                sql.Append(" AND instr(search_text, $search) > 0");
                command.Parameters.AddWithValue("$search", search);
            }
        }

        sql.Append(query.Pinned
            ? " ORDER BY pinned_at DESC, id DESC"
            : " ORDER BY last_used_at DESC, id DESC");
        sql.Append(" LIMIT $limit OFFSET $offset;");
        command.Parameters.AddWithValue("$limit", Math.Max(1, query.Limit));
        command.Parameters.AddWithValue("$offset", Math.Max(0, query.Offset));
        command.CommandText = sql.ToString();

        var results = new List<ClipEntry>();
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            results.Add(ReadEntry(reader));
        }

        return results;
    }

    public bool SetPinned(long id, bool pinned, DateTimeOffset when)
    {
        using var connection = _database.Open();
        using var transaction = connection.BeginTransaction();
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = pinned
            ? "UPDATE entries SET is_pinned = 1, pinned_at = $when WHERE id = $id AND is_pinned = 0;"
            : "UPDATE entries SET is_pinned = 0, pinned_at = NULL, pin_title = NULL, last_used_at = $when WHERE id = $id AND is_pinned = 1;";
        command.Parameters.AddWithValue("$when", when.ToUnixTimeMilliseconds());
        command.Parameters.AddWithValue("$id", id);
        var changed = command.ExecuteNonQuery() > 0;
        if (changed && !pinned)
        {
            // the title was removed, so the search text must be rebuilt
            UpdateSearchText(connection, transaction, id, null);
        }

        transaction.Commit();
        return changed;
    }

    public bool SetPinTitle(long id, string? title)
    {
        title = string.IsNullOrWhiteSpace(title) ? null : title.Trim();
        if (title is { Length: > 200 })
        {
            title = title[..200];
        }

        using var connection = _database.Open();
        using var transaction = connection.BeginTransaction();
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "UPDATE entries SET pin_title = $title WHERE id = $id AND is_pinned = 1;";
        command.Parameters.AddWithValue("$title", (object?)title ?? DBNull.Value);
        command.Parameters.AddWithValue("$id", id);
        var changed = command.ExecuteNonQuery() > 0;
        if (changed)
        {
            UpdateSearchText(connection, transaction, id, title);
        }

        transaction.Commit();
        return changed;
    }

    public bool SetCodeLanguage(long id, string? languageId)
    {
        using var connection = _database.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "UPDATE entries SET code_language = $lang WHERE id = $id;";
        command.Parameters.AddWithValue("$lang", (object?)languageId ?? DBNull.Value);
        command.Parameters.AddWithValue("$id", id);
        return command.ExecuteNonQuery() > 0;
    }

    public DeletionResult Delete(long id)
    {
        using var connection = _database.Open();
        using var transaction = connection.BeginTransaction();
        var victims = SelectVictims(connection, transaction, "SELECT id, image_file, thumb_file, byte_size FROM entries WHERE id = $id;", ("$id", id));
        DeleteRows(connection, transaction, victims);
        transaction.Commit();
        return ToResult(victims);
    }

    public DeletionResult DeleteAllUnpinned()
    {
        using var connection = _database.Open();
        using var transaction = connection.BeginTransaction();
        var victims = SelectVictims(connection, transaction, "SELECT id, image_file, thumb_file, byte_size FROM entries WHERE is_pinned = 0;");
        DeleteRows(connection, transaction, victims);
        transaction.Commit();
        return ToResult(victims);
    }

    public DeletionResult DeleteEverything()
    {
        using var connection = _database.Open();
        using var transaction = connection.BeginTransaction();
        var victims = SelectVictims(connection, transaction, "SELECT id, image_file, thumb_file, byte_size FROM entries;");
        DeleteRows(connection, transaction, victims);
        transaction.Commit();
        return ToResult(victims);
    }

    /// <summary>
    /// removes old unpinned entries by age, count, and total size.
    /// pinned entries are never removed here.
    /// </summary>
    public DeletionResult ApplyRetention(RetentionLimits limits, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(limits);
        using var connection = _database.Open();
        using var transaction = connection.BeginTransaction();

        var victims = new List<Victim>();
        var chosen = new HashSet<long>();

        void AddAll(IEnumerable<Victim> items)
        {
            foreach (var item in items)
            {
                if (chosen.Add(item.Id))
                {
                    victims.Add(item);
                }
            }
        }

        // 1. too old
        if (limits.RetentionDays > 0)
        {
            var cutoff = (now - TimeSpan.FromDays(limits.RetentionDays)).ToUnixTimeMilliseconds();
            AddAll(SelectVictims(connection, transaction,
                "SELECT id, image_file, thumb_file, byte_size FROM entries WHERE is_pinned = 0 AND last_used_at < $cutoff;",
                ("$cutoff", cutoff)));
        }

        // 2. too many, keep only the newest ones
        if (limits.MaxUnpinnedItems >= 0)
        {
            AddAll(SelectVictims(connection, transaction,
                "SELECT id, image_file, thumb_file, byte_size FROM entries WHERE is_pinned = 0 ORDER BY last_used_at DESC, id DESC LIMIT -1 OFFSET $max;",
                ("$max", limits.MaxUnpinnedItems)));
        }

        // 3. too big, remove the oldest until the total fits
        if (limits.MaxStorageBytes > 0)
        {
            using var sumCommand = connection.CreateCommand();
            sumCommand.Transaction = transaction;
            sumCommand.CommandText = "SELECT COALESCE(SUM(byte_size), 0) FROM entries;";
            var total = Convert.ToInt64(sumCommand.ExecuteScalar(), CultureInfo.InvariantCulture);
            foreach (var victim in victims)
            {
                total -= victim.ByteSize;
            }

            if (total > limits.MaxStorageBytes)
            {
                var oldestFirst = SelectVictims(connection, transaction,
                    "SELECT id, image_file, thumb_file, byte_size FROM entries WHERE is_pinned = 0 ORDER BY last_used_at ASC, id ASC;");
                foreach (var candidate in oldestFirst)
                {
                    if (total <= limits.MaxStorageBytes)
                    {
                        break;
                    }

                    if (chosen.Add(candidate.Id))
                    {
                        victims.Add(candidate);
                        total -= candidate.ByteSize;
                    }
                }
            }
        }

        DeleteRows(connection, transaction, victims);
        transaction.Commit();
        return ToResult(victims);
    }

    /// <summary>
    /// every image or thumbnail file name that an entry still uses.
    /// </summary>
    public HashSet<string> GetReferencedFiles()
    {
        var files = new HashSet<string>(StringComparer.Ordinal);
        using var connection = _database.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT image_file, thumb_file FROM entries WHERE kind = 1;";
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            if (!reader.IsDBNull(0))
            {
                files.Add(reader.GetString(0));
            }

            if (!reader.IsDBNull(1))
            {
                files.Add(reader.GetString(1));
            }
        }

        return files;
    }

    public EntryCounts GetCounts()
    {
        using var connection = _database.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT count(*), COALESCE(SUM(is_pinned), 0), COALESCE(SUM(byte_size), 0) FROM entries;";
        using var reader = command.ExecuteReader();
        reader.Read();
        return new EntryCounts(reader.GetInt32(0), reader.GetInt32(1), reader.GetInt64(2));
    }

    /// <summary>
    /// gives free pages back to the file system after deletes.
    /// </summary>
    public void ReleaseFreeSpace()
    {
        using var connection = _database.Open();
        ClipDatabase.Execute(connection, "PRAGMA incremental_vacuum; PRAGMA wal_checkpoint(TRUNCATE);");
    }

    private static void UpdateSearchText(SqliteConnection connection, SqliteTransaction transaction, long id, string? title)
    {
        using var read = connection.CreateCommand();
        read.Transaction = transaction;
        read.CommandText = "SELECT substr(text_content, 1, $chars) FROM entries WHERE id = $id;";
        read.Parameters.AddWithValue("$chars", CaptureLimits.SearchIndexChars);
        read.Parameters.AddWithValue("$id", id);
        var text = read.ExecuteScalar() as string;

        using var write = connection.CreateCommand();
        write.Transaction = transaction;
        write.CommandText = "UPDATE entries SET search_text = $search WHERE id = $id;";
        write.Parameters.AddWithValue("$search", SearchText.BuildIndexText(title, text, CaptureLimits.SearchIndexChars));
        write.Parameters.AddWithValue("$id", id);
        write.ExecuteNonQuery();
    }

    private readonly record struct Victim(long Id, string? ImageFile, string? ThumbnailFile, long ByteSize);

    private static List<Victim> SelectVictims(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string sql,
        params (string Name, object Value)[] parameters)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = sql;
        foreach (var (name, value) in parameters)
        {
            command.Parameters.AddWithValue(name, value);
        }

        var list = new List<Victim>();
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            list.Add(new Victim(
                reader.GetInt64(0),
                reader.IsDBNull(1) ? null : reader.GetString(1),
                reader.IsDBNull(2) ? null : reader.GetString(2),
                reader.GetInt64(3)));
        }

        return list;
    }

    private static void DeleteRows(SqliteConnection connection, SqliteTransaction transaction, IReadOnlyList<Victim> victims)
    {
        if (victims.Count == 0)
        {
            return;
        }

        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "DELETE FROM entries WHERE id = $id;";
        var parameter = command.Parameters.Add("$id", SqliteType.Integer);
        foreach (var victim in victims)
        {
            parameter.Value = victim.Id;
            command.ExecuteNonQuery();
        }
    }

    private static DeletionResult ToResult(IReadOnlyList<Victim> victims)
    {
        var files = new List<string>();
        foreach (var victim in victims)
        {
            if (victim.ImageFile is not null)
            {
                files.Add(victim.ImageFile);
            }

            if (victim.ThumbnailFile is not null)
            {
                files.Add(victim.ThumbnailFile);
            }
        }

        return new DeletionResult(victims.Count, files);
    }

    private static ClipEntry? ReadSingle(SqliteCommand command)
    {
        using var reader = command.ExecuteReader();
        return reader.Read() ? ReadEntry(reader) : null;
    }

    private static ClipEntry ReadEntry(SqliteDataReader reader) => new()
    {
        Id = reader.GetInt64(0),
        Kind = (ClipKind)reader.GetInt32(1),
        TextPreview = reader.IsDBNull(2) ? null : reader.GetString(2),
        TextLength = reader.IsDBNull(3) ? 0 : reader.GetInt32(3),
        LineCount = reader.GetInt32(4),
        ContentHash = reader.GetString(5),
        ImageFile = reader.IsDBNull(6) ? null : reader.GetString(6),
        ThumbnailFile = reader.IsDBNull(7) ? null : reader.GetString(7),
        ImageWidth = reader.GetInt32(8),
        ImageHeight = reader.GetInt32(9),
        ByteSize = reader.GetInt64(10),
        CreatedAt = DateTimeOffset.FromUnixTimeMilliseconds(reader.GetInt64(11)),
        LastUsedAt = DateTimeOffset.FromUnixTimeMilliseconds(reader.GetInt64(12)),
        IsPinned = reader.GetInt32(13) != 0,
        PinTitle = reader.IsDBNull(14) ? null : reader.GetString(14),
        PinnedAt = reader.IsDBNull(15) ? null : DateTimeOffset.FromUnixTimeMilliseconds(reader.GetInt64(15)),
        SourceApp = reader.IsDBNull(16) ? null : reader.GetString(16),
        CodeLanguage = reader.IsDBNull(17) ? null : reader.GetString(17),
    };
}
