// T3 の実 UIA テスト用ターゲットアプリ — WPF 版 (docs/TESTING.md §1 の T3)。
//
// stdin から 1 行 1 コマンドを受け取り、UI スレッド上で実行して結果を stdout に返す。
// 応答は必ず 1 行の "ok <verb>" か "err <理由>" で終わるため、テスト側は
// 「コマンドが UI に反映され終わった」ことを待ち合わせられる (完全に決定論的)。
// 擬似入力 (合成したキー・マウス入力) は一切使わない — docs/TESTING.md §4 の教訓。
// この禁止は CI の lint が grep で強制しているので、API 名を書くこともできない。
//
// WinForms 版との違い:
//   ・DPI 宣言は app.manifest で行う (WPF には SetHighDpiMode 相当の API が無い)
//   ・コマンドを UI スレッドへ渡すための隠しウィンドウは要らない。
//     WPF の Dispatcher はスレッド親和でウィンドウ親和ではないため、
//     recreate-window で対象ウィンドウが入れ替わっても marshalling 先は生き続ける
using System.Windows;

namespace UiaTrigger.TestTarget.Wpf;

internal static class Program
{
    [STAThread]
    private static void Main(string[] args)
    {
        string title = GetOption(args, "--title") ?? "UiaTrigger TestTarget (WPF)";

        var application = new Application
        {
            // 対象ウィンドウを閉じてもプロセスを終わらせない。recreate-window (A9) は
            // 「最後のウィンドウを閉じて作り直す」操作そのものである
            ShutdownMode = ShutdownMode.OnExplicitShutdown,
        };

        var app = new TargetApp(title);
        application.Startup += (_, _) =>
        {
            app.Open();
            var reader = new Thread(() => ReadLoop(application, app))
            {
                IsBackground = true,
                Name = "UiaTrigger.TestTarget.Wpf.stdin",
            };
            reader.Start();
        };

        application.Run();
    }

    private static void ReadLoop(Application application, TargetApp app)
    {
        while (Console.In.ReadLine() is { } line)
        {
            if (line.Length == 0)
            {
                continue;
            }
            // exit だけは応答を書き切ってから終了する。UI スレッド側で Shutdown すると
            // Run が抜けてプロセスが終わり、応答が届かないことがある
            if (string.Equals(line, "exit", StringComparison.Ordinal))
            {
                Write("ok exit\n");
                break;
            }
            string response;
            try
            {
                response = application.Dispatcher.Invoke(() => app.Execute(line));
            }
            catch (Exception ex) when (ex is InvalidOperationException or ObjectDisposedException or TaskCanceledException)
            {
                break;
            }
            Write(response);
        }
        Shutdown(application);
    }

    private static void Write(string text)
    {
        Console.Out.Write(text);
        Console.Out.Flush();
    }

    /// <summary>stdin が閉じられた場合も含め、必ず Dispatcher を畳んで終了する。</summary>
    private static void Shutdown(Application application)
    {
        try
        {
            application.Dispatcher.Invoke(application.Shutdown);
        }
        catch (Exception ex) when (ex is InvalidOperationException or ObjectDisposedException or TaskCanceledException)
        {
        }
    }

    private static string? GetOption(string[] args, string name)
    {
        int i = Array.IndexOf(args, name);
        return i >= 0 && i + 1 < args.Length ? args[i + 1] : null;
    }
}
