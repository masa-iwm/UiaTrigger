using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using System.Runtime.CompilerServices;
using System.Text;
using UiaTrigger.Interop;
using UiaTrigger.Tests;
using Xunit;

namespace UiaTrigger.RealUia.Tests;

/// <summary>
/// テストプロセスの DPI 認識を、**何よりも先に**宣言する。
/// </summary>
/// <remarks>
/// <para>
/// **UIA を一度でも触ると、プロセスの DPI 認識は <c>System</c> に固定される。**
/// 以後 <c>SetProcessDpiAwarenessContext</c> は失敗し、二度と変えられない
/// (実測: 触る前 <c>Unaware</c> → <c>AutomationElement.RootElement</c> を
/// 読んだ直後 <c>System</c> → 宣言を試みても <c>changed=False</c>)。
/// </para>
/// <para>
/// <see cref="TestTargetProcess.RequireCoordinateSafeProcess"/> を「最初のテストが呼ぶ」に
/// 任せると、**UIA を先に触るテストが 1 つでも在れば、それが先に走った実行だけ**
/// 座標を使う全テストが落ちる (座標を使わないテストは宣言を通らずに UIA へ入れるため、
/// xunit の実行順しだいで座標系がまとめて落ちる)。
/// 落ち方が「ハーネスが拒む」なので黙って嘘をつくわけではないが、
/// **実行順に依存する赤**であり、原因が見えない。
/// </para>
/// <para>
/// モジュール初期化子はモジュール内のどのコードよりも先に走るので、
/// 「誰が最初に走るか」に依存しなくなる。<c>RequireCoordinateSafeProcess</c> の
/// assert は**残す** — 宣言が効かない環境 (manifest で別の値が固定されている等) を
/// 見逃さないためである。
/// </para>
/// <para>
/// このファイルは T3 と T4 の両方にリンクされているので、両方のアセンブリが同じ扱いになる。
/// </para>
/// </remarks>
internal static class TestProcessDpi
{
    [ModuleInitializer]
    internal static void DeclareBeforeAnythingTouchesUia() => DpiAwareness.TryEnablePerMonitorV2();
}

/// <summary>
/// テスト対象アプリ (tests/UiaTrigger.TestTarget) の子プロセス。
///
/// UI の変化は **すべて stdin のコマンド** で起こし、応答 (ok/err) を待ってから次へ進む。
/// 擬似入力は使わない — 擬似入力は「入力がどこへ届くか」を検証できず、
/// 誤った結論を導いた実績がある (docs/TESTING.md §4)。
/// </summary>
internal sealed class TestTargetProcess : IDisposable
{
    private static readonly TimeSpan ResponseTimeout = TimeSpan.FromSeconds(20);

    private readonly Process _process;
    private readonly BlockingCollection<string> _output = [];
    private bool _disposed;

    /// <summary>応答をまだ受け取っていないコマンド。<see cref="Post"/> の remarks を参照。</summary>
    private string? _pending;

    /// <summary>ウィンドウタイトル。テストごとに一意なので、これでウィンドウを固定できる。</summary>
    public string Title { get; }

    /// <summary>この対象アプリの種別 (WinForms / WPF)。</summary>
    public TargetProfile Profile { get; }

    /// <summary>
    /// 子プロセスの ID。指した座標がこのプロセスの窓を指しているかの確認に使う (T4)。
    /// </summary>
    public int ProcessId => _process.Id;

    private TestTargetProcess(Process process, string title, TargetProfile profile)
    {
        _process = process;
        Title = title;
        Profile = profile;
    }

    /// <summary>プロファイル名 (<c>[Theory]</c> から渡ってくる文字列) で起動する。</summary>
    public static TestTargetProcess Start(string profileName) => Start(TargetProfile.ByName(profileName));

    public static TestTargetProcess Start() => Start(TargetProfile.WinForms);

    public static TestTargetProcess Start(TargetProfile profile)
    {
        RequireCoordinateSafeProcess();
        // タイトルはテストごとに一意にする。並行して残っている対象プロセスを
        // 取り違えると、通ったのか通っていないのか分からないテストになる
        string title = "UiaTriggerTestTarget-" + Guid.NewGuid().ToString("N", CultureInfo.InvariantCulture)[..12];
        var info = new ProcessStartInfo(ExecutablePathFor(profile))
        {
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            UseShellExecute = false,
            // **対象アプリはコンソールサブシステム (Exe) である。**手で起動できるようにするため
            // WinExe をやめた (csproj のコメントを参照)。その代償として、これを付けないと
            // **対象プロセスごとにコンソール窓が 1 枚デスクトップに増える**。
            // T3 も T4 も座標 (ElementFromPoint) を通るので、余分な最前面窓は
            // 「記録したつもりが別の窓を掴む」を間欠的に起こす。窓は出さずパイプだけ繋ぐ
            CreateNoWindow = true,
            // BOM を書き込むエンコーディングだと最初のコマンド行が壊れる
            StandardInputEncoding = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false),
            StandardOutputEncoding = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false),
        };
        info.ArgumentList.Add("--title");
        info.ArgumentList.Add(title);

        Process process = Process.Start(info)
            ?? throw new InvalidOperationException($"起動できませんでした: {ExecutablePathFor(profile)}");
        var target = new TestTargetProcess(process, title, profile);

        var reader = new Thread(target.ReadLoop) { IsBackground = true, Name = "TestTarget.stdout" };
        reader.Start();

        // メッセージループとウィンドウが立ち上がるまでの待ち合わせを兼ねる
        target.Send("ping");
        return target;
    }

    /// <summary>
    /// 1 コマンドを送り、応答の data 行を返す。err なら例外。
    /// 戻った時点で UI への反映は完了している (UIA イベントの配送はこの限りではない)。
    /// </summary>
    public IReadOnlyList<string> Send(string command)
    {
        Post(command);
        return AwaitResponse();
    }

    /// <summary>
    /// 応答を待たずに 1 コマンドを送る。**塞がれたアプリへ送るための口**である。
    /// </summary>
    /// <remarks>
    /// <para>
    /// <c>hang</c> 中のアプリは stdin を読んだ先で UI スレッドを待つので、応答は
    /// 塞ぎが明けるまで返らない。<see cref="Send"/> だとそこで待ち切ってしまい、
    /// **塞いでいるあいだに別のことをする**という観測ができない。
    /// </para>
    /// <para>
    /// これが要るのは検証の順序のためである。「塞がれていた」ことを塞ぎの**後**に
    /// 確かめても、観測したかった窓のあいだ塞がっていた証拠にはならない。
    /// 先にここで送っておき、観測を挟んでから <see cref="AwaitResponse"/> で受けると、
    /// 「送ってから受けるまで返らなかった」= その窓のあいだ塞がっていた、が言える。
    /// </para>
    /// <para>
    /// **未受け取りの応答を 1 つまでしか許さない。**2 つ送ると、次の
    /// <see cref="Send"/> が**前のコマンドの終了行を自分の応答として読む** —
    /// 例外は出ず、以後すべての応答が 1 つずつずれる。静かに嘘をつく壊れ方なので、
    /// ここで落とす。
    /// </para>
    /// </remarks>
    public void Post(string command)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_pending is not null)
        {
            throw new InvalidOperationException(
                $"'{_pending}' の応答をまだ受け取っていません ('{command}' を送ろうとしました)。" +
                "未受け取りの応答が 2 つあると、以後の応答が 1 つずつずれます。");
        }
        _pending = command;
        _process.StandardInput.WriteLine(command);
        _process.StandardInput.Flush();
    }

    /// <summary><see cref="Post"/> で送ったコマンドの応答を待ち、data 行を返す。</summary>
    public IReadOnlyList<string> AwaitResponse()
    {
        string command = _pending
            ?? throw new InvalidOperationException("送っていないコマンドの応答は待てません。");
        try
        {
            var data = new List<string>();
            while (true)
            {
                if (!_output.TryTake(out string? line, ResponseTimeout))
                {
                    throw new TimeoutException($"対象アプリが応答しません: '{command}'");
                }
                if (line.StartsWith("data ", StringComparison.Ordinal))
                {
                    data.Add(line["data ".Length..]);
                    continue;
                }
                if (line.StartsWith("err ", StringComparison.Ordinal))
                {
                    throw new InvalidOperationException($"対象アプリがコマンドを拒否しました: '{command}' → {line}");
                }
                return data;
            }
        }
        finally
        {
            _pending = null;
        }
    }

    /// <summary>
    /// UI スレッドを <paramref name="duration"/> だけ塞ぐ。**応答は先に返る**ので、
    /// この呼び出し自体は待たない (tests/UiaTrigger.TestTarget/TargetApp.cs の <c>hang</c>)。
    /// </summary>
    public void Hang(TimeSpan duration) => Send(string.Create(
        CultureInfo.InvariantCulture, $"hang {(int)duration.TotalMilliseconds}"));

    /// <summary>
    /// コントロールが自己申告する画面上の矩形 (物理座標)。
    /// これが UIA の <c>BoundingRectangle</c> と一致することがハーネスの前提であり、
    /// ずれていれば座標からの記録は**例外を出さずに別の要素を掴む** (docs/DESIGN.md A19)。
    /// </summary>
    public (int Left, int Top, int Right, int Bottom) RectOf(string controlName)
    {
        string line = Send("rect " + controlName).Single();
        string[] parts = line.Split(' ');
        Assert.Equal("rect", parts[0]);
        int Value(int index) => int.Parse(parts[index], CultureInfo.InvariantCulture);
        return (Value(1), Value(2), Value(3), Value(4));
    }

    /// <summary>コントロールの画面上の中心 (物理座標)。座標記録の入力に使う。</summary>
    public (int X, int Y) CenterOf(string controlName)
    {
        (int left, int top, int right, int bottom) = RectOf(controlName);
        return ((left + right) / 2, (top + bottom) / 2);
    }

    /// <summary>
    /// <c>TextBox</c> のキャレット位置。**UIA を通さずに読む** (T5 の K2 用)。
    /// </summary>
    /// <remarks>
    /// ←/→ が「ピッカーだけでなく対象アプリにも普通に届く」ことの観測点である
    /// (MANUAL-CHECKS §2)。フックはキーを吸収せず <c>CallNextHookEx</c> で
    /// 流すので、届いていればキャレットが 1 つ動く。
    /// </remarks>
    public int CaretOf(string controlName)
        => int.Parse(Send("caret " + controlName).Single()["caret ".Length..], CultureInfo.InvariantCulture);

    /// <summary>
    /// そのコントロールが受け取ったクリックの数 (T5 の M1 用)。
    /// </summary>
    public int ClicksOn(string controlName)
        => int.Parse(Send("clicks " + controlName).Single()["clicks ".Length..], CultureInfo.InvariantCulture);

    /// <summary>
    /// コントロールにキーボードフォーカスを当て、**当たったかどうか**を返す。
    /// </summary>
    /// <remarks>
    /// **true が返っても、それだけを信じてはいけない。**フォアグラウンドロックの規則により
    /// 背景のプロセスは自分を前面へ出せないので、ここは「アプリの中では選択されている」しか
    /// 言っていない。撃つ前の検算は UIA の <c>HasKeyboardFocus</c> で行う
    /// (docs/TESTING.md §3 の解禁条件 1)。
    /// </remarks>
    public bool TryFocus(string controlName)
        => Send("focus " + controlName).Single()["focus ".Length..] == "1";

    public long WindowHandle()
        => long.Parse(Send("hwnd").Single()["hwnd ".Length..], CultureInfo.InvariantCulture);

    /// <summary>ウィンドウを閉じて作り直し、新しい HWND を返す (docs/DESIGN.md A9)。</summary>
    public long RecreateWindow()
        => long.Parse(Send("recreate-window").Single()["hwnd ".Length..], CultureInfo.InvariantCulture);

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }
        _disposed = true;
        try
        {
            _process.StandardInput.WriteLine("exit");
            _process.StandardInput.Flush();
            if (!_process.WaitForExit(5000))
            {
                _process.Kill(entireProcessTree: true);
            }
        }
        catch (Exception ex) when (ex is IOException or InvalidOperationException or ObjectDisposedException)
        {
        }
        _process.Dispose();
        _output.Dispose();
    }

    private void ReadLoop()
    {
        try
        {
            while (_process.StandardOutput.ReadLine() is { } line)
            {
                _output.Add(line);
            }
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException or InvalidOperationException)
        {
        }
    }

    /// <summary>
    /// 対象アプリの実行ファイル。テストと同じ構成 (Debug/Release) の出力を優先する。
    /// </summary>
    private static string ExecutablePathFor(TargetProfile profile)
        => ExecutablePaths.GetOrAdd(profile, FindExecutable);

    private static readonly ConcurrentDictionary<TargetProfile, string> ExecutablePaths = new();

    private static string FindExecutable(TargetProfile profile)
    {
        string binDir = RepoPaths.Combine("tests", profile.ProjectDirectory, "bin");
        if (!Directory.Exists(binDir))
        {
            throw new InvalidOperationException(
                $"{profile.ProjectDirectory} がビルドされていません ({binDir} がありません)。");
        }

        // AppContext.BaseDirectory が "...\bin\Release\net10.0-windows\" のような形なので、
        // そこから構成名を取り出して同じものを選ぶ
        string configuration = AppContext.BaseDirectory.Contains(
            Path.Combine("bin", "Debug"), StringComparison.OrdinalIgnoreCase) ? "Debug" : "Release";

        string[] candidates = [.. Directory.EnumerateFiles(binDir, profile.ExecutableName, SearchOption.AllDirectories)];
        string? match = Array.Find(candidates,
            p => p.Contains(Path.Combine("bin", configuration), StringComparison.OrdinalIgnoreCase));

        return match ?? (candidates.Length > 0
            ? candidates[0]
            : throw new InvalidOperationException($"{profile.ExecutableName} が見つかりません: {binDir}"));
    }

    /// <summary>
    /// テストプロセスを PerMonitorV2 にする。
    ///
    /// DPI 非認識のままだと Windows が座標を仮想化するため、UIA に渡した座標が
    /// 狙った要素の外を指し、**例外にならずに別の要素を記録する** (docs/DESIGN.md A19)。
    /// これはハーネスが静かに嘘をつく壊れ方なので、成立していることを明示的に確かめる。
    /// </summary>
    public static void RequireCoordinateSafeProcess()
    {
        DpiAwareness.TryEnablePerMonitorV2();
        Assert.True(
            DpiAwareness.IsCoordinateSafe(),
            "テストプロセスを PerMonitorV2 にできませんでした。" +
            "この状態では座標から記録した定義が別の要素を指すため、T3 の結果は信用できません: " +
            DpiAwareness.DescribeProblem());
    }
}
