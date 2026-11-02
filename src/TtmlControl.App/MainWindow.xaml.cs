using System.ComponentModel;
using System.Diagnostics;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using TtmlControl.Core.Library;
using Windows.Storage.Pickers;
using WinRT.Interop;

namespace TtmlControl.App;

public sealed partial class MainWindow : Window
{
    private bool _restoringSelection;
    private string? _selectedFilePath;

    public MainViewModel ViewModel { get; }

    public MainWindow(MainViewModel viewModel)
    {
        ViewModel = viewModel;
        InitializeComponent();
        ViewModel.PropertyChanged += ViewModel_PropertyChanged;
        _ = StartAsync();
    }

    public static bool HasText(string text) => !string.IsNullOrEmpty(text);

    private async Task StartAsync()
    {
        await Refresh(ViewModel.LoadAsync);
        if (ViewModel.HasRoots)
        {
            await Refresh(ViewModel.ScanAsync);
        }
    }

    private void ViewModel_PropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(MainViewModel.IsScanning))
        {
            ScanButton.IsEnabled = !ViewModel.IsScanning;
        }
    }

    /// <summary>Runs an operation that rebuilds the rows, keeping the selected songs selected.</summary>
    private async Task Refresh(Func<Task> operation)
    {
        var selected = SongList.SelectedItems.OfType<SongRow>().Select(r => r.Id).ToHashSet();
        _restoringSelection = true;
        try
        {
            await operation();
            foreach (var row in ViewModel.Songs.Where(r => selected.Contains(r.Id)))
            {
                SongList.SelectedItems.Add(row);
            }
        }
        catch (Exception ex)
        {
            ViewModel.Message = $"Error: {ex.Message}";
        }
        finally
        {
            _restoringSelection = false;
            ShowSelection();
        }
    }

    private async void SearchBox_TextChanged(AutoSuggestBox sender, AutoSuggestBoxTextChangedEventArgs args)
    {
        ViewModel.SearchText = sender.Text;
        await Refresh(ViewModel.ApplyViewAsync);
    }

    private async void FilterBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        // Fires for the initial SelectedIndex, while InitializeComponent has not created SongList yet.
        if (SongList is null || FilterBox.SelectedItem is not ComboBoxItem { Tag: string tag })
        {
            return;
        }

        ViewModel.Filter = Enum.Parse<SongFilter>(tag);
        await Refresh(ViewModel.ApplyViewAsync);
    }

    private async void Sort_Click(object sender, RoutedEventArgs e)
    {
        ViewModel.ToggleSort(Enum.Parse<SongSort>((string)((FrameworkElement)sender).Tag));
        await Refresh(ViewModel.ApplyViewAsync);
    }

    private async void SetStatus_Click(object sender, RoutedEventArgs e)
    {
        var status = Enum.Parse<SongStatus>((string)((FrameworkElement)sender).Tag);
        var ids = SongList.SelectedItems.OfType<SongRow>().Select(r => r.Id).ToList();
        await Refresh(() => ViewModel.SetStatusAsync(ids, status));
    }

    private async void AddFolder_Click(object sender, RoutedEventArgs e)
    {
        var picker = new FolderPicker();
        picker.FileTypeFilter.Add("*");
        InitializeWithWindow.Initialize(picker, WindowNative.GetWindowHandle(this));

        var folder = await picker.PickSingleFolderAsync();
        if (folder is not null)
        {
            await Refresh(() => ViewModel.AddRootAndScanAsync(folder.Path));
        }
    }

    private async void Scan_Click(object sender, RoutedEventArgs e)
    {
        await Refresh(ViewModel.ScanAsync);
    }

    private void CancelScan_Click(object sender, RoutedEventArgs e) => ViewModel.CancelScan();

    private void SongList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!_restoringSelection)
        {
            ShowSelection();
        }
    }

    private void ShowSelection()
    {
        var rows = SongList.SelectedItems.OfType<SongRow>().ToList();
        StatusButton.IsEnabled = rows.Count > 0;
        DetailPanel.Visibility = rows.Count == 0 ? Visibility.Collapsed : Visibility.Visible;

        if (rows.Count > 1)
        {
            DetailTitle.Text = $"{rows.Count} canciones seleccionadas";
            DetailMetadata.Text = DetailFiles.Text = DetailLyrics.Text = "";
            OpenFileButton.IsEnabled = ShowFileButton.IsEnabled = false;
            return;
        }

        if (rows.Count == 0)
        {
            return;
        }

        var row = rows[0];
        var song = row.Song;
        DetailTitle.Text = row.Title;
        DetailMetadata.Text = string.Join('\n', new[]
        {
            song.Artists,
            song.Album is null ? null : $"Álbum: {song.Album}",
            song.Isrc is null ? null : $"ISRC: {song.Isrc}",
            song.Songwriters is null ? null : $"Compositores: {song.Songwriters}",
            $"Duración: {row.Duration} · {song.LineCount} líneas · Sync: {row.Sync}",
            $"Estado: {row.StatusText}{(row.HasPendingChanges ? " (con cambios posteriores)" : "")}",
            $"Última edición: {row.LastEdited}",
        }.OfType<string>());

        var files = ViewModel.GetFiles(song.Id);
        DetailFiles.Text = string.Join('\n', files.Select(f => f.State switch
        {
            FileState.Missing => $"[Faltante] {f.Path}",
            FileState.Invalid => $"[Error: {f.ParseError}] {f.Path}",
            _ => f.Path,
        }));
        _selectedFilePath = files.FirstOrDefault(f => f.State != FileState.Missing)?.Path;
        OpenFileButton.IsEnabled = ShowFileButton.IsEnabled = _selectedFilePath is not null;
        DetailLyrics.Text = ViewModel.GetLyrics(song.Id);
    }

    private void OpenFile_Click(object sender, RoutedEventArgs e) =>
        Launch(new ProcessStartInfo(_selectedFilePath!) { UseShellExecute = true });

    private void ShowFile_Click(object sender, RoutedEventArgs e) =>
        Launch(new ProcessStartInfo("explorer.exe") { ArgumentList = { $"/select,{_selectedFilePath}" } });

    private void Launch(ProcessStartInfo startInfo)
    {
        try
        {
            Process.Start(startInfo);
        }
        catch (Exception ex) when (ex is Win32Exception or FileNotFoundException)
        {
            ViewModel.Message = $"No se pudo abrir: {ex.Message}";
        }
    }
}
