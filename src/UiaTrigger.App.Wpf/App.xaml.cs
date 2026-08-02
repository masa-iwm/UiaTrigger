using System.IO;
using System.Windows;
using UiaTrigger.App.Shared;

namespace UiaTrigger.App.Wpf;

/// <summary>The WPF sample host's application object.</summary>
public partial class App : Application
{
    private static readonly string LogPath = Path.Combine(Path.GetTempPath(), "uiatrigger-app-wpf.log");

    /// <summary>Creates the application and wires up the unhandled-exception logging.</summary>
    public App()
    {
        // カルチャの上書きは**どのウィンドウを作るより前**に行う (docs/DESIGN.md §12)。
        // MRT の言語上書きは WinUI ホストにしか無いので、戻り値は使わない
        HostOptions.Initialize(Log);
        _ = HostOptions.ApplyCulture();

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
