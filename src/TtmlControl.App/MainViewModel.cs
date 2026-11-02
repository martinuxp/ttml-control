using System.Collections.ObjectModel;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using TtmlControl.Core.Library;

namespace TtmlControl.App;

public enum SongFilter
{
    All,
    Draft,
    InProgress,
    Complete,
    Uploaded,
    PendingChanges,
    Unidentified,
    Problems,
}

public enum SongSort
{
    Title,
    Artist,
    Status,
    LastEdited,
}

public sealed class SongRow(SongSummary song)
{
    public SongSummary Song { get; } = song;
    public long Id => Song.Id;
    public string Title => Song.DisplayTitle;
    public string Artists => Song.Artists ?? (Song.NeedsIdentification ? "Sin identificar" : "—");
    public string StatusText => Song.Status switch
    {
        SongStatus.InProgress => "En progreso",
        SongStatus.Complete => "Completo",
        SongStatus.Uploaded => "Subido",
        _ => "Borrador",
    };
    public bool HasPendingChanges => Song.HasChangesSinceStatus;
    public string Sync => Song.Timing switch
    {
        "Word" => "Palabra",
        "Line" => "Línea",
        _ => "—",
    };
    public string LastEdited => Song.LastEditedAt is { } value
        ? DateTimeOffset.Parse(value, CultureInfo.InvariantCulture).ToLocalTime().ToString("g")
        : "—";
    public string VersionCount => Song.VersionCount.ToString();
    public bool HasProblems => Song.InvalidFileCount > 0 || Song.MissingFileCount > 0;
    public string Problems => string.Join(", ", new[]
    {
        Song.InvalidFileCount > 0 ? $"{Song.InvalidFileCount} con errores" : null,
        Song.MissingFileCount > 0 ? $"{Song.MissingFileCount} faltante(s)" : null,
    }.OfType<string>());
    public string Duration => Song.DurationMs is { } ms ? TimeSpan.FromMilliseconds(ms).ToString(@"m\:ss") : "—";
}

public partial class MainViewModel(LibraryDatabase database) : ObservableObject
{
    private readonly LibraryScanner _scanner = new(database);
    private IReadOnlyList<SongSummary> _allSongs = [];
    private CancellationTokenSource? _scanCancellation;

    public ObservableCollection<SongRow> Songs { get; } = [];

    [ObservableProperty] public partial string SearchText { get; set; } = "";
    [ObservableProperty] public partial SongFilter Filter { get; set; }
    [ObservableProperty] public partial SongSort Sort { get; set; } = SongSort.LastEdited;
    [ObservableProperty] public partial bool SortDescending { get; set; } = true;
    [ObservableProperty] public partial bool IsScanning { get; set; }
    [ObservableProperty] public partial string ScanStatus { get; set; } = "";
    [ObservableProperty] public partial string Summary { get; set; } = "";
    [ObservableProperty] public partial string Message { get; set; } = "";
    [ObservableProperty] public partial bool IsLibraryEmpty { get; set; }

    public bool HasRoots => database.GetRoots().Count > 0;

    public IReadOnlyList<TrackedFile> GetFiles(long songId) => database.GetFiles(songId);

    public string GetLyrics(long songId) => database.GetLyrics(songId);

    public IReadOnlyList<string> GetRoots() => database.GetRoots();

    public IReadOnlyList<string> GetExclusions() => database.GetExclusions();

    public void RemoveRoot(string path) => database.RemoveRoot(path);

    public void AddExclusion(string path) => database.AddExclusion(path);

    public void RemoveExclusion(string path) => database.RemoveExclusion(path);

    public async Task SetIdentityAsync(long songId, string title, string artists)
    {
        await Task.Run(() => database.SetIdentity(songId, title, artists));
        await LoadAsync();
    }

    public async Task LoadAsync()
    {
        _allSongs = await Task.Run(database.GetSongs);
        IsLibraryEmpty = _allSongs.Count == 0;
        await ApplyViewAsync();
    }

    /// <summary>Rebuilds the visible rows. The window restores the selection afterwards.</summary>
    public async Task ApplyViewAsync()
    {
        var query = SearchText.Trim();
        var matches = query.Length == 0 ? null : await Task.Run(() => database.Search(query));

        // A newer keystroke may have started its own pass; the last one to finish matches SearchText.
        if (query != SearchText.Trim())
        {
            return;
        }

        var visible = _allSongs.Where(s => matches is null || matches.Contains(s.Id)).Where(MatchesFilter);
        var sorted = (Sort, SortDescending) switch
        {
            (SongSort.Artist, false) => visible.OrderBy(s => s.Artists, StringComparer.CurrentCultureIgnoreCase),
            (SongSort.Artist, true) => visible.OrderByDescending(s => s.Artists, StringComparer.CurrentCultureIgnoreCase),
            (SongSort.Status, false) => visible.OrderBy(s => s.Status),
            (SongSort.Status, true) => visible.OrderByDescending(s => s.Status),
            (SongSort.LastEdited, false) => visible.OrderBy(s => s.LastEditedAt, StringComparer.Ordinal),
            (SongSort.LastEdited, true) => visible.OrderByDescending(s => s.LastEditedAt, StringComparer.Ordinal),
            (_, false) => visible.OrderBy(s => s.DisplayTitle, StringComparer.CurrentCultureIgnoreCase),
            (_, true) => visible.OrderByDescending(s => s.DisplayTitle, StringComparer.CurrentCultureIgnoreCase),
        };

        Songs.Clear();
        foreach (var song in sorted)
        {
            Songs.Add(new SongRow(song));
        }

        Summary = $"{Songs.Count} de {_allSongs.Count} canciones";
    }

    private bool MatchesFilter(SongSummary song) => Filter switch
    {
        SongFilter.Draft => song.Status == SongStatus.Draft,
        SongFilter.InProgress => song.Status == SongStatus.InProgress,
        SongFilter.Complete => song.Status == SongStatus.Complete,
        SongFilter.Uploaded => song.Status == SongStatus.Uploaded,
        SongFilter.PendingChanges => song.HasChangesSinceStatus,
        SongFilter.Unidentified => song.NeedsIdentification,
        SongFilter.Problems => song.InvalidFileCount > 0 || song.MissingFileCount > 0,
        _ => true,
    };

    public void ToggleSort(SongSort sort)
    {
        SortDescending = Sort == sort ? !SortDescending : sort == SongSort.LastEdited;
        Sort = sort;
    }

    public async Task SetStatusAsync(IReadOnlyList<long> songIds, SongStatus status)
    {
        await Task.Run(() => database.SetStatus(songIds, status));
        await LoadAsync();
    }

    public async Task AddRootAndScanAsync(string path)
    {
        database.AddRoot(path);
        await ScanAsync();
    }

    public void CancelScan() => _scanCancellation?.Cancel();

    public async Task ScanAsync()
    {
        if (IsScanning)
        {
            return;
        }

        IsScanning = true;
        Message = "";
        _scanCancellation = new CancellationTokenSource();
        var progress = new Progress<ScanProgress>(p => ScanStatus = $"{p.FilesProcessed} archivos: {Path.GetFileName(p.CurrentPath)}");
        try
        {
            var result = await _scanner.ScanAsync(progress, _scanCancellation.Token);
            ScanStatus = "";
            Message = Describe(result);
        }
        catch (OperationCanceledException)
        {
            ScanStatus = "";
            Message = "Escaneo cancelado.";
        }
        finally
        {
            IsScanning = false;
            _scanCancellation.Dispose();
            _scanCancellation = null;
        }

        await LoadAsync();
    }

    private static string Describe(ScanResult result)
    {
        var parts = new List<string>();
        void Add(int count, string singular, string plural)
        {
            if (count > 0)
            {
                parts.Add($"{count} {(count == 1 ? singular : plural)}");
            }
        }

        Add(result.NewSongs, "canción nueva", "canciones nuevas");
        Add(result.NewVersions, "versión nueva", "versiones nuevas");
        Add(result.Moved, "archivo movido", "archivos movidos");
        Add(result.Missing, "archivo faltante", "archivos faltantes");
        Add(result.Invalid, "archivo con errores", "archivos con errores");
        Add(result.Errors.Count, "archivo sin poder leer", "archivos sin poder leer");
        Add(result.UnavailableRoots.Count, "carpeta no disponible", "carpetas no disponibles");
        return parts.Count == 0 ? "" : "Escaneo: " + string.Join(", ", parts) + ".";
    }
}
