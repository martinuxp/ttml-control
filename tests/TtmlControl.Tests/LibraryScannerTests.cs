using System.Text;
using TtmlControl.Core.Library;

namespace TtmlControl.Tests;

public sealed class LibraryScannerTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("ttml-tests-").FullName;
    private readonly string _root;
    private readonly LibraryDatabase _db;
    private readonly LibraryScanner _scanner;

    public LibraryScannerTests()
    {
        _root = Directory.CreateDirectory(Path.Combine(_dir, "root")).FullName;
        _db = new LibraryDatabase(Path.Combine(_dir, "library.db"));
        _db.AddRoot(_root);
        _scanner = new LibraryScanner(_db);
    }

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    private static string Ttml(string lyrics, string? title = "Song", string begin = "1.000") =>
        $"""
        <tt xmlns:amll="http://www.example.com/ns/amll"><head><metadata>{(title is null ? "" : $"<amll:meta key=\"musicName\" value=\"{title}\" />")}</metadata></head><body><div><p begin="{begin}" end="9.000"><span begin="{begin}" end="9.000">{lyrics}</span></p></div></body></tt>
        """;

    // Timestamps can repeat within a test; a distinct one guarantees the change is seen.
    private string WriteFile(string name, string content, int minutes = 0)
    {
        var path = Path.Combine(_root, name);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, content, Encoding.UTF8);
        File.SetLastWriteTimeUtc(path, new DateTime(2026, 1, 1, 0, minutes, 0, DateTimeKind.Utc));
        return path;
    }

    private Task<ScanResult> Scan() => _scanner.ScanAsync();

    [Fact]
    public async Task First_scan_stores_song_and_recoverable_version()
    {
        var content = Ttml("hello");
        WriteFile("sub/a.TTML", content);

        var result = await Scan();

        Assert.Equal((1, 1, 1), (result.NewFiles, result.NewSongs, result.NewVersions));
        var song = Assert.Single(_db.GetSongs());
        Assert.Equal("Song", song.DisplayTitle);
        var version = Assert.Single(_db.GetVersions(song.Id));
        Assert.Equal("2026-01-01T00:00:00.000Z", version.FileModifiedAt);
        Assert.Equal(content, Encoding.UTF8.GetString(_db.GetVersionContent(version.Id)).TrimStart('﻿'));
    }

    [Fact]
    public async Task Second_scan_of_untouched_files_changes_nothing()
    {
        WriteFile("a.ttml", Ttml("hello"));
        await Scan();

        var result = await Scan();

        Assert.Equal(1, result.Unchanged);
        Assert.Equal(0, result.NewFiles + result.NewVersions + result.NewSongs);
    }

    [Fact]
    public async Task Touching_a_file_does_not_create_a_version()
    {
        WriteFile("a.ttml", Ttml("hello"));
        await Scan();
        WriteFile("a.ttml", Ttml("hello"), minutes: 1);

        var result = await Scan();

        Assert.Equal(0, result.NewVersions);
    }

    [Fact]
    public async Task Editing_lyrics_stays_in_the_same_song_as_a_new_version()
    {
        WriteFile("a.ttml", Ttml("hello"));
        await Scan();
        WriteFile("a.ttml", Ttml("completely different"), minutes: 1);

        var result = await Scan();

        Assert.Equal((1, 0), (result.NewVersions, result.NewSongs));
        Assert.Equal(2, Assert.Single(_db.GetSongs()).VersionCount);
    }

    [Fact]
    public async Task Reverting_reuses_the_old_version()
    {
        WriteFile("a.ttml", Ttml("hello"));
        await Scan();
        WriteFile("a.ttml", Ttml("changed"), minutes: 1);
        await Scan();
        WriteFile("a.ttml", Ttml("hello"), minutes: 2);

        var result = await Scan();

        Assert.Equal(0, result.NewVersions);
        var song = Assert.Single(_db.GetSongs());
        Assert.Equal(_db.GetVersions(song.Id)[0].Id, Assert.Single(_db.GetFiles()).VersionId);
    }

    [Fact]
    public async Task Moved_file_keeps_its_history()
    {
        var path = WriteFile("a.ttml", Ttml("hello"));
        await Scan();
        Directory.CreateDirectory(Path.Combine(_root, "new"));
        File.Move(path, Path.Combine(_root, "new", "renamed.ttml"));

        var result = await Scan();

        Assert.Equal((1, 0, 0), (result.Moved, result.NewFiles, result.Missing));
        Assert.EndsWith("renamed.ttml", Assert.Single(_db.GetFiles()).Path);
    }

    [Fact]
    public async Task Copy_and_retimed_file_join_the_same_song()
    {
        WriteFile("a.ttml", Ttml("hello"));
        WriteFile("a(1).ttml", Ttml("hello"));
        WriteFile("b.ttml", Ttml("hello", begin: "2.000"));

        var result = await Scan();

        Assert.Equal((3, 1, 2), (result.NewFiles, result.NewSongs, result.NewVersions));
    }

    [Fact]
    public async Task Different_lyrics_are_different_songs()
    {
        WriteFile("a.ttml", Ttml("hello"));
        WriteFile("b.ttml", Ttml("goodbye"));

        await Scan();

        Assert.Equal(2, _db.GetSongs().Count);
    }

    [Fact]
    public async Task Without_metadata_the_cleaned_file_name_is_shown()
    {
        WriteFile("Compass - mili(1).ttml", Ttml("hello", title: null));

        await Scan();

        var song = Assert.Single(_db.GetSongs());
        Assert.Equal("Compass - mili", song.DisplayTitle);
        Assert.True(song.NeedsIdentification);
    }

    [Fact]
    public async Task Deleted_file_is_missing_and_returns_when_restored()
    {
        var content = Ttml("hello");
        var path = WriteFile("a.ttml", content);
        await Scan();
        File.Delete(path);

        Assert.Equal(1, (await Scan()).Missing);
        Assert.Equal(FileState.Missing, Assert.Single(_db.GetFiles()).State);

        WriteFile("a.ttml", content);
        await Scan();

        Assert.Equal(FileState.Present, Assert.Single(_db.GetFiles()).State);
        Assert.Equal(1, Assert.Single(_db.GetSongs()).VersionCount);
    }

    [Fact]
    public async Task Unavailable_root_does_not_mark_files_missing()
    {
        WriteFile("a.ttml", Ttml("hello"));
        await Scan();
        Directory.Move(_root, _root + "-offline");

        var result = await Scan();

        Assert.Equal(_root, Assert.Single(result.UnavailableRoots));
        Assert.Equal(FileState.Present, Assert.Single(_db.GetFiles()).State);
    }

    [Fact]
    public async Task Corrupt_save_is_flagged_without_losing_history_or_stopping_the_scan()
    {
        WriteFile("a.ttml", Ttml("hello"));
        WriteFile("new-broken.ttml", "<tt><body>");
        await Scan();
        WriteFile("a.ttml", "<tt><body>", minutes: 1);
        WriteFile("z.ttml", Ttml("other"));

        var result = await Scan();

        Assert.Equal(1, result.Invalid);
        Assert.Equal(1, result.NewFiles);
        var file = _db.GetFiles().Single(f => f.Path.EndsWith("a.ttml"));
        Assert.Equal(FileState.Invalid, file.State);
        Assert.NotNull(file.ParseError);
        Assert.NotNull(file.VersionId);
    }

    [Fact]
    public async Task Cancelled_scan_does_not_mark_files_missing()
    {
        var path = WriteFile("a.ttml", Ttml("hello"));
        await Scan();
        File.Delete(path);
        WriteFile("b.ttml", Ttml("other"));
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => _scanner.ScanAsync(cancellationToken: cts.Token));

        Assert.Equal(FileState.Present, Assert.Single(_db.GetFiles()).State);
    }

    [Fact]
    public async Task Metadata_survives_a_version_that_lacks_it()
    {
        WriteFile("a.ttml", Ttml("hello"));
        await Scan();
        WriteFile("a.ttml", Ttml("hello", title: null), minutes: 1);

        await Scan();

        Assert.Equal("Song", Assert.Single(_db.GetSongs()).Title);
    }

    [Fact]
    public async Task Real_samples_become_one_song_each_and_rescan_is_incremental()
    {
        var samples = FindSamples();
        if (samples is null)
        {
            return;
        }

        var files = Directory.GetFiles(samples, "*.ttml");
        foreach (var file in files)
        {
            File.Copy(file, Path.Combine(_root, Path.GetFileName(file)));
        }

        var first = await Scan();
        var second = await Scan();

        Assert.Equal(files.Length, first.NewSongs);
        Assert.Empty(first.Errors);
        Assert.Equal(0, first.Invalid);
        Assert.Equal(files.Length, second.Unchanged);
        Assert.Contains(_db.GetSongs(), s => s.DisplayTitle == "Compass - mili" && s.NeedsIdentification);
    }

    private static string? FindSamples()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        {
            var candidate = Path.Combine(dir.FullName, "samples");
            if (Directory.Exists(candidate))
            {
                return candidate;
            }
        }

        return null;
    }
}
