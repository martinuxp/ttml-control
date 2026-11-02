using System.Text;
using TtmlControl.Core.Library;

namespace TtmlControl.Tests;

public sealed class LibraryFoldersAndIdentityTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("ttml-tests-").FullName;
    private readonly string _root;
    private readonly LibraryDatabase _db;
    private readonly LibraryScanner _scanner;

    public LibraryFoldersAndIdentityTests()
    {
        _root = Directory.CreateDirectory(Path.Combine(_dir, "root")).FullName;
        _db = new LibraryDatabase(Path.Combine(_dir, "library.db"));
        _db.AddRoot(_root);
        _scanner = new LibraryScanner(_db);
    }

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    private static string Ttml(string lyrics, string? title = null) =>
        $"""<tt xmlns:amll="http://www.example.com/ns/amll"><head><metadata>{(title is null ? "" : $"<amll:meta key=\"musicName\" value=\"{title}\" />")}</metadata></head><body><div><p begin="1.000" end="9.000"><span begin="1.000" end="9.000">{lyrics}</span></p></div></body></tt>""";

    private string WriteFile(string name, string content, int minutes = 0)
    {
        var path = Path.Combine(_root, name);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, content, Encoding.UTF8);
        File.SetLastWriteTimeUtc(path, new DateTime(2026, 1, 1, 0, minutes, 0, DateTimeKind.Utc));
        return path;
    }

    [Fact]
    public async Task User_identity_survives_scans_and_new_file_metadata()
    {
        WriteFile("a.ttml", Ttml("hello"));
        await _scanner.ScanAsync();
        var id = Assert.Single(_db.GetSongs()).Id;

        _db.SetIdentity(id, " Compass ", "Mili");
        WriteFile("a.ttml", Ttml("hello there", title: "From file"), minutes: 1);
        await _scanner.ScanAsync();

        var song = Assert.Single(_db.GetSongs());
        Assert.Equal("Compass", song.Title);
        Assert.Equal("Mili", song.Artists);
        Assert.False(song.NeedsIdentification);
    }

    [Fact]
    public async Task Clearing_identity_returns_to_file_metadata()
    {
        WriteFile("a.ttml", Ttml("hello", title: "From file"));
        await _scanner.ScanAsync();
        var id = Assert.Single(_db.GetSongs()).Id;

        _db.SetIdentity(id, "Mine", "Me");
        _db.SetIdentity(id, "  ", null);

        var song = Assert.Single(_db.GetSongs());
        Assert.Equal("From file", song.Title);
        Assert.Null(song.Artists);
    }

    [Fact]
    public async Task Edited_identity_is_searchable_and_keeps_lyrics()
    {
        WriteFile("a.ttml", Ttml("hello"));
        await _scanner.ScanAsync();
        var id = Assert.Single(_db.GetSongs()).Id;

        _db.SetIdentity(id, "Canción", "Mili");

        Assert.Single(_db.Search("cancion"));
        Assert.Single(_db.Search("hello"));
        Assert.Equal("hello", _db.GetLyrics(id));
    }

    [Fact]
    public async Task Excluded_folder_is_skipped_and_hides_tracked_songs_without_losing_them()
    {
        WriteFile("keep/a.ttml", Ttml("hello"));
        WriteFile("old/b.ttml", Ttml("goodbye"));
        await _scanner.ScanAsync();
        var old = Path.Combine(_root, "old");

        _db.AddExclusion(old);
        WriteFile("old/c.ttml", Ttml("never indexed"));
        var excludedScan = await _scanner.ScanAsync();

        Assert.Equal(0, excludedScan.NewFiles);
        Assert.Equal(0, excludedScan.Missing);
        Assert.Single(_db.GetSongs());

        _db.RemoveExclusion(old);
        var restoredScan = await _scanner.ScanAsync();

        Assert.Equal(1, restoredScan.NewSongs);
        Assert.Equal(3, _db.GetSongs().Count);
        Assert.All(_db.GetFiles(), f => Assert.Equal(FileState.Present, f.State));
    }

    [Fact]
    public async Task Removing_a_root_hides_songs_and_adding_it_back_restores_history_and_status()
    {
        WriteFile("a.ttml", Ttml("hello"));
        await _scanner.ScanAsync();
        var id = Assert.Single(_db.GetSongs()).Id;
        _db.SetStatus([id], SongStatus.Uploaded);

        _db.RemoveRoot(_root);
        Assert.Empty(_db.GetSongs());
        Assert.Empty(await Task.FromResult(_db.GetRoots()));

        _db.AddRoot(_root);
        await _scanner.ScanAsync();

        var song = Assert.Single(_db.GetSongs());
        Assert.Equal(id, song.Id);
        Assert.Equal(SongStatus.Uploaded, song.Status);
        Assert.False(song.HasChangesSinceStatus);
        Assert.Equal(FileState.Present, Assert.Single(_db.GetFiles()).State);
    }

    [Fact]
    public async Task Nested_root_keeps_files_visible_when_the_parent_is_removed()
    {
        WriteFile("sub/a.ttml", Ttml("hello"));
        _db.AddRoot(Path.Combine(_root, "sub"));
        await _scanner.ScanAsync();

        _db.RemoveRoot(_root);

        Assert.Single(_db.GetSongs());
    }

    [Theory]
    [InlineData(@"D:\music\a.ttml", @"D:\", true)]
    [InlineData(@"D:\music\a.ttml", @"d:\MUSIC", true)]
    [InlineData(@"D:\music\a.ttml", @"D:\music\", true)]
    [InlineData(@"D:\musicians\a.ttml", @"D:\music", false)]
    [InlineData(@"E:\music\a.ttml", @"D:\", false)]
    public void IsUnder_compares_whole_folder_names(string path, string folder, bool expected)
    {
        Assert.Equal(expected, LibraryPaths.IsUnder(path, folder));
    }
}
