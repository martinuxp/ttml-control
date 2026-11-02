using Dapper;
using Microsoft.Data.Sqlite;

namespace TtmlControl.Core.Library;

public enum FileState
{
    Present = 0,
    Missing = 1,
    Invalid = 2,
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

    public string DisplayTitle => Title ?? FileNameHint;
    public bool NeedsIdentification => Title is null;
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
    private const int SchemaVersion = 1;

    private const string Schema =
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
        if (version == 0)
        {
            using var transaction = connection.BeginTransaction();
            connection.Execute(Schema, transaction: transaction);
            connection.Execute($"PRAGMA user_version = {SchemaVersion}", transaction: transaction);
            transaction.Commit();
        }
    }

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
            """
            SELECT s.id, s.title, s.artists, s.album, s.isrc, s.songwriters, s.file_name_hint AS FileNameHint,
                   (SELECT COUNT(*) FROM version v WHERE v.song_id = s.id) AS VersionCount,
                   (SELECT COUNT(*) FROM ttml_file f WHERE f.song_id = s.id AND f.state <> 1) AS FileCount,
                   (SELECT MAX(v.file_modified_at) FROM version v WHERE v.song_id = s.id) AS LastEditedAt
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
