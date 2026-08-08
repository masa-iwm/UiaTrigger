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

    /// <summary>
    /// 診断ログ。時刻の書式は App.Shared が invariant で握る (docs/DESIGN.md L7) —
    /// 3 変種で書くと必ずずれ、ログを突き合わせる側が困る。
    /// </summary>
    internal static void Log(string message) => HostLog.Append(LogPath, message);
}
