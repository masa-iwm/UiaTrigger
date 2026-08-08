using UiaTrigger.App.Shared;

namespace UiaTrigger.App.WinForms;

internal static class Program
{
    private static readonly string LogPath =
        Path.Combine(Path.GetTempPath(), "uiatrigger-app-winforms.log");

    [STAThread]
    private static void Main()
    {
        // 例外は握り潰さずに残す (docs/DESIGN.md D7)。3 ホストを同時に動かしたときに
        // ログが混ざらないよう、ファイル名だけ変えてある
        Application.ThreadException += (_, e) => Log($"ThreadException: {e.Exception}");
        AppDomain.CurrentDomain.UnhandledException += (_, e) => Log($"AppDomain: {e.ExceptionObject}");
        TaskScheduler.UnobservedTaskException += (_, e) => Log($"UnobservedTask: {e.Exception}");

        // DPI 認識は**ウィンドウを 1 つも作る前に**宣言する (docs/DESIGN.md A19 / docs/TESTING.md §4)。
        // 非認識のままだと Windows が座標を仮想化し、ピッカーがカーソル位置から掴む要素が
        // 狙ったものとずれる。例外にはならず「静かに別の要素を記録した定義」ができる。
        //
        // Windows Forms では manifest に書けない (アナライザー WFO0003 がエラーにする) ので、
        // T3 の対象アプリと同じくここで宣言する。ApplicationConfiguration.Initialize より
        // 先に呼ぶこと — あちらは既定値で SetHighDpiMode を呼んでしまう
        Application.SetHighDpiMode(HighDpiMode.PerMonitorV2);
        ApplicationConfiguration.Initialize();

        // カルチャの上書きは**ウィンドウを 1 つも作る前**に行う (docs/DESIGN.md §12)。
        // MRT の言語上書きは WinUI ホストにしか無いので、戻り値は使わない
        HostOptions.Initialize(Log);
        _ = HostOptions.ApplyCulture();

        Application.Run(new MainForm());
    }

    /// <summary>
    /// 診断ログ。時刻の書式は App.Shared が invariant で握る (docs/DESIGN.md L7)。
    /// </summary>
    internal static void Log(string message) => HostLog.Append(LogPath, message);
}
