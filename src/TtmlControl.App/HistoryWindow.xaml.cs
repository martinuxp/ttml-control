using System.Globalization;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using TtmlControl.Core.Library;
using TtmlControl.Core.Ttml;
using Windows.Graphics;

namespace TtmlControl.App;

public sealed class VersionItem
{
    public required SongVersion Version { get; init; }
    public required TtmlDocument Document { get; init; }
    public required string Date { get; init; }
    public required string Badges { get; init; }
    public required string Summary { get; init; }
    public string Label => Version.Label ?? "";
    public bool HasLabel => Version.Label is not null;
    public bool HasBadges => Badges.Length > 0;
}

public sealed record DiffRow(string Kind, Brush Brush, string OldTime, string OldText, string NewTime, string NewText);

public sealed partial class HistoryWindow : Window
{
    private readonly MainViewModel _viewModel;
    private readonly long _songId;
    private readonly Func<Task> _refreshMainWindow;
    private List<VersionItem> _items = [];
    private long? _currentVersionId;
    private bool _reloading;

    public HistoryWindow(MainViewModel viewModel, long songId, Func<Task> refreshMainWindow)
    {
        _viewModel = viewModel;
        _songId = songId;
        _refreshMainWindow = refreshMainWindow;
        InitializeComponent();
        AppWindow.Resize(new SizeInt32(1200, 800));
        Reload();
    }

    /// <summary>Reads the song again, keeping the selected version.</summary>
    public void Reload()
    {
        var song = _viewModel.GetSong(_songId);
        if (song is null)
        {
            Close();
            return;
        }

        Title = $"Historial: {song.DisplayTitle}";
        SongTitle.Text = song.DisplayTitle;
        _currentVersionId = song.CurrentVersionId;

        var selectedId = (VersionList.SelectedItem as VersionItem)?.Version.Id ?? song.CurrentVersionId;
        _items = BuildItems(song);

        _reloading = true;
        VersionList.ItemsSource = _items;
        VersionList.SelectedItem = _items.FirstOrDefault(i => i.Version.Id == selectedId) ?? _items.FirstOrDefault();
        _reloading = false;
        ShowSelection();
    }

    private List<VersionItem> BuildItems(SongSummary song)
    {
        var statuses = _viewModel.GetStatusHistory(_songId);
        var items = new List<VersionItem>();
        TtmlDocument? previous = null;

        foreach (var version in _viewModel.GetVersions(_songId))
        {
            var document = TtmlParser.Parse(new MemoryStream(_viewModel.GetVersionContent(version.Id)));
            items.Add(new VersionItem
            {
                Version = version,
                Document = document,
                Date = FormatDate(version.FileModifiedAt),
                Badges = BuildBadges(version, song, statuses),
                Summary = previous is null ? "Primera versión" : Summarize(TtmlDiff.Compare(previous, document)),
            });
            previous = document;
        }

        items.Reverse();
        return items;
    }

    private static string BuildBadges(SongVersion version, SongSummary song, IReadOnlyList<StatusRecord> statuses)
    {
        var badges = new List<string>();
        if (version.Id == song.CurrentVersionId)
        {
            badges.Add("Actual");
        }

        // The last mark of each kind is the one that matters: "this is what I uploaded".
        foreach (var status in new[] { SongStatus.Complete, SongStatus.Uploaded })
        {
            if (statuses.LastOrDefault(s => s.Status == status)?.VersionId == version.Id)
            {
                badges.Add(status == SongStatus.Complete ? "Marcada completa" : "Marcada subida");
            }
        }

        return string.Join(" · ", badges);
    }

    private static string Summarize(TtmlDiff diff)
    {
        var parts = new List<string>();
        if (diff.LyricChanges > 0)
        {
            parts.Add(Plural(diff.LyricChanges, "línea de letra", "líneas de letra"));
        }

        if (diff.TimingChanges > 0)
        {
            parts.Add(Plural(diff.TimingChanges, "línea de tiempos", "líneas de tiempos"));
        }

        if (diff.VoiceChanges > 0)
        {
            parts.Add(Plural(diff.VoiceChanges, "cambio de voz", "cambios de voz"));
        }

        if (diff.Metadata.Count > 0)
        {
            parts.Add(Plural(diff.Metadata.Count, "dato", "datos"));
        }

        return parts.Count == 0 ? "Sin cambios de contenido" : string.Join(", ", parts);
    }

    private static string Plural(int count, string singular, string plural) => $"{count} {(count == 1 ? singular : plural)}";

    private static string FormatDate(string value) =>
        DateTimeOffset.Parse(value, CultureInfo.InvariantCulture).ToLocalTime().ToString("g");

    private void VersionList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!_reloading)
        {
            ShowSelection();
        }
    }

    private void DiffOption_Changed(object sender, RoutedEventArgs e)
    {
        // CompareBox raises this for its initial value, before the rest of the window exists.
        if (DiffList is not null)
        {
            ShowSelection();
        }
    }

    private void ShowSelection()
    {
        var selected = VersionList.SelectedItem as VersionItem;
        LabelButton.IsEnabled = selected is not null;
        RestoreButton.IsEnabled = selected is not null;

        if (selected is null)
        {
            DiffList.ItemsSource = null;
            return;
        }

        var baseItem = CompareBox.SelectedIndex == 1
            ? _items.FirstOrDefault(i => i.Version.Id == _currentVersionId)
            : _items.ElementAtOrDefault(_items.IndexOf(selected) + 1);

        if (baseItem is null || baseItem == selected)
        {
            DiffSummary.Text = baseItem is null ? "Primera versión: no hay nada anterior con qué comparar." : "Esta es la versión actual.";
            MetadataSummary.Text = OldHeader.Text = NewHeader.Text = "";
            DiffList.ItemsSource = null;
            return;
        }

        var (older, newer) = CompareBox.SelectedIndex == 1 && baseItem.Version.Id != selected.Version.Id
            ? (selected, baseItem)
            : (baseItem, selected);
        var diff = TtmlDiff.Compare(older.Document, newer.Document);

        OldHeader.Text = $"Antes ({older.Date})";
        NewHeader.Text = $"Después ({newer.Date})";
        DiffSummary.Text = Summarize(diff);
        MetadataSummary.Text = string.Join('\n', diff.Metadata.Select(m => $"{m.Field}: {m.Old ?? "(vacío)"} → {m.New ?? "(vacío)"}"));
        DiffList.ItemsSource = diff.Lines.Where(IsVisible).Select(ToRow).ToList();
    }

    private bool IsVisible(LineChange change) => change.Kind switch
    {
        LineChangeKind.Unchanged => ShowUnchanged.IsChecked == true,
        LineChangeKind.TimingChanged or LineChangeKind.VoiceChanged => ShowTimings.IsChecked == true,
        _ => ShowLyrics.IsChecked == true,
    };

    private static DiffRow ToRow(LineChange change)
    {
        var (kind, brushKey) = change.Kind switch
        {
            LineChangeKind.TextChanged => ("Letra", "SystemFillColorCautionBrush"),
            LineChangeKind.TimingChanged => ("Tiempos", "AccentTextFillColorPrimaryBrush"),
            LineChangeKind.VoiceChanged => ("Voz", "AccentTextFillColorPrimaryBrush"),
            LineChangeKind.Added => ("Añadida", "SystemFillColorSuccessBrush"),
            LineChangeKind.Removed => ("Eliminada", "SystemFillColorCriticalBrush"),
            _ => ("Igual", "TextFillColorTertiaryBrush"),
        };

        return new DiffRow(
            kind,
            (Brush)Application.Current.Resources[brushKey],
            Time(change.Old),
            Text(change.Old),
            Time(change.New),
            Text(change.New));
    }

    private static string Time(TtmlLine? line) =>
        line?.Begin is { } begin ? begin.ToString(@"m\:ss\.fff") + (line.End is { } end ? " - " + end.ToString(@"m\:ss\.fff") : "") : "";

    private static string Text(TtmlLine? line)
    {
        if (line is null)
        {
            return "";
        }

        return line.BackgroundText.Length == 0 ? line.Text : $"{line.Text} [{line.BackgroundText}]";
    }

    private async void Label_Click(object sender, RoutedEventArgs e)
    {
        if (VersionList.SelectedItem is not VersionItem item)
        {
            return;
        }

        var box = new TextBox { Header = "Etiqueta", Text = item.Label, MinWidth = 320, PlaceholderText = "Por ejemplo: versión enviada" };
        var dialog = new ContentDialog
        {
            XamlRoot = Content.XamlRoot,
            Title = "Etiquetar versión",
            Content = box,
            PrimaryButtonText = "Guardar",
            CloseButtonText = "Cancelar",
            DefaultButton = ContentDialogButton.Primary,
        };

        if (await dialog.ShowAsync() == ContentDialogResult.Primary)
        {
            _viewModel.SetVersionLabel(item.Version.Id, box.Text);
            Reload();
        }
    }

    private async void Restore_Click(object sender, RoutedEventArgs e)
    {
        if (VersionList.SelectedItem is not VersionItem item)
        {
            return;
        }

        var files = _viewModel.GetFiles(_songId);
        if (files.Count == 0)
        {
            return;
        }

        var fileBox = new ComboBox
        {
            Header = "Archivo que se reemplazará",
            HorizontalAlignment = HorizontalAlignment.Stretch,
        };
        foreach (var file in files)
        {
            fileBox.Items.Add(new ComboBoxItem
            {
                Content = file.State == FileState.Missing ? $"{file.Path} (faltante: se creará de nuevo)" : file.Path,
                Tag = file.Path,
            });
        }

        // Long paths get cut off inside the box, and this is the file about to be overwritten.
        var fullPath = new TextBlock { TextWrapping = TextWrapping.Wrap, FontSize = 12, IsTextSelectionEnabled = true };
        fileBox.SelectionChanged += (_, _) => fullPath.Text = (string)((ComboBoxItem)fileBox.SelectedItem).Tag;
        fileBox.SelectedIndex = files.ToList().IndexOf(TrackedFile.PickRestoreTarget(files, item.Version.Id)!);

        var dialog = new ContentDialog
        {
            XamlRoot = Content.XamlRoot,
            Title = "Restaurar versión",
            Content = new StackPanel
            {
                Spacing = 12,
                MinWidth = 480,
                Children =
                {
                    new TextBlock
                    {
                        Text = $"Se escribirá la versión del {item.Date} sobre este archivo. Su contenido actual se guarda antes en el historial.",
                        TextWrapping = TextWrapping.Wrap,
                    },
                    fileBox,
                    fullPath,
                },
            },
            PrimaryButtonText = "Restaurar",
            CloseButtonText = "Cancelar",
            DefaultButton = ContentDialogButton.Close,
        };

        if (await dialog.ShowAsync() != ContentDialogResult.Primary)
        {
            return;
        }

        var path = (string)((ComboBoxItem)fileBox.SelectedItem).Tag;
        try
        {
            var result = await _viewModel.RestoreAsync(item.Version.Id, path);
            await _refreshMainWindow();
            Reload();
            if (result.BackupPath is not null)
            {
                await ShowMessageAsync("Restaurada", $"El archivo estaba dañado y no se podía guardar como versión. Se dejó una copia en:\n{result.BackupPath}");
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            await ShowMessageAsync("No se pudo restaurar", ex.Message);
        }
    }

    private async Task ShowMessageAsync(string title, string message)
    {
        var dialog = new ContentDialog
        {
            XamlRoot = Content.XamlRoot,
            Title = title,
            Content = new TextBlock { Text = message, TextWrapping = TextWrapping.Wrap, IsTextSelectionEnabled = true },
            CloseButtonText = "Cerrar",
        };
        await dialog.ShowAsync();
    }
}
