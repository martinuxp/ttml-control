using System.Globalization;
using System.Text;
using Dapper;
using Microsoft.Data.Sqlite;
using TtmlControl.Core.Ttml;

namespace TtmlControl.Core.Library;

public enum FileState
{
    Present = 0,
    Missing = 1,
    Invalid = 2,
}

public enum SongStatus
{
    Draft,
    InProgress,
    Complete,
    Uploaded,
}

public sealed class TrackedFile
{
    public long Id { get; init; }
    public string Path { get; init; } = "";
    public long? SongId { get; init; }
    public long? VersionId { get; init; }
    public string? Sha256 { get; init; }
    public long Size { get; init; }
    public long ModifiedTicks { get; init; }
    public FileState State { get; init; }
    public string? ParseError { get; init; }
}

public sealed class SongSummary
{
    public long Id { get; init; }
    public string? Title { get; init; }
    public string? Artists { get; init; }
    public string? Album { get; init; }
    public string? Isrc { get; init; }
    public string? Songwriters { get; init; }
    public string FileNameHint { get; init; } = "";
    public long VersionCount { get; init; }
    public long FileCount { get; init; }
    public string? LastEditedAt { get; init; }
    public string? Timing { get; init; }
    public long? DurationMs { get; init; }
    public long LineCount { get; init; }
    public long InvalidFileCount { get; init; }
    public long MissingFileCount { get; init; }
    public string? StatusName { get; init; }
    public long? StatusVersionId { get; init; }
    public long? CurrentVersionId { get; init; }

    public string DisplayTitle => Title ?? FileNameHint;
    public bool NeedsIdentification => Title is null;
    public SongStatus Status => Enum.TryParse<SongStatus>(StatusName, out var status) ? status : SongStatus.Draft;

    /// <summary>The file changed after the song was marked complete or uploaded.</summary>
    public bool HasChangesSinceStatus =>
        Status is SongStatus.Complete or SongStatus.Uploaded && StatusVersionId != CurrentVersionId;
}

public sealed class SongVersion
{
    public long Id { get; init; }
    public long SongId { get; init; }
    public string Sha256 { get; init; } = "";
    public string LyricsFingerprint { get; init; } = "";
    public string FileModifiedAt { get; init; } = "";
    public string DetectedAt { get; init; } = "";
}

/// <summary>The app's own store. User TTML files are never written to.</summary>
public sealed class LibraryDatabase
{
    private const int SchemaVersion = 2;

    // Trigram tokenizing gives substring search for any script, including Japanese.
    private const string SchemaV2 =
        """
        ALTER TABLE song ADD COLUMN timing TEXT;
        ALTER TABLE song ADD COLUMN duration_ms INTEGER;
        ALTER TABLE song ADD COLUMN line_count INTEGER NOT NULL DEFAULT 0;
        CREATE VIRTUAL TABLE song_search USING fts5(song_id UNINDEXED, lyrics UNINDEXED, folded, tokenize = "trigram");
        """;

    private const string SchemaV1 =
        """
        CREATE TABLE song (
            id INTEGER PRIMARY KEY,
            title TEXT,
            artists TEXT,
            album TEXT,
            isrc TEXT,
            songwriters TEXT,
            file_name_hint TEXT NOT NULL,
            created_at TEXT NOT NULL
        );

        CREATE TABLE version (
            id INTEGER PRIMARY KEY,
            song_id INTEGER NOT NULL REFERENCES song(id),
            sha256 TEXT NOT NULL,
            content BLOB NOT NULL,
            lyrics_fingerprint TEXT NOT NULL,
            file_modified_at TEXT NOT NULL,
            detected_at TEXT NOT NULL,
            UNIQUE (song_id, sha256)
        );
        CREATE INDEX ix_version_sha256 ON version(sha256);
        CREATE INDEX ix_version_fingerprint ON version(lyrics_fingerprint);

        CREATE TABLE ttml_file (
            id INTEGER PRIMARY KEY,
            path TEXT NOT NULL UNIQUE COLLATE NOCASE,
            song_id INTEGER REFERENCES song(id),
            version_id INTEGER REFERENCES version(id),
            size INTEGER NOT NULL,
            modified_ticks INTEGER NOT NULL,
            state INTEGER NOT NULL,
            parse_error TEXT,
            first_seen_at TEXT NOT NULL
        );

        CREATE TABLE status_event (
            id INTEGER PRIMARY KEY,
            song_id INTEGER NOT NULL REFERENCES song(id),
            version_id INTEGER REFERENCES version(id),
            status TEXT NOT NULL,
            created_at TEXT NOT NULL
        );

        CREATE TABLE scan_root (
            id INTEGER PRIMARY KEY,
            path TEXT NOT NULL UNIQUE COLLATE NOCASE
        );
        """;

    private const string FileSelect =
        """
        SELECT f.id, f.path, f.song_id AS SongId, f.version_id AS VersionId, v.sha256 AS Sha256, f.size,
               f.modified_ticks AS ModifiedTicks, f.state, f.parse_error AS ParseError
        FROM ttml_file f LEFT JOIN version v ON v.id = f.version_id
        """;

    private readonly string _connectionString;

    public LibraryDatabase(string databasePath)
    {
        _connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = databasePath,
            ForeignKeys = true,
            Pooling = false,
        }.ToString();

        using var connection = Open();
        var version = connection.ExecuteScalar<long>("PRAGMA user_version");
        if (version > SchemaVersion)
        {
            throw new InvalidOperationException($"La base de datos usa un esquema más nuevo ({version}).");
        }

        connection.Execute("PRAGMA journal_mode = WAL");
        if (version < SchemaVersion)
        {
            using var transaction = connection.BeginTransaction();
            if (version < 1)
            {
                connection.Execute(SchemaV1, transaction: transaction);
            }

            connection.Execute(SchemaV2, transaction: transaction);
            BackfillSongs(connection, transaction);
            connection.Execute($"PRAGMA user_version = {SchemaVersion}", transaction: transaction);
            transaction.Commit();
        }
    }

    private static void BackfillSongs(SqliteConnection connection, SqliteTransaction transaction)
    {
        var latest = connection.Query<(long SongId, byte[] Content)>(
            "SELECT song_id, content FROM version WHERE id IN (SELECT MAX(id) FROM version GROUP BY song_id)",
            transaction: transaction).ToList();
        foreach (var (songId, content) in latest)
        {
            var document = TtmlParser.Parse(new MemoryStream(VersionContent.Decompress(content)));
            ApplyDocument(connection, songId, document, transaction);
        }
    }

    internal static void ApplyDocument(SqliteConnection connection, long songId, TtmlDocument document, SqliteTransaction transaction)
    {
        // A version without a field must not erase what an earlier one provided.
        connection.Execute(
            """
            UPDATE song
            SET title = COALESCE(@title, title),
                artists = COALESCE(@artists, artists),
                album = COALESCE(@album, album),
                isrc = COALESCE(@isrc, isrc),
                songwriters = COALESCE(@songwriters, songwriters),
                timing = @timing,
                duration_ms = @durationMs,
                line_count = @lineCount
            WHERE id = @songId
            """,
            new
            {
                songId,
                title = document.Title,
                artists = JoinOrNull(document.Artists),
                album = document.Album,
                isrc = document.Isrc,
                songwriters = JoinOrNull(document.Songwriters),
                timing = document.Timing.ToString(),
                durationMs = (long?)document.Duration?.TotalMilliseconds,
                lineCount = document.Lines.Count,
            },
            transaction);

        connection.Execute("DELETE FROM song_search WHERE song_id = @songId", new { songId }, transaction);
        var lyrics = string.Join('\n', document.Lines.Select(l => l.Text));
        var metadata = connection.QuerySingle<string>(
            """
            SELECT concat_ws(char(10), title, artists, album, isrc, songwriters, file_name_hint)
            FROM song WHERE id = @songId
            """,
            new { songId },
            transaction);
        connection.Execute(
            "INSERT INTO song_search (song_id, lyrics, folded) VALUES (@songId, @lyrics, @folded)",
            new { songId, lyrics, folded = Fold(metadata + "\n" + lyrics) },
            transaction);
    }

    // Lets "corazon" find "corazón". Applied to both the index and the query.
    private static string Fold(string text)
    {
        var builder = new StringBuilder(text.Length);
        foreach (var c in text.Normalize(NormalizationForm.FormD))
        {
            if (CharUnicodeInfo.GetUnicodeCategory(c) != UnicodeCategory.NonSpacingMark)
            {
                builder.Append(c);
            }
        }

        return builder.ToString().Normalize(NormalizationForm.FormC).ToLowerInvariant();
    }

    private static string? JoinOrNull(IReadOnlyList<string> values) => values.Count == 0 ? null : string.Join(", ", values);

    public void SetStatus(IEnumerable<long> songIds, SongStatus status)
    {
        using var connection = Open();
        using var transaction = connection.BeginTransaction();
        foreach (var songId in songIds)
        {
            connection.Execute(
                $"""
                INSERT INTO status_event (song_id, version_id, status, created_at)
                SELECT @songId, {CurrentVersionSql("@songId")}, @status, @now
                """,
                new
                {
                    songId,
                    status = status.ToString(),
                    now = DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ss.fffZ"),
                },
                transaction);
        }

        transaction.Commit();
    }

    /// <summary>Song ids matching title, artist, album, ISRC, songwriter, file name or lyrics.</summary>
    public IReadOnlySet<long> Search(string query)
    {
        // LIKE wildcards would otherwise defeat a literal search.
        var pattern = "%" + Fold(query.Trim().Replace("%", "").Replace("_", "")) + "%";
        using var connection = Open();
        return connection.Query<long>(
            "SELECT song_id FROM song_search WHERE folded LIKE @pattern",
            new { pattern }).ToHashSet();
    }

    public string GetLyrics(long songId)
    {
        using var connection = Open();
        return connection.QuerySingleOrDefault<string>(
            "SELECT lyrics FROM song_search WHERE song_id = @songId", new { songId }) ?? "";
    }

    public IReadOnlyList<TrackedFile> GetFiles(long songId)
    {
        using var connection = Open();
        return connection.Query<TrackedFile>(FileSelect + " WHERE f.song_id = @songId ORDER BY f.path", new { songId }).ToList();
    }

    private static string CurrentVersionSql(string songId) =>
        $"""
        COALESCE(
            (SELECT f.version_id FROM ttml_file f
             WHERE f.song_id = {songId} AND f.state <> 1 AND f.version_id IS NOT NULL
             ORDER BY f.modified_ticks DESC LIMIT 1),
            (SELECT MAX(v.id) FROM version v WHERE v.song_id = {songId}))
        """;

    internal SqliteConnection Open()
    {
        var connection = new SqliteConnection(_connectionString);
        connection.Open();
        return connection;
    }

    public void AddRoot(string path)
    {
        using var connection = Open();
        connection.Execute(
            "INSERT INTO scan_root (path) VALUES (@path) ON CONFLICT (path) DO NOTHING",
            new { path = System.IO.Path.GetFullPath(path) });
    }

    public IReadOnlyList<string> GetRoots()
    {
        using var connection = Open();
        return connection.Query<string>("SELECT path FROM scan_root ORDER BY path").ToList();
    }

    public IReadOnlyList<SongSummary> GetSongs()
    {
        using var connection = Open();
        return connection.Query<SongSummary>(
            $"""
            SELECT s.id, s.title, s.artists, s.album, s.isrc, s.songwriters, s.file_name_hint AS FileNameHint,
                   (SELECT COUNT(*) FROM version v WHERE v.song_id = s.id) AS VersionCount,
                   (SELECT COUNT(*) FROM ttml_file f WHERE f.song_id = s.id AND f.state <> 1) AS FileCount,
                   (SELECT MAX(v.file_modified_at) FROM version v WHERE v.song_id = s.id) AS LastEditedAt,
                   s.timing, s.duration_ms AS DurationMs, s.line_count AS LineCount,
                   (SELECT COUNT(*) FROM ttml_file f WHERE f.song_id = s.id AND f.state = 2) AS InvalidFileCount,
                   (SELECT COUNT(*) FROM ttml_file f WHERE f.song_id = s.id AND f.state = 1) AS MissingFileCount,
                   (SELECT e.status FROM status_event e WHERE e.song_id = s.id ORDER BY e.id DESC LIMIT 1) AS StatusName,
                   (SELECT e.version_id FROM status_event e WHERE e.song_id = s.id ORDER BY e.id DESC LIMIT 1) AS StatusVersionId,
                   {CurrentVersionSql("s.id")} AS CurrentVersionId
            FROM song s
            ORDER BY COALESCE(s.title, s.file_name_hint) COLLATE NOCASE
            """).ToList();
    }

    public IReadOnlyList<SongVersion> GetVersions(long songId)
    {
        using var connection = Open();
        return connection.Query<SongVersion>(
            """
            SELECT id, song_id AS SongId, sha256 AS Sha256, lyrics_fingerprint AS LyricsFingerprint,
                   file_modified_at AS FileModifiedAt, detected_at AS DetectedAt
            FROM version WHERE song_id = @songId ORDER BY id
            """,
            new { songId }).ToList();
    }

    public byte[] GetVersionContent(long versionId)
    {
        using var connection = Open();
        var compressed = connection.QuerySingle<byte[]>(
            "SELECT content FROM version WHERE id = @versionId", new { versionId });
        return VersionContent.Decompress(compressed);
    }

    public IReadOnlyList<TrackedFile> GetFiles()
    {
        using var connection = Open();
        return connection.Query<TrackedFile>(FileSelect).ToList();
    }

    internal static TrackedFile? FindFile(SqliteConnection connection, string path) =>
        connection.QuerySingleOrDefault<TrackedFile>(FileSelect + " WHERE f.path = @path", new { path });
}
