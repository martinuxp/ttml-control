using Microsoft.UI.Xaml;
using TtmlControl.Core.Library;

namespace TtmlControl.App;

public partial class App : Application
{
    private Window? _window;

    public App()
    {
        InitializeComponent();
    }

    protected override void OnLaunched(LaunchActivatedEventArgs args)
    {
        var options = StartupOptions.FromCommandLine(Environment.GetCommandLineArgs());
        Directory.CreateDirectory(options.DataDirectory);
        UnhandledException += (_, e) => File.AppendAllText(
            Path.Combine(options.DataDirectory, "crash.log"),
            $"{DateTime.Now:O}\n{e.Exception}\n\n");
        var database = new LibraryDatabase(Path.Combine(options.DataDirectory, "library.db"));
        foreach (var root in options.Roots)
        {
            database.AddRoot(root);
        }

        _window = new MainWindow(new MainViewModel(database));
        _window.Activate();
    }
}
