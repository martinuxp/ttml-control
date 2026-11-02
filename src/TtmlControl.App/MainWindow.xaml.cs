using System.ComponentModel;
using System.Diagnostics;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using TtmlControl.Core.Library;
using TtmlControl.Core.Ttml;
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
        if (await PickFolderAsync() is { } path)
        {
            await Refresh(() => ViewModel.AddRootAndScanAsync(path));
        }
    }

    private async Task<string?> PickFolderAsync()
    {
        var picker = new FolderPicker();
        picker.FileTypeFilter.Add("*");
        InitializeWithWindow.Initialize(picker, WindowNative.GetWindowHandle(this));
        return (await picker.PickSingleFolderAsync())?.Path;
    }

    private async void ManageFolders_Click(object sender, RoutedEventArgs e)
    {
        var content = new StackPanel { Spacing = 8, MinWidth = 460 };

        void Rebuild()
        {
            content.Children.Clear();
            AddFolderSection(content, "Carpetas escaneadas", ViewModel.GetRoots(), ViewModel.RemoveRoot, Rebuild);
            AddFolderSection(content, "Carpetas excluidas", ViewModel.GetExclusions(), ViewModel.RemoveExclusion, Rebuild);

            var exclude = new Button { Content = "Excluir carpeta..." };
            exclude.Click += async (_, _) =>
            {
                if (await PickFolderAsync() is { } path)
                {
                    ViewModel.AddExclusion(path);
                    Rebuild();
                }
            };
            content.Children.Add(exclude);
            content.Children.Add(new TextBlock
            {
                Text = "Al quitar o excluir una carpeta, sus canciones se ocultan. Su historial se conserva y vuelve si la añades de nuevo.",
                TextWrapping = TextWrapping.Wrap,
                FontSize = 12,
            });
        }

        Rebuild();
        var dialog = new ContentDialog
        {
            XamlRoot = Content.XamlRoot,
            Title = "Carpetas",
            Content = new ScrollViewer { Content = content },
            CloseButtonText = "Cerrar",
        };
        await dialog.ShowAsync();
        await Refresh(ViewModel.ScanAsync);
    }

    private static void AddFolderSection(StackPanel parent, string title, IReadOnlyList<string> paths, Action<string> remove, Action rebuild)
    {
        parent.Children.Add(new TextBlock { Text = title, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold });
        if (paths.Count == 0)
        {
            parent.Children.Add(new TextBlock { Text = "Ninguna" });
        }

        foreach (var path in paths)
        {
            var row = new Grid { ColumnSpacing = 8 };
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            row.Children.Add(new TextBlock { Text = path, TextWrapping = TextWrapping.Wrap, VerticalAlignment = VerticalAlignment.Center });

            var button = new Button { Content = "Quitar" };
            Grid.SetColumn(button, 1);
            button.Click += (_, _) =>
            {
                remove(path);
                rebuild();
            };
            row.Children.Add(button);
            parent.Children.Add(row);
        }
    }

    private async void EditIdentity_Click(object sender, RoutedEventArgs e)
    {
        if (SongList.SelectedItems.OfType<SongRow>().FirstOrDefault() is not { } row)
        {
            return;
        }

        // The file name's order varies, so the guess can be swapped with one click.
        var parts = FileNameHints.FromName(row.Song.FileNameHint).Parts;
        var titleBox = new TextBox
        {
            Header = "Título",
            Text = row.Song.Title ?? parts.FirstOrDefault() ?? "",
        };
        var artistsBox = new TextBox
        {
            Header = "Artista",
            Text = row.Song.Artists ?? (row.Song.Title is null ? string.Join(" - ", parts.Skip(1)) : ""),
        };
        var swap = new Button { Content = "Intercambiar" };
        swap.Click += (_, _) => (titleBox.Text, artistsBox.Text) = (artistsBox.Text, titleBox.Text);

        var dialog = new ContentDialog
        {
            XamlRoot = Content.XamlRoot,
            Title = "Editar datos",
            Content = new StackPanel
            {
                Spacing = 12,
                MinWidth = 360,
                Children =
                {
                    titleBox,
                    artistsBox,
                    swap,
                    new TextBlock
                    {
                        Text = "Tu TTML no se modifica. Deja un campo vacío para usar el dato del archivo.",
                        TextWrapping = TextWrapping.Wrap,
                        FontSize = 12,
                    },
                },
            },
            PrimaryButtonText = "Guardar",
            CloseButtonText = "Cancelar",
            DefaultButton = ContentDialogButton.Primary,
        };

        if (await dialog.ShowAsync() == ContentDialogResult.Primary)
        {
            await Refresh(() => ViewModel.SetIdentityAsync(row.Id, titleBox.Text, artistsBox.Text));
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
            EditButton.IsEnabled = OpenFileButton.IsEnabled = ShowFileButton.IsEnabled = false;
            return;
        }

        if (rows.Count == 0)
        {
            return;
        }

        var row = rows[0];
        var song = row.Song;
        EditButton.IsEnabled = true;
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
