namespace TtmlControl.Core.Library;

public static class LibraryPaths
{
    public static bool IsUnder(string path, string folder)
    {
        // A drive root such as "D:\" already ends in a separator.
        var prefix = Path.EndsInDirectorySeparator(folder) ? folder : folder + Path.DirectorySeparatorChar;
        return path.StartsWith(prefix, StringComparison.OrdinalIgnoreCase);
    }
}
