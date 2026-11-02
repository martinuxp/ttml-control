using System.Text;
using Microsoft.Data.Sqlite;
using TtmlControl.Core.Library;

namespace TtmlControl.Tests;

public sealed class LibraryStatusAndSearchTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("ttml-tests-").FullName;
    private readonly string _root;
    private readonly LibraryDatabase _db;
    private readonly LibraryScanner _scanner;

    public LibraryStatusAndSearchTests()
    {
        _root = Directory.CreateDirectory(Path.Combine(_dir, "root")).FullName;
        _db = new LibraryDatabase(Path.Combine(_dir, "library.db"));
        _db.AddRoot(_root);
        _scanner = new LibraryScanner(_db);
    }

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    private static string Ttml(string lyrics) =>
        $"""<tt><body><div><p begin="1.000" end="9.000"><span begin="1.000" end="9.000">{lyrics}</span></p></div></body></tt>""";

    private async Task WriteAndScan(string lyrics, int minutes = 0)
    {
        var path = Path.Combine(_root, "archivo.ttml");
        File.WriteAllText(path, Ttml(lyrics), Encoding.UTF8);
        File.SetLastWriteTimeUtc(path, new DateTime(2026, 1, 1, 0, minutes, 0, DateTimeKind.Utc));
        await _scanner.ScanAsync();
    }

    [Fact]
    public async Task New_song_is_a_draft_without_pending_changes()
    {
        await WriteAndScan("hello");

        var song = Assert.Single(_db.GetSongs());
        Assert.Equal(SongStatus.Draft, song.Status);
        Assert.False(song.HasChangesSinceStatus);
    }

    [Theory]
    [InlineData(SongStatus.Uploaded)]
    [InlineData(SongStatus.Complete)]
    public async Task Editing_after_marking_flags_changes_and_reverting_clears_them(SongStatus status)
    {
        await WriteAndScan("hello");
        _db.SetStatus([Assert.Single(_db.GetSongs()).Id], status);
        Assert.False(Assert.Single(_db.GetSongs()).HasChangesSinceStatus);

        await WriteAndScan("hello again", minutes: 1);
        var changed = Assert.Single(_db.GetSongs());
        Assert.Equal(status, changed.Status);
        Assert.True(changed.HasChangesSinceStatus);

        await WriteAndScan("hello", minutes: 2);
        Assert.False(Assert.Single(_db.GetSongs()).HasChangesSinceStatus);
    }

    [Fact]
    public async Task In_progress_never_flags_changes()
    {
        await WriteAndScan("hello");
        _db.SetStatus([Assert.Single(_db.GetSongs()).Id], SongStatus.InProgress);

        await WriteAndScan("edited", minutes: 1);

        Assert.False(Assert.Single(_db.GetSongs()).HasChangesSinceStatus);
    }

    [Theory]
    [InlineData("MUNDO")]
    [InlineData("corazon")]
    [InlineData("フリー")]
    [InlineData("ARCHIVO")]
    [InlineData("la")]
    public async Task Search_finds_lyrics_and_file_names(string query)
    {
        await WriteAndScan("Hola mundo corazón フリーク");

        Assert.Single(_db.Search(query));
    }

    [Theory]
    [InlineData("zzzzz")]
    [InlineData("100%")]
    public async Task Search_without_a_match_is_empty(string query)
    {
        await WriteAndScan("hello");

        Assert.Empty(_db.Search(query));
    }

    [Fact]
    public async Task Search_uses_the_latest_lyrics()
    {
        await WriteAndScan("oldword");
        await WriteAndScan("newword", minutes: 1);

        Assert.Empty(_db.Search("oldword"));
        Assert.Single(_db.Search("newword"));
    }

    [Fact]
    public void Upgrades_a_v1_database_and_backfills_it()
    {
        var path = Path.Combine(_dir, "old.db");
        var content = typeof(LibraryDatabase).Assembly
            .GetType("TtmlControl.Core.Library.VersionContent")!
            .GetMethod("Compress")!
            .Invoke(null, [Encoding.UTF8.GetBytes(Ttml("legacy lyrics"))]);
        var v1 = typeof(LibraryDatabase)
            .GetField("SchemaV1", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static)!
            .GetRawConstantValue();

        using (var connection = new SqliteConnection($"Data Source={path};Pooling=False"))
        {
            connection.Open();
            using var command = connection.CreateCommand();
            command.CommandText =
                $"""
                {v1}
                INSERT INTO song (id, file_name_hint, created_at) VALUES (1, 'old', 'x');
                INSERT INTO version (song_id, sha256, content, lyrics_fingerprint, file_modified_at, detected_at)
                VALUES (1, 'h', $content, 'f', 'x', 'x');
                INSERT INTO ttml_file (path, song_id, version_id, size, modified_ticks, state, first_seen_at)
                VALUES ('C:\old.ttml', 1, 1, 1, 1, 0, 'x');
                PRAGMA user_version = 1;
                """;
            command.Parameters.AddWithValue("$content", content);
            command.ExecuteNonQuery();
        }

        var db = new LibraryDatabase(path);

        Assert.Single(db.Search("legacy"));
        Assert.Equal(1, Assert.Single(db.GetSongs()).LineCount);
    }
}
