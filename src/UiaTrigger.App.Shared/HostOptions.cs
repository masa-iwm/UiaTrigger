// サンプルホスト 3 つが共有する、コマンドラインの読み取り (docs/DESIGN.md §12)。
//
// 読むのは 4 つ:
//   --pick-at x,y            ピッカーに「カーソルはここに在る」と思わせる。繰り返せる
//   --culture <name>         表示カルチャを上書きする
//   --triggers <path>        トリガーの保存先を差し替える
//   --place-windows L,T,W,H  自分が出す窓をこの矩形へ置き続ける (HostWindowPlacer)
//
// なぜ --pick-at がホスト側に要るのか — ピッカーの主要な操作はホバー滞留であり、捕捉が
// 起きなければツリーには何も出ない。T4 (tests/UiaTrigger.Picker.UiTests) が実体化待ちの
// 経路を確かめるには、まず捕捉を起こす必要がある。実際のマウスを動かすのは擬似入力であり、
// このリポジトリのテストでは禁止されている (docs/TESTING.md §4)。
//
// 差し替えているのは入力**イベント**ではなくカーソルの**取得元**である。
// 入力経路そのものは検証対象ではない (滞留の算術は T1 が見ている) ので、
// docs/TESTING.md §4 の教訓に反しない。
//
// 引数は Environment.GetCommandLineArgs() から読む。3 ホストとも生成された
// エントリーポイントを使っており (WinUI3 は生成、WPF は App.xaml の StartupUri、
// WinForms だけは Main を書いているが揃えてある)、args を受け取る口が無いためである。
//
// ■ 3 ホストの写しをここへ畳んだ判断について
//
// src/UiaTrigger.App.* は TriggerFilePath.cs / AppStrings.cs / MainWindow の大半を
// **意図的に写している** — 「同じライブラリが 3 つの UI スタックで動く」ことを
// 見せるのがあのディレクトリの目的だからである。HostOptions だけを引き出したのは、
// **読み取るオプションが 4 つになった**ためで、写しを保つ判断そのものが
// 「4 つ目が要るときは共有プロジェクトへ移す」と書き残されていた。
//
// 写しが同一でなかった点は 3 つあり、それぞれこう扱う:
//   ・名前空間  → UiaTrigger.App.Shared に一本化する
//   ・ログの出口 (App.Log / Program.Log) → Initialize で Action<string> を受ける
//   ・WinUI だけの MRT 言語上書き → ApplyCulture が適用した名前を返し、WinUI 側で行う
//
// ソースのリンク共有 (<Compile Include="..\Shared\HostOptions.cs" />) には戻さないこと。
// あれは WinUI3 / x64 でプロジェクト参照ができない場合の回避としてだけ許している。
using System.Globalization;
using UiaTrigger.Picker;

namespace UiaTrigger.App.Shared;

/// <summary>ホストが受け取ったオプション。<see cref="Initialize"/> が唯一の入口である。</summary>
internal static class HostOptions
{
    private static HostCommandLine? _parsed;
    private static Action<string>? _log;
    private static int _pickersOpened;

    /// <summary>
    /// コマンドラインを解析して控える。**どのウィンドウを作るより前に、1 度だけ呼ぶ。**
    /// </summary>
    /// <param name="log">解釈できなかった値を書き出す先 (ホストの未処理例外ログ)。</param>
    /// <remarks>
    /// <para>
    /// **呼び忘れは静かな既定値ではなく例外にする。**共有版を作ると
    /// 「<c>Initialize()</c> の呼び忘れ」という新しい失敗形が生まれる、というのが
    /// 写しを保っていた側の言い分だった。既定値に落ちる形にすると、
    /// <c>--triggers</c> が効かないまま**開発機の実ファイルを書き換える**ところまで
    /// 素通りする。落とすほうが安い。
    /// </para>
    /// <para>
    /// 呼ぶ順序そのものは <c>HostOptionsTests.EveryHostInitializesBeforeItCreatesAWindow</c>
    /// が 3 ホストのソースに対して固定している。
    /// </para>
    /// </remarks>
    public static void Initialize(Action<string> log)
    {
        ArgumentNullException.ThrowIfNull(log);
        _log = log;
        _parsed = HostCommandLine.Parse(Environment.GetCommandLineArgs(), log);

        // --place-windows は**ウィンドウを 1 つも作る前**に効かせる必要がある。
        // フックは「これから起きること」しか教えないので、あとから張ると
        // 既に出ている窓を取りこぼす (HostWindowPlacer.Start の remarks)
        if (_parsed.Placement is { } placement)
        {
            HostWindowPlacer.Start(placement, log);
        }
    }

    private static HostCommandLine Current => _parsed ?? throw NotInitialized();

    private static Action<string> Log => _log ?? throw NotInitialized();

    // プログラマ向けの契約違反なので英語で書く (docs/LOCALIZATION.md §3)。
    // 利用者の画面には出ない — 出るとしたらホストの実装が間違っている
    private static InvalidOperationException NotInitialized() => new(
        "HostOptions was used before HostOptions.Initialize was called. " +
        "Call it from the host entry point, before creating any window.");

    /// <summary>
    /// <c>--pick-at x,y</c> で指定された固定カーソルの列。指定が無ければ空。
    /// </summary>
    public static IReadOnlyList<ICursorSource> Cursors => Current.Cursors;

    /// <summary>
    /// <c>--triggers &lt;path&gt;</c> で指定されたトリガーの保存先。無指定なら null。
    /// </summary>
    public static string? TriggerFile => Current.TriggerFile;

    /// <summary>
    /// 次に開くピッカーへ渡すカーソル。<c>--pick-at</c> が無ければ null。
    /// </summary>
    /// <remarks>
    /// <para>
    /// **n 枚目のピッカーが n 番目の座標**を受け取る。足りなければ最後を使い回すので、
    /// <c>--pick-at</c> を 1 つだけ渡す従来の使い方は何枚開いても同じ座標になる。
    /// </para>
    /// <para>
    /// **「n 枚目」はこのメソッドを呼んだ回数である。**「ピッカーで追加」は既に開いていれば
    /// 前面に出すだけなので、2 枚目を開くには「もう 1 つ開く」を押す必要がある。
    /// S1 (2 枚がそれぞれ独立に追従すること) の検出力はここに依存する — 2 枚が同じ座標を
    /// 受け取ると、オーバーレイを static singleton へ戻す退行が「枠が一致する」で素通りする。
    /// </para>
    /// </remarks>
    public static ICursorSource? NextCursor()
    {
        IReadOnlyList<ICursorSource> cursors = Current.Cursors;
        if (cursors.Count == 0)
        {
            return null;
        }
        int index = _pickersOpened++;
        return cursors[Math.Min(index, cursors.Count - 1)];
    }

    /// <summary>
    /// <c>--culture &lt;name&gt;</c> を表示カルチャへ反映する。
    /// </summary>
    /// <returns>
    /// 適用したカルチャ名。<c>--culture</c> が無い / 解釈できなかったときは null。
    /// </returns>
    /// <remarks>
    /// <para>
    /// **ウィンドウを 1 つも作る前に呼ぶこと。**WPF / WinForms の
    /// <c>ResxPickerStrings</c> は <c>CurrentUICulture</c> を追うので、これで切り替わる。
    /// </para>
    /// <para>
    /// **WinUI の MRT はこれだけでは切り替わらない。**あちらは
    /// <c>ApplicationLanguages.PrimaryLanguageOverride</c> も要るが、それは
    /// Windows App SDK に依存するのでここには置けない。**戻り値を返すのはそのためである** —
    /// WinUI ホストが受け取って自分で立てる。<c>MrtPickerStrings.Loader</c> は
    /// <c>static readonly Lazy</c> であり、**一度でも文字列を読んだら決着している**ので、
    /// 上書きは <c>App</c> のコンストラクター (どのウィンドウを作る前) で行う必要がある。
    /// 実測の結果は docs/DESIGN.md §12 にある。
    /// </para>
    /// </remarks>
    public static string? ApplyCulture()
    {
        if (Current.Culture is not { Length: > 0 } name)
        {
            return null;
        }

        try
        {
            var culture = new CultureInfo(name);
            CultureInfo.DefaultThreadCurrentCulture = culture;
            CultureInfo.DefaultThreadCurrentUICulture = culture;
            CultureInfo.CurrentCulture = culture;
            CultureInfo.CurrentUICulture = culture;
        }
        catch (CultureNotFoundException)
        {
            Log($"--culture: '{name}' is not a known culture name.");
            return null;
        }

        return name;
    }
}
