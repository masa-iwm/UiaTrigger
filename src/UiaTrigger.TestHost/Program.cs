// UiaTrigger.Core のテスト用コンソールホスト。
//   monitor [--file <path>]                          : 定義を読み込み監視・発火ログ出力 (既定コマンド)
//   record <id> [--on T] [--prop P] [--op O] [...]   : カーソル下 (または --point 指定座標) の要素から
//                                                      トリガー定義を記録して保存
using System.Globalization;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using UiaTrigger;
using UiaTrigger.Models;
using UiaTrigger.Monitoring;
using UiaTrigger.Persistence;
using UiaTrigger.Serialization;
using UiaTrigger.TestHost;

// record は物理スクリーン座標を UIA へ渡す。DPI 非認識のままだと Windows が座標を仮想化し、
// 狙った要素ではなくその親を静かに記録する (docs/DESIGN.md A19)。
// コンソールアプリなのでウィンドウは無く、ここで宣言してよい。
DpiAwareness.TryEnablePerMonitorV2();

var argList = new List<string>(args);
string command = argList.Count > 0 && !argList[0].StartsWith('-') ? argList[0] : "monitor";
if (argList.Count > 0 && !argList[0].StartsWith('-'))
{
    argList.RemoveAt(0);
}

string? GetOption(string name)
{
    int i = argList.IndexOf(name);
    if (i < 0 || i + 1 >= argList.Count)
    {
        return null;
    }
    return argList[i + 1];
}

string file = GetOption("--file") ?? TriggerFilePath.Default;

// 表示カルチャの上書き。OS の表示言語を変えずにローカライズを検証できるようにする
// (CI では AOT 発行後のバイナリでサテライトが解決されることをこれで確認する)。
if (GetOption("--culture") is { Length: > 0 } cultureName)
{
    // 綴りミスを生のスタックトレースにしない。共有側 (App.Shared の HostOptions) と
    // 同じ規律 — **黙って通常動作に落ちない**が、落とし方は利用ミスとして言う
    try
    {
        var culture = new CultureInfo(cultureName);
        CultureInfo.DefaultThreadCurrentCulture = culture;
        CultureInfo.DefaultThreadCurrentUICulture = culture;
        CultureInfo.CurrentCulture = culture;
        CultureInfo.CurrentUICulture = culture;
    }
    catch (CultureNotFoundException)
    {
        Console.Error.WriteLine($"--culture: '{cultureName}' は既知のカルチャ名ではありません。");
        return 1;
    }
}

// DPI の診断メッセージはローカライズされるので、カルチャを決めた後に出す
if (DpiAwareness.DescribeProblem() is { } dpiProblem)
{
    Console.Error.WriteLine(dpiProblem);
}

// --log を付けると Core の診断ログ (docs/DESIGN.md C9) が stderr に出る。
// 「動かないが理由が分からない」を潰すための出口がここであることを実物で示しておく
ILogger? logger = Enum.TryParse(GetOption("--log"), ignoreCase: true, out LogLevel level)
    ? new StderrLogger(level)
    : null;

return command switch
{
    "record" => await RecordAsync(),
    "monitor" => await MonitorAsync(),
    _ => Usage(),
};

int Usage()
{
    Console.WriteLine("""
        使い方:
          共通オプション: [--culture <name>] [--log <Trace|Debug|Information|Warning|Error>]
                          例: --culture ja-JP / --log Debug (Core の診断ログを stderr へ)

          UiaTrigger.TestHost [monitor] [--file <path>] [--duration <sec>]
          UiaTrigger.TestHost record <id> [--file <path>] [--delay <sec>] [--point <x>,<y>]
              [--on ElementAppeared|ElementRemoved|PropertyChanged|WhileMatching]
              [--prop Name|AutomationId|ClassName|ControlType|BoundingRectangle|AccessKey|
                      AcceleratorKey|HelpText|IsEnabled|IsOffscreen|Value|RangeValue|
                      RangeValueMinimum|RangeValueMaximum]
              [--op Always|Equals|NotEquals|Between|NotBetween|GreaterThan|LessThan|
                    LessOrEqual|GreaterOrEqual|RegexMatch|RegexNotMatch]
              [--value <num>] [--low <num>] [--high <num>] [--text <str>] [--tolerance <num>]
              [--min-interval <sec>] [--poll-interval <sec>] [--view Raw|Control|Content]

          --poll-interval は「対象アプリが UIA の通知を上げないので鳴らない」ときの逃げ道
          (docs/DESIGN.md §5)。既定は無効 = イベント駆動。
          停止時に出る PollCount / PolledReadCount が、その費用と、
          「鳴らなかったのは回っていないからか」の切り分けになる。
        """);
    return 1;
}

async Task<int> RecordAsync()
{
    // record の第 1 引数がトリガー Id
    string? id = argList.Count > 0 && !argList[0].StartsWith('-') ? argList[0] : null;
    if (id is null)
    {
        Console.Error.WriteLine("record にはトリガー Id を指定してください。");
        return Usage();
    }

    var on = ParseEnum("--on", TriggerOn.PropertyChanged);
    var property = ParseEnum("--prop", TriggerProperty.Name);
    var op = ParseEnum("--op", ComparisonOp.Always);
    var view = ParseEnum("--view", TreeViewMode.Control);

    // --point でスクリーン座標を直接指定できる。カーソル操作が要らないのでスクリプトから
    // 記録 → 監視 を回せる (経路記録と経路解決を実 UIA で突き合わせるのに使う)。
    (int X, int Y)? point = ParsePoint("--point");

    // 記録・調査・監視はすべて UiaSession に統合されている (docs/DESIGN.md §3) —
    // 1 セッション = 1 MTA スレッドの上に乗る
    await using var session = new UiaSession(new UiaSessionOptions { Logger = logger });
    TriggerDefinition def;
    if (point is { } p)
    {
        def = await session.BuildDefinitionFromPointAsync(p.X, p.Y, view);
    }
    else
    {
        int delay = int.TryParse(GetOption("--delay"), NumberStyles.Integer, CultureInfo.InvariantCulture, out int d) ? d : 3;
        Console.WriteLine($"{delay} 秒後にマウスカーソル下の要素を記録します。対象の上にカーソルを置いてください...");
        for (int i = delay; i > 0; i--)
        {
            Console.Write($"\r  {i} ");
            await Task.Delay(1000);
        }
        Console.WriteLine();
        def = await session.BuildDefinitionFromCursorAsync(view);
    }

    def.Id = id;
    def.On = on;
    // 出現・削除だけを見るトリガーは句を付けない — ライフサイクル (On) と
    // 述語 (Clauses) は別軸なので、片方だけの指定が成立する
    if (on is TriggerOn.PropertyChanged or TriggerOn.WhileMatching || op != ComparisonOp.Always)
    {
        def.Clauses.Add(new PropertyClause
        {
            Property = property,
            Op = op,
            Text = GetOption("--text"),
            Value = ParseDouble("--value"),
            Low = ParseDouble("--low"),
            High = ParseDouble("--high"),
            Tolerance = ParseDouble("--tolerance") ?? 0,
        });
    }
    if (ParseDouble("--min-interval") is { } seconds)
    {
        def.MinInterval = TimeSpan.FromSeconds(seconds);
    }
    if (ParseDouble("--poll-interval") is { } pollSeconds)
    {
        def.PollInterval = TimeSpan.FromSeconds(pollSeconds);
    }

    List<TriggerDefinition> existing;
    try
    {
        existing = [.. TriggerStore.Load(file).Where(t => !string.Equals(t.Id, id, StringComparison.Ordinal))];
    }
    catch (Exception ex) when (ex is JsonException or NotSupportedException
        or IOException or UnauthorizedAccessException)
    {
        // **既存を読めないまま保存しない。**上書きすると、読めなかっただけの定義が消える
        Console.Error.WriteLine($"既存のトリガー定義を読めないので保存しません ({file}): {ex.Message}");
        return 1;
    }
    existing.Add(def);
    TriggerStore.Save(file, existing);

    Console.WriteLine($"記録しました: id='{id}' → {file}");
    Console.WriteLine(JsonSerializer.Serialize(def, TriggerJsonContext.Default.TriggerDefinition));
    return 0;
}

async Task<int> MonitorAsync()
{
    // 壊れた / 新しい形式のファイルを生のスタックトレースにしない。TriggerStore.Load が
    // 文書化している例外 (JsonException / NotSupportedException) を理由として言う —
    // 後者は「このビルドより新しい形式」であり、利用者が取れる行動が違う
    IReadOnlyList<TriggerDefinition> triggers;
    try
    {
        triggers = TriggerStore.Load(file);
    }
    catch (Exception ex) when (ex is JsonException or NotSupportedException
        or IOException or UnauthorizedAccessException)
    {
        Console.Error.WriteLine($"トリガー定義を読めません ({file}): {ex.Message}");
        return 1;
    }
    if (triggers.Count == 0)
    {
        Console.Error.WriteLine($"トリガー定義がありません: {file}");
        Console.Error.WriteLine("record コマンドで作成してください。");
        return 1;
    }

    Console.WriteLine($"定義 {triggers.Count} 件を読み込みました ({file}):");
    foreach (TriggerDefinition def in triggers)
    {
        string clauses = def.Clauses.Count == 0
            ? "(条件なし)"
            : string.Join($" {def.Combine} ", def.Clauses.Select(c => $"{c.Property} {c.Op}"));
        Console.WriteLine($"  [{def.Id}] {def.DisplayName} — {def.Window.ProcessName} / {def.On} : {clauses}");
    }

    await using var monitor = new TriggerMonitor(new TriggerMonitorOptions
    {
        Session = new UiaSessionOptions { Logger = logger, ThreadName = "UiaTrigger.TestHost" },
    });
    monitor.UnhandledException += ex => Log($"!! ディスパッチャ例外: {ex}");
    monitor.ResolutionChanged += (_, e) =>
        Log($"[{e.TriggerId}] {(e.IsResolved ? "解決" : "未解決")} : {e.Message}");
    monitor.TriggerFired += (_, e) =>
    {
        // OldValue/NewValue は ComparisonString — 条件評価が見ているのと同じ invariant 形である。
        // ここで表示用に整形し直すと「ログの値で条件を書いたのに一致しない」が起きる
        Log($"[{e.TriggerId}] ★発火 {e.On}: '{e.OldValue}' → '{e.NewValue}'");
        if (e.Properties is { } p)
        {
            // 型名は安定名 (ControlTypeName) と表示名 (LocalizedControlType) を併記する
            // (docs/DESIGN.md L6)。条件に書けるのは前者だけなので、後者しか出さないと誤解を招く。
            // 数値も invariant で揃える — この行は条件を組み立てるための資料であって
            // ユーザー向けの表示ではない (docs/LOCALIZATION.md §3 の 3)
            Log(FormattableString.Invariant(
                    $"    Name='{p.Name}' Type={p.ControlTypeName} ({p.LocalizedControlType})") +
                FormattableString.Invariant($" Class='{p.ClassName}' Rect={p.BoundingRectangle}") +
                (p.IsPassword ? " (password — 値は伏せています)" : "") +
                (p.SupportsValuePattern ? $" Value='{p.Value}'" : "") +
                (p.SupportsRangeValuePattern
                    ? FormattableString.Invariant(
                        $" Range={p.RangeValue} [{p.RangeValueMinimum}..{p.RangeValueMaximum}]")
                    : ""));
        }
    };

    try
    {
        await monitor.StartAsync(triggers);
    }
    catch (ArgumentException ex)
    {
        Console.Error.WriteLine($"定義エラー: {ex.Message}");
        return 1;
    }

    // --duration は CI 用。監視の開始まで通ったことを AOT 発行済みバイナリで確かめたいが、
    // Ctrl+C を待つ形だと自動化できない
    double? duration = ParseDouble("--duration");
    // 画面に出す数値は現在のカルチャで書く (docs/DESIGN.md L7)。オプションの解釈
    // (常に Invariant) と表示 (カルチャ依存) は別の規則である — 同じ規則で
    // 扱うと取り違える
    Console.WriteLine(duration is { } seconds
        ? string.Format(CultureInfo.CurrentCulture, "監視中... {0} 秒後に終了します。", seconds)
        : "監視中... Ctrl+C で終了します。");
    var exit = new TaskCompletionSource();
    Console.CancelKeyPress += (_, e) =>
    {
        e.Cancel = true;
        exit.TrySetResult();
    };
    if (duration is { } limit)
    {
        await Task.WhenAny(exit.Task, Task.Delay(TimeSpan.FromSeconds(limit)));
    }
    else
    {
        await exit.Task;
    }
    // 停止前に費用を出す。「鳴らなかった」が「ポーリングが回っていなかった」なのか
    // 「回ったが値が変わらなかった」なのかは、この 2 つでしか切り分けられない
    // (docs/DESIGN.md §5 / docs/MANUAL-CHECKS.md §9)
    TriggerMonitorDiagnostics diagnostics = monitor.GetDiagnostics();
    Console.WriteLine(string.Format(
        CultureInfo.CurrentCulture,
        "診断: 掃引={0} ポーリング周={1} ポーリング読み={2} 解決={3}/{4} 抑制した発火={5}",
        diagnostics.SweepCount, diagnostics.PollCount, diagnostics.PolledReadCount,
        diagnostics.ResolvedElementCount, diagnostics.ElementSlotCount, diagnostics.SuppressedFireCount));

    Console.WriteLine("停止します...");
    return 0;
}

// 発火ログの時刻。書式指定子の ':' と '.' はカルチャで置換されうるので InvariantCulture で固定する
// (docs/DESIGN.md L7 / docs/LOCALIZATION.md §3 の 3)。ログは grep して突き合わせるものなので、
// 実行環境の言語で桁や区切りが変わってはいけない
static void Log(string message)
    => Console.WriteLine(string.Create(CultureInfo.InvariantCulture, $"{DateTime.Now:HH:mm:ss.fff} {message}"));

T ParseEnum<T>(string option, T fallback) where T : struct, Enum
    => Enum.TryParse(GetOption(option), ignoreCase: true, out T parsed) ? parsed : fallback;

double? ParseDouble(string option)
    => double.TryParse(GetOption(option), NumberStyles.Float, CultureInfo.InvariantCulture, out double v) ? v : null;

// "X,Y" 形式。座標は比較・受渡し用の値なので必ず InvariantCulture で解釈する
(int X, int Y)? ParsePoint(string option)
{
    string[] parts = (GetOption(option) ?? string.Empty).Split(',');
    if (parts.Length == 2 &&
        int.TryParse(parts[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out int x) &&
        int.TryParse(parts[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out int y))
    {
        return (x, y);
    }
    return null;
}
