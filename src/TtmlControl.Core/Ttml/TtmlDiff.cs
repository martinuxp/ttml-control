namespace TtmlControl.Core.Ttml;

public enum LineChangeKind
{
    Unchanged,
    TextChanged,
    TimingChanged,
    VoiceChanged,
    Added,
    Removed,
}

public sealed record LineChange(LineChangeKind Kind, TtmlLine? Old, TtmlLine? New);

public sealed record MetadataChange(string Field, string? Old, string? New);

public sealed record TtmlDiff(IReadOnlyList<LineChange> Lines, IReadOnlyList<MetadataChange> Metadata)
{
    public int Count(params LineChangeKind[] kinds) => Lines.Count(l => kinds.Contains(l.Kind));

    public int LyricChanges => Count(LineChangeKind.TextChanged, LineChangeKind.Added, LineChangeKind.Removed);

    public int TimingChanges => Count(LineChangeKind.TimingChanged);

    public int VoiceChanges => Count(LineChangeKind.VoiceChanged);

    public static TtmlDiff Compare(TtmlDocument oldDocument, TtmlDocument newDocument) =>
        new(CompareLines(oldDocument.Lines, newDocument.Lines), CompareMetadata(oldDocument, newDocument));

    private static List<LineChange> CompareLines(IReadOnlyList<TtmlLine> oldLines, IReadOnlyList<TtmlLine> newLines)
    {
        var lengths = BuildLcsTable(oldLines, newLines);
        var changes = new List<LineChange>();
        var removed = new List<TtmlLine>();
        var added = new List<TtmlLine>();
        int i = 0, j = 0;

        while (i < oldLines.Count || j < newLines.Count)
        {
            if (i < oldLines.Count && j < newLines.Count && SameText(oldLines[i], newLines[j]))
            {
                FlushEdits(changes, removed, added);
                changes.Add(new LineChange(ClassifyMatch(oldLines[i], newLines[j]), oldLines[i], newLines[j]));
                i++;
                j++;
            }
            else if (j == newLines.Count || (i < oldLines.Count && lengths[i + 1, j] >= lengths[i, j + 1]))
            {
                removed.Add(oldLines[i++]);
            }
            else
            {
                added.Add(newLines[j++]);
            }
        }

        FlushEdits(changes, removed, added);
        return changes;
    }

    private static int[,] BuildLcsTable(IReadOnlyList<TtmlLine> oldLines, IReadOnlyList<TtmlLine> newLines)
    {
        var lengths = new int[oldLines.Count + 1, newLines.Count + 1];
        for (var i = oldLines.Count - 1; i >= 0; i--)
        {
            for (var j = newLines.Count - 1; j >= 0; j--)
            {
                lengths[i, j] = SameText(oldLines[i], newLines[j])
                    ? lengths[i + 1, j + 1] + 1
                    : Math.Max(lengths[i + 1, j], lengths[i, j + 1]);
            }
        }

        return lengths;
    }

    // Lines edited between two matches pair up in order, so a corrected word reads as one
    // changed line and not as a removal plus an addition.
    private static void FlushEdits(List<LineChange> changes, List<TtmlLine> removed, List<TtmlLine> added)
    {
        var paired = Math.Min(removed.Count, added.Count);
        for (var k = 0; k < paired; k++)
        {
            changes.Add(new LineChange(LineChangeKind.TextChanged, removed[k], added[k]));
        }

        changes.AddRange(removed.Skip(paired).Select(l => new LineChange(LineChangeKind.Removed, l, null)));
        changes.AddRange(added.Skip(paired).Select(l => new LineChange(LineChangeKind.Added, null, l)));
        removed.Clear();
        added.Clear();
    }

    private static bool SameText(TtmlLine a, TtmlLine b) => a.Text == b.Text && a.BackgroundText == b.BackgroundText;

    private static LineChangeKind ClassifyMatch(TtmlLine oldLine, TtmlLine newLine)
    {
        if (oldLine.TimingSignature != newLine.TimingSignature)
        {
            return LineChangeKind.TimingChanged;
        }

        return oldLine.Agent != newLine.Agent ? LineChangeKind.VoiceChanged : LineChangeKind.Unchanged;
    }

    private static List<MetadataChange> CompareMetadata(TtmlDocument a, TtmlDocument b)
    {
        var fields = new (string Name, string? Old, string? New)[]
        {
            ("Título", a.Title, b.Title),
            ("Artistas", Join(a.Artists), Join(b.Artists)),
            ("Álbum", a.Album, b.Album),
            ("ISRC", a.Isrc, b.Isrc),
            ("Portada", a.CoverArtUrl, b.CoverArtUrl),
            ("Idioma", a.Language, b.Language),
            ("Compositores", Join(a.Songwriters), Join(b.Songwriters)),
            ("Sync", a.Timing.ToString(), b.Timing.ToString()),
            ("Duración", a.Duration?.ToString(), b.Duration?.ToString()),
        };
        return fields.Where(f => f.Old != f.New).Select(f => new MetadataChange(f.Name, f.Old, f.New)).ToList();
    }

    private static string? Join(IReadOnlyList<string> values) => values.Count == 0 ? null : string.Join(", ", values);
}
