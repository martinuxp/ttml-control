using System.Text.RegularExpressions;

namespace TtmlControl.Core.Ttml;

/// <summary>
/// Pieces of a file name, used only when the TTML has no metadata. Names appear both as
/// "Artist - Title" and "Title - Artist", so the parts are not labelled.
/// </summary>
public sealed partial record FileNameHints(string CleanName, IReadOnlyList<string> Parts)
{
    public static FileNameHints FromPath(string path)
    {
        var name = CopySuffix().Replace(Path.GetFileNameWithoutExtension(path), "").Trim();
        var parts = name.Split(" - ", StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        return new FileNameHints(name, parts);
    }

    [GeneratedRegex(@"\s*\(\d+\)$")]
    private static partial Regex CopySuffix();
}
