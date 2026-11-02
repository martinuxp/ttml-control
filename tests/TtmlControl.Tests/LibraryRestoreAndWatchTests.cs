using System.Text;
using TtmlControl.Core.Library;

namespace TtmlControl.Tests;

public sealed class LibraryRestoreAndWatchTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("ttml-tests-").FullName;
    private readonly string _root;
    private readonly LibraryDatabase _db;
    private readonly LibraryScanner _scanner;

    public LibraryRestoreAndWatchTests()
    {
        _root = Directory.CreateDirectory(Path.Combine(_dir, "root")).FullName;
        _db = new LibraryDatabase(Path.Combine(_dir, "library.db"));
        _db.AddRoot(_root);
        _scanner = new LibraryScanner(_db);
    }

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    private static string Ttml(string lyrics) =>
        $"""<tt><body><div><p begin="1.000" end="9.000"><span begin="1.000" end="9.000">{lyrics}</span></p></div></body></tt>""";

    private string Write(string lyrics, int minutes = 0, string name = "a.ttml")
    {
        var path = Path.Combine(_root, name);
        File.WriteAllText(path, Ttml(lyrics), new UTF8Encoding(false));
        File.SetLastWriteTimeUtc(path, new DateTime(2026, 1, 1, 0, minutes, 0, DateTimeKind.Utc));
        return path;
    }

    private SongVersion[] Versions() => _db.GetVersions(Assert.Single(_db.GetSongs()).Id).ToArray();

    [Fact]
    public async Task Restore_writes_the_exact_old_bytes_and_makes_that_version_current()
    {
        var path = Write("first");
        await _scanner.ScanAsync();
        Write("second", minutes: 1);
        await _scanner.ScanAsync();
        var first = Versions()[0];

        await _scanner.RestoreAsync(first.Id, path);

        Assert.Equal(Ttml("first"), File.ReadAllText(path));
        Assert.Equal(first.Id, Assert.Single(_db.GetSongs()).CurrentVersionId);
        Assert.Equal(2, Versions().Length);
        Assert.Equal(FileState.Present, Assert.Single(_db.GetFiles()).State);
        Assert.Empty(Directory.GetFiles(_root, "*.restoring"));
    }

    [Fact]
    public async Task Restore_first_saves_edits_no_scan_has_seen()
    {
        var path = Write("first");
        await _scanner.ScanAsync();
        var first = Versions()[0];
        Write("unscanned work", minutes: 1);

        await _scanner.RestoreAsync(first.Id, path);

        var versions = Versions();
        Assert.Equal(2, versions.Length);
        Assert.Equal(Ttml("unscanned work"), Encoding.UTF8.GetString(_db.GetVersionContent(versions[1].Id)));
        Assert.Equal(Ttml("first"), File.ReadAllText(path));
    }

    [Fact]
    public async Task Restoring_over_a_corrupt_file_keeps_a_backup_of_it()
    {
        var path = Write("first");
        await _scanner.ScanAsync();
        File.WriteAllText(path, "<tt><body>");

        var result = await _scanner.RestoreAsync(Versions()[0].Id, path);

        Assert.NotNull(result.BackupPath);
        Assert.Equal("<tt><body>", File.ReadAllText(result.BackupPath));
        Assert.Equal(Ttml("first"), File.ReadAllText(path));
        Assert.Equal(FileState.Present, Assert.Single(_db.GetFiles()).State);
    }

    [Fact]
    public async Task Restore_recreates_a_deleted_file()
    {
        var path = Write("first");
        await _scanner.ScanAsync();
        File.Delete(path);
        await _scanner.ScanAsync();

        await _scanner.RestoreAsync(Versions()[0].Id, path);

        Assert.Equal(Ttml("first"), File.ReadAllText(path));
        Assert.Equal(FileState.Present, Assert.Single(_db.GetFiles()).State);
    }

    [Fact]
    public async Task Restore_refuses_a_file_of_another_song()
    {
        var path = Write("first");
        var other = Write("different", name: "b.ttml");
        await _scanner.ScanAsync();
        var song = _db.GetSongs().Single(s => s.FileNameHint == "a");

        await Assert.ThrowsAsync<InvalidOperationException>(() => _scanner.RestoreAsync(_db.GetVersions(song.Id)[0].Id, other));

        Assert.Equal(Ttml("different"), File.ReadAllText(other));
        Assert.Equal(Ttml("first"), File.ReadAllText(path));
    }

    [Fact]
    public async Task Failed_restore_leaves_the_file_and_no_temporary()
    {
        var path = Write("first");
        await _scanner.ScanAsync();
        Write("second", minutes: 1);
        await _scanner.ScanAsync();
        File.SetAttributes(path, FileAttributes.ReadOnly);

        try
        {
            await Assert.ThrowsAnyAsync<Exception>(() => _scanner.RestoreAsync(Versions()[0].Id, path));
            Assert.Equal(Ttml("second"), File.ReadAllText(path));
            Assert.Empty(Directory.GetFiles(_root, "*.restoring"));
        }
        finally
        {
            File.SetAttributes(path, FileAttributes.Normal);
        }
    }

    [Fact]
    public async Task Restoring_the_uploaded_version_clears_pending_changes()
    {
        var path = Write("first");
        await _scanner.ScanAsync();
        var song = Assert.Single(_db.GetSongs());
        _db.SetStatus([song.Id], SongStatus.Uploaded);
        Write("second", minutes: 1);
        await _scanner.ScanAsync();
        Assert.True(Assert.Single(_db.GetSongs()).HasChangesSinceStatus);

        await _scanner.RestoreAsync(Versions()[0].Id, path);

        Assert.False(Assert.Single(_db.GetSongs()).HasChangesSinceStatus);
    }

    [Fact]
    public void Restore_target_prefers_existing_files_over_deleted_copies()
    {
        var deleted = new TrackedFile { Path = "a", VersionId = 2, State = FileState.Missing };
        var atOtherVersion = new TrackedFile { Path = "b", VersionId = 2, State = FileState.Present };
        var atThisVersion = new TrackedFile { Path = "c", VersionId = 1, State = FileState.Present };

        Assert.Equal("b", TrackedFile.PickRestoreTarget([deleted, atThisVersion, atOtherVersion], 1)!.Path);
        Assert.Equal("c", TrackedFile.PickRestoreTarget([deleted, atThisVersion], 1)!.Path);
        Assert.Equal("a", TrackedFile.PickRestoreTarget([deleted], 1)!.Path);
    }

    [Fact]
    public async Task Labels_can_be_set_and_cleared()
    {
        Write("first");
        await _scanner.ScanAsync();
        var id = Versions()[0].Id;

        _db.SetVersionLabel(id, " final ");
        Assert.Equal("final", Versions()[0].Label);

        _db.SetVersionLabel(id, "");
        Assert.Null(Versions()[0].Label);
    }

    [Fact]
    public async Task Status_history_records_the_version()
    {
        Write("first");
        await _scanner.ScanAsync();
        var song = Assert.Single(_db.GetSongs());

        _db.SetStatus([song.Id], SongStatus.Complete);
        _db.SetStatus([song.Id], SongStatus.Uploaded);

        var history = _db.GetStatusHistory(song.Id);
        Assert.Equal([SongStatus.Complete, SongStatus.Uploaded], history.Select(h => h.Status));
        Assert.All(history, h => Assert.Equal(song.CurrentVersionId, h.VersionId));
    }

    [Fact]
    public async Task Concurrent_scans_do_not_collide()
    {
        for (var i = 0; i < 30; i++)
        {
            Write($"song {i}", name: $"s{i}.ttml");
        }

        var results = await Task.WhenAll(_scanner.ScanAsync(), _scanner.ScanAsync(), _scanner.ScanAsync());

        Assert.Equal(30, results.Sum(r => r.NewFiles));
        Assert.Equal(30, _db.GetSongs().Count);
    }

    [Theory]
    [InlineData(WatcherChangeTypes.Changed, @"C:\x\a.ttml", true)]
    [InlineData(WatcherChangeTypes.Changed, @"C:\x\A.TTML", true)]
    [InlineData(WatcherChangeTypes.Changed, @"C:\x\a.txt", false)]
    [InlineData(WatcherChangeTypes.Changed, @"C:\x\sub", false)]
    [InlineData(WatcherChangeTypes.Deleted, @"C:\x\a.ttml.restoring", false)]
    [InlineData(WatcherChangeTypes.Deleted, @"C:\x\folder", true)]
    public void Watcher_filters_irrelevant_events(WatcherChangeTypes change, string path, bool expected)
    {
        Assert.Equal(expected, LibraryWatcher.IsRelevant(change, path));
    }

    [Fact]
    public async Task Watcher_coalesces_a_burst_of_saves_and_ignores_other_files()
    {
        using var watcher = new LibraryWatcher(TimeSpan.FromMilliseconds(300));
        var count = 0;
        watcher.Changed += () => Interlocked.Increment(ref count);
        watcher.SetRoots([_root]);

        File.WriteAllText(Path.Combine(_root, "notes.txt"), "x");
        await Task.Delay(800);
        Assert.Equal(0, count);

        for (var i = 0; i < 5; i++)
        {
            File.WriteAllText(Path.Combine(_root, "a.ttml"), $"content {i}");
            await Task.Delay(30);
        }

        await Task.Delay(1000);
        Assert.Equal(1, count);
    }

    [Fact]
    public async Task Watcher_sees_folder_renames_and_skips_missing_roots()
    {
        var folder = Directory.CreateDirectory(Path.Combine(_root, "album")).FullName;
        using var watcher = new LibraryWatcher(TimeSpan.FromMilliseconds(200));
        var count = 0;
        watcher.Changed += () => Interlocked.Increment(ref count);
        watcher.SetRoots([_root, Path.Combine(_dir, "does-not-exist")]);

        Directory.Move(folder, folder + "-renamed");
        await Task.Delay(800);

        Assert.True(count >= 1);
    }
}
