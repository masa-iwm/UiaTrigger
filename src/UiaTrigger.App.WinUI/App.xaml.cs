using Microsoft.UI.Xaml;

namespace UiaTrigger.App.WinUI;

public partial class App : Application
{
    private Window? _window;

    private static readonly string LogPath = Path.Combine(Path.GetTempPath(), "uiatrigger-app.log");

    public App()
    {
        // カルチャの上書きは**どのウィンドウを作るより前**に行う (docs/DESIGN.md §12)。
        // MrtPickerStrings.Loader は static readonly Lazy なので、一度でも文字列を読んだら
        // その時点の言語で決着している。HostOptions の static プロパティ初期化子は
        // MainWindow から初めて触られたときに走るため、そこに任せると遅い
        HostOptions.ApplyCulture();
        InitializeComponent();
        UnhandledException += (_, e) =>
        {
            Log($"UnhandledException: {e.Exception}");
            e.Handled = true;
        };
        AppDomain.CurrentDomain.UnhandledException += (_, e) => Log($"AppDomain: {e.ExceptionObject}");
        TaskScheduler.UnobservedTaskException += (_, e) => Log($"UnobservedTask: {e.Exception}");
    }

    internal static void Log(string message)
    {
        try
        {
            File.AppendAllText(LogPath, $"{DateTime.Now:HH:mm:ss.fff} {message}{Environment.NewLine}");
        }
        catch
        {
        }
    }

    protected override void OnLaunched(LaunchActivatedEventArgs args)
    {
        _window = new MainWindow();
        _window.Activate();
    }
}
