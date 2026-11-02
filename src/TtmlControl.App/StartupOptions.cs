namespace TtmlControl.App;

/// <summary>Supports "--data-dir path" and repeated "--root path", for testing against a separate library.</summary>
public sealed record StartupOptions(string DataDirectory, IReadOnlyList<string> Roots)
{
    public static StartupOptions FromCommandLine(string[] args)
    {
        var dataDirectory = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "TtmlControl");
        var roots = new List<string>();

        for (var i = 1; i < args.Length - 1; i += 2)
        {
            switch (args[i])
            {
                case "--data-dir":
                    dataDirectory = args[i + 1];
                    break;
                case "--root":
                    roots.Add(args[i + 1]);
                    break;
            }
        }

        return new StartupOptions(dataDirectory, roots);
    }
}
