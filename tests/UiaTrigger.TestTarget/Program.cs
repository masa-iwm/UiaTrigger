// T3 の実 UIA テスト用ターゲットアプリ (docs/TESTING.md §1 の T3)。
//
// stdin から 1 行 1 コマンドを受け取り、UI スレッド上で実行して結果を stdout に返す。
// 応答は必ず 1 行の "ok <verb>" か "err <理由>" で終わるため、テスト側は
// 「コマンドが UI に反映され終わった」ことを待ち合わせられる (完全に決定論的)。
// 擬似入力 (合成したキー・マウス入力) は一切使わない — docs/TESTING.md §4 の教訓。
// この禁止は CI の lint が grep で強制しているので、API 名を書くこともできない。
namespace UiaTrigger.TestTarget;

internal static class Program
{
    [STAThread]
    private static void Main(string[] args)
    {
        // DPI 非認識だと UIA の BoundingRectangle (物理座標) と Control.RectangleToScreen が
        // 食い違い、座標から記録したつもりの要素が別物になる (docs/TESTING.md §4 と同じ壊れ方)。
        // WinForms は manifest ではなくこの API での設定を要求する (WFO0003)。
        Application.SetHighDpiMode(HighDpiMode.PerMonitorV2);
        Application.EnableVisualStyles();
        Application.SetCompatibleTextRenderingDefault(false);

        string title = GetOption(args, "--title") ?? "UiaTrigger TestTarget";

        // コマンドを UI スレッドへ渡すためだけの不可視ウィンドウ。
        // 対象ウィンドウは recreate-window で作り直されるため、常に生きている
        // marshalling 先が別に要る。Show しないので可視ウィンドウの列挙には現れない。
        using var pump = new Form();
        _ = pump.Handle;

        var app = new TargetApp(title);
        app.Open();

        var reader = new Thread(() => ReadLoop(pump, app))
        {
            IsBackground = true,
            Name = "UiaTrigger.TestTarget.stdin",
        };
        reader.Start();

        // MainForm を持たせない。対象ウィンドウを閉じてもメッセージループは止まらず、
        // exit コマンド (Application.ExitThread) でだけ終了する。
        Application.Run(new ApplicationContext());
    }

    private static void ReadLoop(Form pump, TargetApp app)
    {
        while (Console.In.ReadLine() is { } line)
        {
            if (line.Length == 0)
            {
                continue;
            }
            // exit だけは応答を書き切ってから終了する。UI スレッド側で ExitThread すると
            // Application.Run が抜けてプロセスが終わり、応答が届かないことがある
            if (string.Equals(line, "exit", StringComparison.Ordinal))
            {
                Write("ok exit\n");
                break;
            }
            string response;
            try
            {
                response = (string)pump.Invoke(() => app.Execute(line));
            }
            catch (Exception ex) when (ex is InvalidOperationException or ObjectDisposedException)
            {
                break;
            }
            Write(response);
        }
        Shutdown(pump);
    }

    private static void Write(string text)
    {
        Console.Out.Write(text);
        Console.Out.Flush();
    }

    /// <summary>stdin が閉じられた場合も含め、必ずメッセージループを畳んで終了する。</summary>
    private static void Shutdown(Form pump)
    {
        try
        {
            pump.Invoke(Application.ExitThread);
        }
        catch (Exception ex) when (ex is InvalidOperationException or ObjectDisposedException)
        {
        }
    }

    private static string? GetOption(string[] args, string name)
    {
        int i = Array.IndexOf(args, name);
        return i >= 0 && i + 1 < args.Length ? args[i + 1] : null;
    }
}
