using System.Security.Cryptography;
using Dapper;
using Microsoft.Data.Sqlite;
using TtmlControl.Core.Ttml;

namespace TtmlControl.Core.Library;

public sealed record ScanProgress(int FilesProcessed, string CurrentPath);

public sealed class ScanResult
{
    public int Unchanged { get; internal set; }
    public int NewFiles { get; internal set; }
    public int NewSongs { get; internal set; }
    public int NewVersions { get; internal set; }
    public int Moved { get; internal set; }
    public int Missing { get; internal set; }
    public int Invalid { get; internal set; }
    public List<string> UnavailableRoots { get; } = [];
    public List<(string Path, string Error)> Errors { get; } = [];
}

public sealed class LibraryScanner(LibraryDatabase database, TimeProvider? timeProvider = null)
{
    private static readonly EnumerationOptions Enumeration = new()
    {
        RecurseSubdirectories = true,
        IgnoreInaccessible = true,
        MatchCasing = MatchCasing.CaseInsensitive,
        // Reparse points can form loops.
        AttributesToSkip = FileAttributes.Hidden | FileAttributes.System | FileAttributes.ReparsePoint,
    };

    private readonly TimeProvider _time = timeProvider ?? TimeProvider.System;

    public Task<ScanResult> ScanAsync(IProgress<ScanProgress>? progress = null, CancellationToken cancellationToken = default) =>
        Task.Run(() => Scan(progress, cancellationToken), cancellationToken);

    private ScanResult Scan(IProgress<ScanProgress>? progress, CancellationToken cancellationToken)
    {
        var result = new ScanResult();
        var processed = 0;

        foreach (var root in database.GetRoots())
        {
            // A disconnected drive must not turn its whole library into missing files.
            if (!Directory.Exists(root))
            {
                result.UnavailableRoots.Add(root);
                continue;
            }

            foreach (var path in Directory.EnumerateFiles(root, "*.ttml", Enumeration))
            {
                cancellationToken.ThrowIfCancellationRequested();
                progress?.Report(new ScanProgress(++processed, path));
                try
                {
                    ProcessFile(path, result);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    // Nothing was recorded, so the next scan retries this file.
                    result.Errors.Add((path, ex.Message));
                }
            }

            MarkMissing(root, result);
        }

        return result;
    }

    private void ProcessFile(string path, ScanResult result)
    {
        using var connection = database.Open();
        var info = new FileInfo(path);
        var size = info.Length;
        var modified = info.LastWriteTimeUtc;

        var existing = LibraryDatabase.FindFile(connection, path);
        if (existing is { State: not FileState.Missing } && existing.Size == size && existing.ModifiedTicks == modified.Ticks)
        {
            result.Unchanged++;
            return;
        }

        var bytes = File.ReadAllBytes(path);
        var sha256 = Convert.ToHexStringLower(SHA256.HashData(bytes));

        TtmlDocument document;
        try
        {
            document = TtmlParser.Parse(new MemoryStream(bytes));
        }
        catch (TtmlParseException ex)
        {
            RecordInvalid(connection, path, existing, size, modified, ex.Message);
            result.Invalid++;
            return;
        }

        using var transaction = connection.BeginTransaction();
        var now = Timestamp(_time.GetUtcNow());

        var moved = existing is null ? FindMovedFile(connection, sha256, transaction) : null;
        var tracked = existing ?? moved;
        var songId = tracked?.SongId ?? FindSong(connection, sha256, document.LyricsFingerprint, transaction);

        if (songId is null)
        {
            songId = connection.ExecuteScalar<long>(
                """
                INSERT INTO song (file_name_hint, created_at) VALUES (@hint, @now) RETURNING id
                """,
                new { hint = FileNameHints.FromPath(path).CleanName, now },
                transaction);
            result.NewSongs++;
        }

        var versionId = connection.ExecuteScalar<long?>(
            "SELECT id FROM version WHERE song_id = @songId AND sha256 = @sha256",
            new { songId, sha256 },
            transaction);

        if (versionId is null)
        {
            versionId = connection.ExecuteScalar<long>(
                """
                INSERT INTO version (song_id, sha256, content, lyrics_fingerprint, file_modified_at, detected_at)
                VALUES (@songId, @sha256, @content, @fingerprint, @fileModified, @now)
                RETURNING id
                """,
                new
                {
                    songId,
                    sha256,
                    content = VersionContent.Compress(bytes),
                    fingerprint = document.LyricsFingerprint,
                    fileModified = Timestamp(modified),
                    now,
                },
                transaction);
            result.NewVersions++;
        }

        LibraryDatabase.ApplyDocument(connection, songId.Value, document, transaction);

        if (tracked is null)
        {
            connection.Execute(
                """
                INSERT INTO ttml_file (path, song_id, version_id, size, modified_ticks, state, first_seen_at)
                VALUES (@path, @songId, @versionId, @size, @ticks, 0, @now)
                """,
                new { path, songId, versionId, size, ticks = modified.Ticks, now },
                transaction);
            result.NewFiles++;
        }
        else
        {
            connection.Execute(
                """
                UPDATE ttml_file
                SET path = @path, song_id = @songId, version_id = @versionId, size = @size,
                    modified_ticks = @ticks, state = 0, parse_error = NULL
                WHERE id = @id
                """,
                new { path, songId, versionId, size, ticks = modified.Ticks, id = tracked.Id },
                transaction);
            if (moved is not null)
            {
                result.Moved++;
            }
        }

        transaction.Commit();
    }

    private void RecordInvalid(SqliteConnection connection, string path, TrackedFile? existing, long size, DateTime modified, string error)
    {
        // The song and its last good version stay linked, so a broken save loses no history.
        if (existing is null)
        {
            connection.Execute(
                """
                INSERT INTO ttml_file (path, size, modified_ticks, state, parse_error, first_seen_at)
                VALUES (@path, @size, @ticks, 2, @error, @now)
                """,
                new { path, size, ticks = modified.Ticks, error, now = Timestamp(_time.GetUtcNow()) });
        }
        else
        {
            connection.Execute(
                "UPDATE ttml_file SET size = @size, modified_ticks = @ticks, state = 2, parse_error = @error WHERE id = @id",
                new { size, ticks = modified.Ticks, error, id = existing.Id });
        }
    }

    private static TrackedFile? FindMovedFile(SqliteConnection connection, string sha256, SqliteTransaction transaction)
    {
        var candidates = connection.Query<TrackedFile>(
            """
            SELECT f.id, f.path, f.song_id AS SongId, f.version_id AS VersionId
            FROM ttml_file f JOIN version v ON v.id = f.version_id
            WHERE v.sha256 = @sha256
            """,
            new { sha256 },
            transaction);
        return candidates.FirstOrDefault(c => !File.Exists(c.Path));
    }

    private static long? FindSong(SqliteConnection connection, string sha256, string fingerprint, SqliteTransaction transaction) =>
        connection.ExecuteScalar<long?>(
            """
            SELECT song_id FROM version
            WHERE sha256 = @sha256 OR lyrics_fingerprint = @fingerprint
            ORDER BY sha256 = @sha256 DESC, id
            LIMIT 1
            """,
            new { sha256, fingerprint },
            transaction);

    private void MarkMissing(string root, ScanResult result)
    {
        var prefix = Path.TrimEndingDirectorySeparator(root) + Path.DirectorySeparatorChar;
        using var connection = database.Open();
        var tracked = connection.Query<(long Id, string Path)>("SELECT id, path FROM ttml_file WHERE state <> 1");
        foreach (var (id, path) in tracked)
        {
            if (path.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) && !File.Exists(path))
            {
                connection.Execute("UPDATE ttml_file SET state = 1 WHERE id = @id", new { id });
                result.Missing++;
            }
        }
    }

    private static string Timestamp(DateTimeOffset value) => value.UtcDateTime.ToString("yyyy-MM-ddTHH:mm:ss.fffZ");
}
