using Microsoft.UI.Xaml;
using Microsoft.Windows.Globalization;
using UiaTrigger.App.Shared;

namespace UiaTrigger.App.WinUI;

public partial class App : Application
{
    private Window? _window;

    private static readonly string LogPath = Path.Combine(Path.GetTempPath(), "uiatrigger-app.log");

    public App()
    {
        // カルチャの上書きは**どのウィンドウを作るより前**に行う (docs/DESIGN.md §12)。
        // MrtPickerStrings.Loader は static readonly Lazy なので、一度でも文字列を読んだら
        // その時点の言語で決着している
        HostOptions.Initialize(Log);
        ApplyCulture();
        InitializeComponent();
        UnhandledException += (_, e) =>
        {
            Log($"UnhandledException: {e.Exception}");
            e.Handled = true;
        };
        AppDomain.CurrentDomain.UnhandledException += (_, e) => Log($"AppDomain: {e.ExceptionObject}");
        TaskScheduler.UnobservedTaskException += (_, e) => Log($"UnobservedTask: {e.Exception}");
    }

    /// <summary>
    /// <c>--culture</c> を反映する。**MRT の言語上書きはこのホストにしか無い。**
    /// </summary>
    /// <remarks>
    /// <c>CurrentUICulture</c> までは共有の <see cref="HostOptions.ApplyCulture"/> が行うが、
    /// WinUI の MRT はそれだけでは切り替わらないので <c>PrimaryLanguageOverride</c> も立てる。
    /// 共有側に置けないのは Windows App SDK に依存するためで、
    /// だから <c>ApplyCulture</c> は適用した名前を返すようになっている (docs/DESIGN.md §12)。
    /// </remarks>
    private static void ApplyCulture()
    {
        if (HostOptions.ApplyCulture() is not { } name)
        {
            return;
        }

        try
        {
            ApplicationLanguages.PrimaryLanguageOverride = name;
        }
        catch (Exception ex)
        {
            // アンパッケージのホストで効くかは環境依存である。効かなくても
            // resx 側 (メインウィンドウ) は切り替わるので、ここで落とす価値は無い
            Log($"PrimaryLanguageOverride を設定できませんでした: {ex}");
        }
    }

    /// <summary>
    /// 診断ログ。時刻の書式は App.Shared が invariant で握る (docs/DESIGN.md L7)。
    /// </summary>
    internal static void Log(string message) => HostLog.Append(LogPath, message);

    protected override void OnLaunched(LaunchActivatedEventArgs args)
    {
        _window = new MainWindow();
        _window.Activate();
    }
}
