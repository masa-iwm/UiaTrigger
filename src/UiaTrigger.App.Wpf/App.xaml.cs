using System.IO;
using System.Windows;

namespace UiaTrigger.App.Wpf;

/// <summary>The WPF sample host's application object.</summary>
public partial class App : Application
{
    private static readonly string LogPath = Path.Combine(Path.GetTempPath(), "uiatrigger-app-wpf.log");

    /// <summary>Creates the application and wires up the unhandled-exception logging.</summary>
    public App()
    {
        // カルチャの上書きは<b>どのウィンドウを作るより前</b>に行う (docs/DESIGN.md §12)。
        // HostOptions の static プロパティ初期化子は MainWindow から初めて触られたときに
        // 走るので、そこに任せると遅い
        HostOptions.ApplyCulture();

        // 握り潰さずに残す (docs/DESIGN.md D7)。WinUI 版と同じ扱いで、ファイル名だけ分ける —
        // 3 ホストを同時に動かしたときにログが混ざらないようにするため
        DispatcherUnhandledException += (_, e) =>
        {
            Log($"DispatcherUnhandledException: {e.Exception}");
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
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }
}
