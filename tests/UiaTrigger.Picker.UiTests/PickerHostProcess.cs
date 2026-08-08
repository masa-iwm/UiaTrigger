// サンプルホストの子プロセスと、その中のピッカーウィンドウ (docs/TESTING.md §1 T4)。
//
// **このファイル自身は入力を 1 つも起こさない。**駆動は UIA のコントロールパターンだけで行う。
// T5 (tests/UiaTrigger.Input.Tests) はこれをリンク共有したうえで、舞台が整ってから
// 検査対象そのもの (キー・クリック) だけを最下層から撃つ — 撃つ口はあちらにしか無い
// (docs/TESTING.md §3)。
//
// ホバー捕捉は T4 でも T5 でも --pick-at / FixedCursorSource のままである。
// 差し替えているのは入力**イベント**ではなくカーソルの**取得元** (ICursorSource) であり、
// 入力経路そのものは検証対象ではない (滞留の算術は T1 の TriggerPickerPresenterTests が見ている)。
// 実カーソルを動かして捕捉させないのは、座標の円環が戻るからである (docs/TESTING.md §4)。
using System.Diagnostics;
using System.Globalization;
using System.Windows.Automation;
using UiaTrigger.Interop;
using UiaTrigger.Models;
using UiaTrigger.Persistence;
using UiaTrigger.Tests;
using Xunit;

namespace UiaTrigger.Picker.UiTests;

internal sealed class PickerHostProcess : IDisposable
{
    private static readonly TimeSpan WindowTimeout = TimeSpan.FromSeconds(30);

    private readonly Process _process;
    private readonly string _logPath;
    private readonly IReadOnlyList<System.Windows.Point> _pickPoints;
    private bool _disposed;

    /// <summary>
    /// このホストが読み書きするトリガーファイル。**ホストごとに別の一時ファイル**である。
    /// </summary>
    /// <remarks>
    /// <para>
    /// <c>--triggers</c> を**どの起動口からも必ず渡す**。既定は
    /// <c>%LOCALAPPDATA%\UiaTrigger\triggers.json</c> という実ファイルなので、
    /// 省くと**自動テストが開発機のトリガーを書き換える** (docs/DESIGN.md §12)。
    /// </para>
    /// <para>
    /// 起動口ごとに渡すかどうかを決める形にはしない。「このテストはコミットしないから要らない」は
    /// 今日は正しくても、そのテストに 1 行足した瞬間に嘘になる。
    /// </para>
    /// </remarks>
    public string TriggerFile { get; }

    public PickerHostProfile Profile { get; }

    /// <summary>ホストのメインウィンドウ。所有関係に頼らず HWND から掴む。</summary>
    public AutomationElement MainWindow { get; }

    /// <summary>
    /// ホストのプロセス ID。オーバーレイのウィンドウを**外から**数えるのに要る
    /// (docs/DESIGN.md §10)。
    /// </summary>
    /// <remarks>
    /// オーバーレイはトップレベルウィンドウだが、ピッカーのウィンドウの子ではない。
    /// 所有関係を辿るのではなく、pid + クラス名で絞るのが唯一の見つけ方である。
    /// </remarks>
    public int ProcessId => _process.Id;

    /// <summary>
    /// <see cref="Dispose"/> で、ホストが**自分で**終了したか (Kill せずに済んだか)。
    /// </summary>
    /// <remarks>
    /// **「閉じたらプロセスが残らない」を見るテストの観測点はここである。**
    /// Dispose から戻った時点のプロセスの生死を見る形は、Kill が先回りするので
    /// 退行を入れても必ず緑になる (docs/TESTING.md §2 — 検出力を示せないテストは書かない)。
    /// 監視のスレッドが終了を妨げていれば、CloseMainWindow のあと 5 秒で終わらず false になる。
    /// </remarks>
    public bool ExitedGracefully { get; private set; }

    private PickerHostProcess(
        Process process,
        PickerHostProfile profile,
        AutomationElement mainWindow,
        string logPath,
        string triggerFile,
        IReadOnlyList<System.Windows.Point> pickPoints,
        (int Left, int Top, int Width, int Height) hostRect)
    {
        _process = process;
        Profile = profile;
        MainWindow = mainWindow;
        _logPath = logPath;
        TriggerFile = triggerFile;
        _pickPoints = pickPoints;
        _hostRect = hostRect;
    }

    /// <summary>ホストの窓を置く矩形。既定は <see cref="DesktopLayout.Host"/>。</summary>
    /// <remarks>
    /// <para>
    /// 差し替えられるようにしてあるのは、**表示域の広さそのものが検査対象**になる
    /// テストがあるためである (<see cref="DesktopLayout.NarrowHost"/>)。
    /// </para>
    /// <para>
    /// **置くのはホスト自身である。**この矩形は <c>--place-windows</c> で起動時に渡してあり、
    /// ここに残っているのは「置き終えたか」を外から見るため (<see cref="IsPlaced"/>) だけである。
    /// </para>
    /// </remarks>
    private readonly (int Left, int Top, int Width, int Height) _hostRect;

    /// <summary>このホストが保存したトリガー。1 件も無ければ空。</summary>
    public IReadOnlyList<TriggerDefinition> SavedTriggers() => TriggerStore.Load(TriggerFile);

    /// <summary>
    /// ホストを起動し、メインウィンドウが出るまで待つ。
    /// </summary>
    /// <param name="profile">起動するホストの種別。</param>
    /// <param name="pickX">ピッカーに「カーソルはここに在る」と思わせる X (物理座標)。</param>
    /// <param name="pickY">同じく Y。</param>
    public static PickerHostProcess Start(
        PickerHostProfile profile,
        int pickX,
        int pickY,
        (int Left, int Top, int Width, int Height)? hostRect = null)
    {
        RequireCoordinateSafeProcess();
        return StartCore(
            profile,
            ["--pick-at", string.Create(CultureInfo.InvariantCulture, $"{pickX},{pickY}")],
            [new System.Windows.Point(pickX, pickY)],
            preferPublished: false,
            hostRect: hostRect);
    }

    /// <summary>
    /// 複数の pick 点を渡してホストを起動する。**n 枚目のピッカーが n 番目**を受け取る。
    /// </summary>
    /// <remarks>
    /// S1 (2 枚のピッカーがそれぞれ独立に追従すること) の前提である。
    /// pick 点が 1 つだと 2 枚が同じ要素を捕捉するので、
    /// オーバーレイを static singleton へ戻す退行が「枠が一致する」で素通りする
    /// (docs/DESIGN.md §12)。
    /// </remarks>
    public static PickerHostProcess Start(PickerHostProfile profile, params (int X, int Y)[] pickPoints)
        => Start(profile, culture: null, pickPoints);

    /// <summary>
    /// 表示カルチャを固定して起動する。**画面に出た文字列を検査するときはこちらを使う。**
    /// </summary>
    /// <remarks>
    /// 固定しないとホストは OS の表示言語に従う。開発機が日本語で CI が英語だと、
    /// **同じ assert が機械によって別の文字列と比べられる** — 通るほうの機械では
    /// 気づかないまま、もう一方だけが赤くなる。
    /// </remarks>
    public static PickerHostProcess Start(
        PickerHostProfile profile, string? culture, params (int X, int Y)[] pickPoints)
    {
        ArgumentNullException.ThrowIfNull(pickPoints);
        Assert.NotEmpty(pickPoints);
        RequireCoordinateSafeProcess();

        var arguments = new List<string>();
        if (culture is not null)
        {
            arguments.Add("--culture");
            arguments.Add(culture);
        }
        foreach ((int x, int y) in pickPoints)
        {
            arguments.Add("--pick-at");
            arguments.Add(string.Create(CultureInfo.InvariantCulture, $"{x},{y}"));
        }
        return StartCore(
            profile,
            [.. arguments],
            [.. pickPoints.Select(p => new System.Windows.Point(p.X, p.Y))],
            preferPublished: false);
    }

    /// <summary>
    /// 「もう 1 つ開く」を押して**2 枚目**のピッカーを開く。
    /// </summary>
    /// <remarks>
    /// 「ピッカーで追加」は既に開いていれば前面に出すだけなので、2 枚目はこちらでしか開かない。
    /// <see cref="OpenPicker"/> と違ってツリーは待たない — 2 枚目のツリーを 1 枚目と区別する
    /// 手段が AutomationId には無いためである。数えるのは呼び出し側の仕事。
    /// **窓が出るのは待つ** (退かさなければならないため)。
    /// </remarks>
    public void OpenAnotherPicker()
    {
        OpenAndAwaitPlacement("OpenAnotherPickerButton");
        // 2 枚目こそ確かめる。カスケードは 1 枚目の**あと**なので、より右下へ出る
        RequireThePickPointsAreNotCoveredByTheHost();
    }

    /// <summary>
    /// 静的なラベルだけを読むために、**座標も対象アプリも無しで**ホストを起動する。
    /// </summary>
    /// <param name="profile">起動するホストの種別。</param>
    /// <param name="culture">表示カルチャ (<c>--culture</c>)。</param>
    /// <remarks>
    /// <para>
    /// S4 (発行レイアウトでのリソース解決) はホバー捕捉も pick 点も要らない。
    /// それでも <see cref="Start"/> を使うと <see cref="RequireCoordinateSafeProcess"/> と
    /// <c>--pick-at</c> が付いてきて、**座標と無関係なテストが座標由来の flake を継承する** —
    /// T4 で実際に落ちるのは毎回その系統である。
    /// </para>
    /// <para>
    /// **それでも <c>--pick-at</c> は渡す。画面の外を指すためである。**
    /// 省くとピッカーは <c>Win32CursorSource</c> に落ち、**実マウスの下にあるものを
    /// 1 秒の滞留で本当に捕捉しはじめる** — 相手が大きなツリーだと数秒かかり
    /// (実測で 567 ノード / 2965ms)、その間ピッカーは UIA の問い合わせに答えない。
    /// **「対象アプリを使わない」と「捕捉が起きない」は別のことである。**
    /// </para>
    /// <para>
    /// **画面外を指しても捕捉の経路には入る** — 実測では <c>(-30000,-30000)</c> でも
    /// <c>ElementFromPoint</c> は**デスクトップ (<c>class='#32769'</c>) を返す**。
    /// ピッカーはそれを普通に捕捉し、画面いっぱいの枠を出す。
    /// </para>
    /// <para>
    /// それでも**この起動口の目的は果たしている**。狙いは「捕捉を起こさないこと」ではなく
    /// 「**結果がカーソルの置き場所で変わらないこと**」であり、
    /// デスクトップは常にそこに在って中身も安定しているので、そちらは成立している。
    /// 捕捉そのものも軽い (子を数千個持つツリーを歩くわけではない)。
    /// </para>
    /// <para>
    /// **これは観測された失敗を直したものではなく、構造からの予防である。**
    /// 実マウスに追随する経路は**カーソルがどこに在るかで結果が変わる**ので、
    /// 通るとしてもたまたまでしかない。だから経路ごと消してある。
    /// </para>
    /// </remarks>
    public static PickerHostProcess StartForLabels(PickerHostProfile profile, string culture)
        => StartCore(
            profile,
            ["--culture", culture, "--pick-at", OffScreenPoint],
            pickPoints: [],
            preferPublished: true);

    /// <summary>
    /// 対象アプリも座標も使わずに起動する。**見るのはビルド成果物であって発行物ではない。**
    /// </summary>
    /// <remarks>
    /// <see cref="StartForLabels"/> と起動口は同じで、違うのは <c>preferPublished</c> だけである。
    /// 発行物を見てよいのは**「発行物を検査する」と主張しているテスト (S4) だけ**で、
    /// <c>publish/</c> は一度作ると古いまま残るので、そうでないテストがそちらを見ると
    /// **古い発行物を黙って検査しつづける**ことになる (<see cref="ExecutablePathFor"/> の注記)。
    /// 配置を見るテスト (<c>SplitterTests</c>) はまさにそれで、
    /// XAML を直しても発行し直すまで古い配置を通し続けてしまう。
    /// </remarks>
    public static PickerHostProcess StartWithoutATarget(
        PickerHostProfile profile,
        string culture,
        (int Left, int Top, int Width, int Height)? hostRect = null)
        => StartCore(
            profile,
            ["--culture", culture, "--pick-at", OffScreenPoint],
            pickPoints: [],
            preferPublished: false,
            hostRect: hostRect);

    /// <summary>画面のどこでもない点。<see cref="StartForLabels"/> の remarks を参照。</summary>
    private const string OffScreenPoint = "-30000,-30000";

    private static PickerHostProcess StartCore(
        PickerHostProfile profile,
        string[] arguments,
        IReadOnlyList<System.Windows.Point> pickPoints,
        bool preferPublished,
        (int Left, int Top, int Width, int Height)? hostRect = null)
    {
        // どの起動口からでも窓を退かすので、割り付けが成立していることはここで見る
        DesktopLayout.RequireItFitsOnThisScreen();

        string exe = ExecutablePathFor(profile, preferPublished);
        var info = new ProcessStartInfo(exe) { UseShellExecute = false };
        foreach (string argument in arguments)
        {
            info.ArgumentList.Add(argument);
        }

        // **窓を退かすのはホスト自身である。**ここから渡す矩形へ、ホストが自分の窓を
        // 置き続ける (src/UiaTrigger.App.Shared/HostWindowPlacer.cs)。
        //
        // ハーネス側で 5ms ごとに退かす見張りスレッドを立てていたのをやめた形である。
        // あれには**寿命**があり (ボタンを押す前後だけ立てて finally で止める)、
        // 止まったあとに現れた窓やフレームワークが当て直した窓は誰も退かさなかった。
        // T4 が実際にそれで落ちている (pick 点との余裕 4px)。
        //
        // **どの起動口からも無条件で渡す。**hostRect が指定されたときだけ渡す形にすると、
        // aot ジョブが回す S4 (StartForLabels → hostRect: null) がこの経路を一度も通らず、
        // 「AOT 発行するとフックが黙って効かない」を CI が一度も見ないことになる
        // (docs/DESIGN.md D2 が塞いでいるのと同じ形)
        (int Left, int Top, int Width, int Height) rect = hostRect ?? DesktopLayout.Host;
        info.ArgumentList.Add("--place-windows");
        info.ArgumentList.Add(string.Create(
            CultureInfo.InvariantCulture, $"{rect.Left},{rect.Top},{rect.Width},{rect.Height}"));

        // トリガーの保存先は必ず差し替える (TriggerFile の remarks を参照)
        string triggerFile = Path.Combine(
            Path.GetTempPath(), $"uiatrigger-t4-{Guid.NewGuid():N}.json");
        info.ArgumentList.Add("--triggers");
        info.ArgumentList.Add(triggerFile);

        // 前回の実行のログが混ざると診断が嘘になる
        string logPath = Path.Combine(Path.GetTempPath(), profile.LogFileName);
        DeleteIfPresent(logPath);

        Process process = Process.Start(info)
            ?? throw new InvalidOperationException($"起動できませんでした: {exe}");

        // ウィンドウの出現は MainWindowHandle を待つ。固定の Sleep では足りないことが
        // 実測で分かっている
        AutomationElement mainWindow = Ui.Until(
            () =>
            {
                process.Refresh();
                if (process.HasExited)
                {
                    throw new InvalidOperationException(FormattableString.Invariant(
                        $"ホストが起動直後に終了しました (終了コード {process.ExitCode}): {exe}"));
                }
                nint handle = process.MainWindowHandle;
                return handle == 0 ? null : AutomationElement.FromHandle(handle);
            },
            WindowTimeout,
            $"{profile.Name} ホストのメインウィンドウ",
            () => $"実行ファイル: {exe}");

        var host = new PickerHostProcess(
            process, profile, mainWindow, logPath, triggerFile, pickPoints, rect);
        // 置くのはホストだが、**置き終えたことはこちらで確かめる。**
        // 「渡したから置かれたはず」にすると、フックが張れていない形が素通りする
        _ = Ui.Until(
            () => host.PlacedHostWindowCount() > 0 ? "ok" : null,
            WindowTimeout,
            $"{profile.Name} ホストがメインウィンドウを {Describe(rect)} へ置く",
            host.Diagnostics);
        return host;
    }

    /// <summary>
    /// 「ピッカーで追加」を押してピッカーを開き、ツリーが出るまで待つ。
    /// </summary>
    /// <remarks>
    /// ボタンは **AutomationId** で探す。表示名で探すと OS の表示言語に依存し、
    /// 「英語環境で全部落ちる」形の壊れ方をする。
    /// </remarks>
    public void OpenPicker()
    {
        OpenAndAwaitPlacement(
            "OpenPickerButton",
            thenWaitFor: () => Ui.Until(
                FindTree,
                WindowTimeout,
                $"{Profile.Name} ホストのピッカーウィンドウ (要素ツリー '{Profile.TreeAutomationId}')",
                Diagnostics));
        RequireThePickPointsAreNotCoveredByTheHost();
    }

    /// <summary>
    /// 「一覧を編集」を押してトリガ一覧エディタを開き、一覧が出るまで待つ
    /// (docs/DESIGN.md §4)。
    /// </summary>
    /// <remarks>
    /// **WinUI ホストでしか使えない。**WPF / Windows Forms のエディタは
    /// <c>ShowDialog</c> による本物のモーダルなので、UIA の <c>Invoke()</c> が
    /// **ダイアログが閉じるまで返らない**おそれがある。あちらの確認は MANUAL-CHECKS に置く。
    /// </remarks>
    public void OpenEditor()
        => OpenAndAwaitPlacement(
            "EditListButton",
            thenWaitFor: () => Ui.Until(
                FindEditor,
                WindowTimeout,
                $"{Profile.Name} ホストのエディタウィンドウ (一覧 'EditorTriggerList')",
                Diagnostics));

    /// <summary>
    /// トリガ一覧エディタの**ウィンドウ**。呼ぶたびに探し直す。
    /// </summary>
    /// <remarks>
    /// 手掛かりは**トリガー一覧を持っていること**である。タイトルでは探さない —
    /// ローカライズされているので、探す条件に使うと翻訳の検査と循環する
    /// (<see cref="PickerWindow"/> と同じ理由)。
    /// </remarks>
    public AutomationElement EditorWindow() => Ui.Until(
        FindEditor,
        WindowTimeout,
        $"{Profile.Name} ホストのエディタウィンドウ",
        Diagnostics);

    private AutomationElement? FindEditor()
        => TopLevelWindows().FirstOrDefault(w => w.ById("EditorTriggerList") is not null);

    /// <summary>エディタの窓が出ているか (閉じたことを待つのに使う)。</summary>
    public bool EditorWindowIsShowing() => FindEditor() is not null;

    /// <summary>
    /// エディタのボタンを押して子ピッカーを開き、**出た窓を pick 点から退かす**。
    /// </summary>
    /// <param name="buttonId">押すボタン (<c>AddTriggerButton</c> / <c>EditTriggerButton</c>)。</param>
    /// <returns>開いた子ピッカーのウィンドウ。</returns>
    /// <remarks>
    /// 退かすところまでを 1 つにしてあるのは、素の <c>Invoke()</c> で済ませると
    /// カスケードが pick 点を覆う非決定性がそのまま戻るからである
    /// (<see cref="OpenAndAwaitPlacement(Func{AutomationElement}, string, Action)"/> の remarks を参照)。
    /// </remarks>
    public AutomationElement OpenPickerFromEditor(string buttonId)
    {
        OpenAndAwaitPlacement(
            EditorWindow,
            buttonId,
            thenWaitFor: () => Ui.Until(
                FindTree,
                WindowTimeout,
                $"{Profile.Name} ホストの子ピッカー (要素ツリー '{Profile.TreeAutomationId}')",
                Diagnostics));
        RequireThePickPointsAreNotCoveredByTheHost();
        return PickerWindow();
    }

    /// <summary>
    /// ボタンを押し、そこで出てくるホストの窓が**退き終わるまで待つ**。
    /// </summary>
    /// <remarks>
    /// <para>
    /// **退かすのはホスト自身である** (<c>--place-windows</c> — docs/TESTING.md §1)。
    /// ピッカーの窓はカスケードして開くので、pick 点を覆うかどうかが実行ごとに変わる。
    /// 覆ったまま滞留 1 秒が明けると、ピッカーは自プロセスを掴んで捕捉を飛ばし、
    /// <c>TickAsync</c> の「同じ点は再捕捉しない」により**二度と捕捉しない**。
    /// </para>
    /// <para>
    /// **ここが 5ms ごとの見張りスレッドだった。**やめたのは、あれには**寿命**があったからである —
    /// このメソッドの <c>finally</c> で止まるので、そのあとに現れた窓や、フレームワークが
    /// あとから配置を当て直した窓は誰も退かさなかった。ホスト側のフックには寿命が無い。
    /// </para>
    /// <para>
    /// **数えるのは「窓が出たか」ではなく「退き終えたか」である。**窓の数で待つと
    /// **現れた瞬間に待つのをやめて**しまい、退く前のものが残る。実際にそれで落ちている
    /// ((182,182)-(1382,800) がそのまま残り、pick 点を覆っていた)。
    /// </para>
    /// </remarks>
    private void OpenAndAwaitPlacement(string buttonId, Action? thenWaitFor = null)
        => OpenAndAwaitPlacement(() => MainWindow, buttonId, thenWaitFor);

    /// <summary>
    /// 同じことを、**メインウィンドウ以外**の窓のボタンに対して行う。
    /// </summary>
    /// <remarks>
    /// エディタの [追加] / [条件を編集] から子ピッカーを開く経路がこれである。
    /// **この経路の窓はホストではなく出荷するライブラリが作る** —
    /// <c>TriggerListEditorWindow.EditAsync</c> は窓を返さないので、ホストは参照を持てない。
    /// 生成箇所ごとに置く形では覆えず、ホスト側が自プロセスの窓イベントで拾うのはそのためである。
    /// 根 (<paramref name="root"/>) を遅延で受けるのは、退いた直後の WinUI では
    /// **既に在る窓の要素が一時的に UIA から消える**ため、掴み直せるようにするためである。
    /// </remarks>
    private void OpenAndAwaitPlacement(Func<AutomationElement> root, string buttonId, Action? thenWaitFor = null)
    {
        int before = PlacedHostWindowCount();

        // **待つこと。**素の RequireById (一発の FindFirst) にすると、フル実行のとき
        // だけ「AutomationId 'OpenPickerButton' の要素が見つかりません」という顔で
        // 間欠に落ちる。**WinUI では、レイアウトが動いた直後に既に在る要素が
        // 一時的に UIA から消える**ためで、実際には**まだ出ていないだけ**である。
        // 待っても出てこなければ結局落ちるので、本当に無い場合を見逃すことはない。
        root().RequireByIdEventually(buttonId, Diagnostics).Invoke();
        // Invoke が返っても窓はまだ無いことがある (実測で WPF は 47ms で返り、窓は 381ms)
        _ = Ui.Until(
            () => PlacedHostWindowCount() > before ? "ok" : null,
            WindowTimeout,
            $"{Profile.Name} ホストが新しいウィンドウを {Describe(_hostRect)} へ置く",
            Diagnostics);
        thenWaitFor?.Invoke();
    }

    /// <summary>割り付けの場所へ退き終えた、ホストのトップレベルウィンドウの数。</summary>
    /// <remarks>
    /// **観測だけである。**ここから窓を動かすことはもう無い (動かすのはホスト)。
    /// オーバーレイは数えない — あれは対象要素の上に出るのが仕事で、退かない。
    /// </remarks>
    private int PlacedHostWindowCount()
        => NativeWindows.TopLevelWindowsOf(_process.Id).Count(h => !IsOverlay(h) && IsPlaced(h));

    private static string Describe((int Left, int Top, int Width, int Height) r) => string.Create(
        CultureInfo.InvariantCulture, $"({r.Left},{r.Top})-({r.Left + r.Width},{r.Top + r.Height})");

    private bool IsPlaced(nint hwnd)
    {
        (int left, int top, int width, int height) = _hostRect;
        return NativeWindows.RectOf(hwnd) is { } r &&
               r.Left == left && r.Top == top && r.Right == left + width && r.Bottom == top + height;
    }

    private static bool IsOverlay(nint hwnd)
        => string.Equals(NativeWindows.ClassOf(hwnd), OverlayClassName, StringComparison.Ordinal);

    /// <summary>
    /// pick 点がホストの窓に覆われていないこと。
    /// </summary>
    /// <remarks>
    /// ハーネス自身の検証である (docs/TESTING.md §4 の教訓 (b))。覆われていると捕捉は
    /// **例外も診断も出さずに**起きないので、20 秒のタイムアウトとして受け取ると
    /// 原因が「実体化しなかった」に見える。ここで落とせば理由が残る。
    /// オーバーレイは対象要素の上に出るのが仕事なので数えない。
    /// </remarks>
    public void RequireThePickPointsAreNotCoveredByTheHost()
    {
        foreach (System.Windows.Point point in _pickPoints)
        {
            foreach (nint hwnd in NativeWindows.TopLevelWindowsOf(_process.Id))
            {
                if (IsOverlay(hwnd) || NativeWindows.RectOf(hwnd) is not { } r)
                {
                    continue;
                }
                Assert.False(
                    point.X >= r.Left && point.X < r.Right && point.Y >= r.Top && point.Y < r.Bottom,
                    string.Create(
                        CultureInfo.InvariantCulture,
                        $"pick 点 ({point.X},{point.Y}) がホストの窓 ({r.Left},{r.Top})-({r.Right},{r.Bottom}) に覆われています ({NativeWindows.Describe(hwnd)})。") +
                    "この状態ではホバー捕捉が起きず、しかも同じ点は再捕捉されないので、" +
                    "テストは 20 秒待ってから「実体化しなかった」という顔で落ちます。");
            }
        }
    }

    /// <summary>
    /// ピッカーの要素ツリー。**呼ぶたびに探し直す**。
    /// </summary>
    /// <remarks>
    /// 要素をキャッシュしないのは実 UIA のテストの基本規律である。UIA の要素は
    /// 相手プロセスの状態への参照であり、掴んだまま使い続けると**古い答えを返し続ける**
    /// (実際にそうなった — WPF で選択が反映されているのに、掴んでおいた
    /// <c>TreeView</c> の要素からは永遠に「未選択」に見えた)。
    /// </remarks>
    public AutomationElement Tree() => Ui.Until(
        FindTree,
        WindowTimeout,
        $"要素ツリー ('{Profile.TreeAutomationId}')",
        Diagnostics);

    /// <summary>
    /// ピッカーの**ウィンドウ**。<c>Tree()</c> と同じく呼ぶたびに探し直す。
    /// </summary>
    /// <remarks>
    /// メインウィンドウと区別する手掛かりは**要素ツリーを持っていること**である。
    /// タイトルでは探さない — ローカライズされており、S4 はまさにその翻訳を
    /// 検査対象にしているので、探す条件に使うと循環する。
    /// </remarks>
    public AutomationElement PickerWindow() => Ui.Until(
        () => PickerWindows() is { Count: > 0 } windows ? windows[0] : null,
        WindowTimeout,
        $"{Profile.Name} ホストのピッカーウィンドウ",
        Diagnostics);

    /// <summary>
    /// 開いているピッカーのウィンドウ**すべて**。
    /// </summary>
    /// <remarks>
    /// <para>
    /// <see cref="PickerWindow"/> は 1 枚目しか返さない。K5 (フックを有効にしている
    /// ピッカーだけに ←/→ が届くこと) は 2 枚を別々に操作するので、全部要る。
    /// </para>
    /// <para>
    /// **どれがどのオーバーレイのものかは、ここでも言えない。**ピッカーの窓と
    /// オーバーレイを対応づける手段が無いのは <see cref="Overlays"/> と同じ理由である。
    /// 言えるのは「n 枚ある」ことと「そのうち 1 枚を操作した」ことまでで、
    /// assert は**集合か個数**で書く。
    /// </para>
    /// </remarks>
    public IReadOnlyList<AutomationElement> PickerWindows()
    {
        // 「要素ツリーを子孫に持つ窓」では数えない。所有された子ピッカー
        // (WinUI エディタ経由 — GWLP_HWNDPARENT) は UIA では**所有者の子**に出るので、
        // その形だとエディタの窓まで「ツリーを持つ窓」に一致して二重に数える (実測)。
        // ツリーから**最寄りの Window 要素**へ上がったものだけを、重複を除いて数える
        var found = new List<AutomationElement>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (AutomationElement candidate in TopLevelWindows())
        {
            if (candidate.ById(Profile.TreeAutomationId) is not { } tree ||
                NearestWindowOf(tree) is not { } window)
            {
                continue;
            }
            if (seen.Add(string.Join(",", window.GetRuntimeId())))
            {
                found.Add(window);
            }
        }
        return found;
    }

    /// <summary>要素を実際に載せている、最寄りの Window 要素。</summary>
    private static AutomationElement? NearestWindowOf(AutomationElement element)
    {
        for (AutomationElement? current = element;
             current is not null && current != AutomationElement.RootElement;
             current = TreeWalker.ControlViewWalker.GetParent(current))
        {
            if (current.Current.ControlType == ControlType.Window)
            {
                return current;
            }
        }
        return null;
    }

    /// <summary>
    /// このホストが出している**枠**のウィンドウ (docs/DESIGN.md §10)。
    /// </summary>
    /// <remarks>
    /// <para>
    /// オーバーレイはトップレベルウィンドウで、ピッカーのウィンドウの子ではない。
    /// 所有関係は辿れないので **pid + クラス名**で絞る。
    /// </para>
    /// <para>
    /// **1 枚のピッカーはウィンドウを 2 つ出す** (docs/DESIGN.md §10)。枠と確定アイコンは別の窓で、
    /// クラス名だけが唯一の見分ける手掛かりである。ここが返すのは**枠だけ**で、
    /// アイコンは <see cref="IconOverlays"/> が返す。
    /// **どちらを数えているのかを呼ぶ側に明示させる**のが狙いである —
    /// 1 つの関数で両方返すと「オーバーレイが 2 つある」がピッカー 2 枚なのか
    /// 枠 + アイコンなのか区別できなくなり、A18 の退行検査が意味を失う。
    /// </para>
    /// <para>
    /// **どのオーバーレイがどのピッカーのものかは言えない。**タイトルも AutomationId も
    /// 持たず、同じ種類の窓どうしではクラス名も共通なので、**矩形以外に見分ける手段が無い**。
    /// だから S1 の assert は「対応付け」ではなく**集合の一致**で書く。
    /// </para>
    /// </remarks>
    public IReadOnlyList<AutomationElement> Overlays() => WindowsOfClass(OverlayClassName);

    /// <summary>
    /// このホストが出している**確定アイコン**のウィンドウ (docs/DESIGN.md §10)。
    /// </summary>
    /// <remarks>
    /// **クリックを受け取るのはこちらだけである。**枠の窓は <c>WS_EX_TRANSPARENT</c> で
    /// ヒットテストから外れているので、押せるかどうかを見る検査はこちらを見る。
    /// </remarks>
    public IReadOnlyList<AutomationElement> IconOverlays() => WindowsOfClass(IconOverlayClassName);

    private List<AutomationElement> WindowsOfClass(string className)
    {
        var found = new List<AutomationElement>();
        foreach (AutomationElement e in AutomationElement.RootElement.FindAll(
            TreeScope.Children,
            new AndCondition(
                new PropertyCondition(AutomationElement.ProcessIdProperty, _process.Id),
                new PropertyCondition(AutomationElement.ClassNameProperty, className))))
        {
            found.Add(e);
        }
        return found;
    }

    /// <summary>枠のウィンドウクラス (<c>OverlayController.WindowClassName</c> と同じ綴り)。</summary>
    /// <remarks>
    /// 定数を共有しないのは意図である。<c>Picker.Core</c> 側を変えたらここも落ちるべきで、
    /// 共有すると「両方いっしょに動いて何も守らないテスト」(docs/TESTING.md §2) になる。
    /// </remarks>
    private const string OverlayClassName = "UiaTriggerOverlay";

    /// <summary>
    /// アイコンのウィンドウクラス (<c>OverlayController.IconWindowClassName</c> と同じ綴り)。
    /// </summary>
    /// <remarks>
    /// 上と同じ理由で綴りを写している。**枠のクラス名の前方一致では絞れない** —
    /// <c>"UiaTriggerOverlay"</c> は <c>"UiaTriggerOverlayIcon"</c> の接頭辞なので、
    /// 前方一致にすると枠を数えるつもりでアイコンまで数える。完全一致で絞ること。
    /// </remarks>
    private const string IconOverlayClassName = "UiaTriggerOverlayIcon";

    /// <summary>
    /// このプロセスのトップレベルウィンドウのうち、要素ツリーを持つものの、そのツリー。
    /// </summary>
    /// <remarks>
    /// ウィンドウのタイトルでは探さない — ローカライズされているからである。
    /// 「要素ツリーがある」ことをピッカーの定義にすれば、言語にもクラス名にも依存しない。
    /// </remarks>
    private AutomationElement? FindTree()
    {
        foreach (AutomationElement window in TopLevelWindows())
        {
            if (window.ById(Profile.TreeAutomationId) is { } tree)
            {
                return tree;
            }
        }
        return null;
    }

    private List<AutomationElement> TopLevelWindows()
    {
        AutomationElementCollection children = AutomationElement.RootElement.FindAll(
            TreeScope.Children,
            new PropertyCondition(AutomationElement.ProcessIdProperty, _process.Id));
        var list = new List<AutomationElement>(children.Count);
        for (int i = 0; i < children.Count; i++)
        {
            list.Add(children[i]);

            // 所有された窓 (WinUI エディタの子ピッカー — GWLP_HWNDPARENT) は、Win32 では
            // トップレベルのまま (退かしの NativeWindows 側はそのまま掴む) だが、
            // **UIA の木ではデスクトップ直下ではなく所有者の子に出る** (実測)。
            // ここで拾わないと、エディタ経由で開いたピッカーの窓が見つからない
            AutomationElementCollection owned = children[i].FindAll(
                TreeScope.Children,
                new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.Window));
            for (int j = 0; j < owned.Count; j++)
            {
                list.Add(owned[j]);
            }
        }
        return list;
    }

    /// <summary>失敗したときに読む状態。これが無いと実 UIA のテストは原因が分からない。</summary>
    public string Diagnostics()
    {
        var lines = new List<string>
        {
            FormattableString.Invariant($"ホスト: {Profile.Name} (pid {_process.Id})"),
            // 「捕捉が起きなかった」の原因はほぼ常に**指した先に何が居たか**である。
            // ピッカー自身 (= 自プロセス) を指してしまうと、例外も診断も出ずに何も起きない
            DescribePickPoint(),
        };

        try
        {
            List<AutomationElement> windows = TopLevelWindows();
            lines.Add($"UIA から見えるトップレベルウィンドウ: {windows.Count} 個");
            foreach (AutomationElement window in windows)
            {
                lines.Add(FormattableString.Invariant(
                    $"  name='{window.Current.Name}' class='{window.Current.ClassName}' rect={window.Current.BoundingRectangle}"));
                // ヒント欄はピッカー自身のエラー通知先である (捕捉の失敗・DPI の問題・
                // オーバーレイの生成失敗)。読まずに「捕捉されなかった」とだけ言っても原因が分からない
                foreach (string id in new[] { "HintText", "AutoSelectToggle", "ConfirmedText", "CommitStatus" })
                {
                    if (window.ById(id) is { } label)
                    {
                        lines.Add($"  {id}: '{label.NameOf()}'");
                    }
                }
                if (window.ById(Profile.TreeAutomationId) is { } tree)
                {
                    lines.Add($"  ツリーの行 ({Profile.TreeAutomationId}) — * は選択されている行:");
                    lines.Add(tree
                        .Descendants(ControlType.TreeItem)
                        .Select(r => (r.IsSelected() ? "* " : "  ") + r.NameOf())
                        .ToArray()
                        .Describe());
                }
            }
        }
        catch (ElementNotAvailableException ex)
        {
            lines.Add($"  (ウィンドウの列挙に失敗: {ex.Message})");
        }

        lines.Add(ReadLog());
        return string.Join(Environment.NewLine, lines);
    }

    /// <summary>指した座標に、いま実際に何が居るか。</summary>
    private string DescribePickPoint()
    {
        var lines = new List<string>();
        foreach (System.Windows.Point point in _pickPoints)
        {
            try
            {
                AutomationElement? at = AutomationElement.FromPoint(point);
                string what = at is null
                    ? "(要素なし)"
                    : FormattableString.Invariant(
                        $"pid {at.Current.ProcessId} '{at.Current.Name}' class='{at.Current.ClassName}'");
                lines.Add(FormattableString.Invariant(
                    $"--pick-at ({point.X},{point.Y}) の位置に居る要素: {what}"));
            }
            catch (Exception ex) when (ex is ElementNotAvailableException or ArgumentException)
            {
                lines.Add($"--pick-at の位置を調べられませんでした: {ex.Message}");
            }
        }
        return lines.Count == 0
            ? "--pick-at は画面の外を指しています (捕捉は起きません)。"
            : string.Join(Environment.NewLine, lines);
    }

    /// <summary>
    /// ホストの未処理例外ログだけを読む。**UIA を一切触らない**。
    /// </summary>
    /// <remarks>
    /// <see cref="Diagnostics"/> は使えない場面がある。あれはデスクトップの子を列挙するので、
    /// **応答しない窓が 1 つでも在ると一緒に詰まる** (実測で 12 秒。しかも
    /// <c>Win32Exception</c> を投げることがあり、<c>Ui.Until</c> の診断収集はそれを捕まえないので
    /// **本当の失敗を置き換えて消す** — docs/TESTING.md §1)。
    /// 塞がれたアプリを相手にするテストは、こちらとあらかじめ掴んだ HWND だけで診断を組む。
    /// </remarks>
    public string HostLog() => ReadLog();

    /// <summary>
    /// ホストの未処理例外ログ。**空であることに意味がある** — WinUI の
    /// 「黙って失敗する層」はここにしか痕跡を残さないことがある (docs/DESIGN.md §12)。
    /// </summary>
    private string ReadLog()
    {
        try
        {
            return File.Exists(_logPath)
                ? $"ホストのログ ({_logPath}):{Environment.NewLine}{File.ReadAllText(_logPath)}"
                : $"ホストのログ ({_logPath}): 出力なし";
        }
        catch (IOException ex)
        {
            return $"ホストのログを読めませんでした: {ex.Message}";
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }
        _disposed = true;
        try
        {
            // メインウィンドウを閉じるとホストが開いているピッカーも閉じ、
            // オーバーレイの低レベルキーボードフックが外れる
            _process.CloseMainWindow();
            if (_process.WaitForExit(5000))
            {
                ExitedGracefully = true;
            }
            else
            {
                // **Kill したことを記録する。**ここで黙って始末すると、Dispose から戻った
                // 時点でプロセスは必ず消えており、「閉じたら残らない」を見るテストが
                // Kill の結果を観測して常に緑になる (退行を入れても落ちない = 検出力ゼロ)。
                // 掃除そのものは続ける — 残骸は次のテストを座標で落とす
                _process.Kill(entireProcessTree: true);
                _process.WaitForExit(5000);
            }
        }
        catch (Exception ex) when (ex is InvalidOperationException or SystemException)
        {
        }
        _process.Dispose();
        // ホストが終わってから消す。動いているうちに消すと、コミットで書き直されて残る
        DeleteIfPresent(TriggerFile);
    }

    private static void DeleteIfPresent(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }
    }

    /// <summary>
    /// テストプロセスを PerMonitorV2 にする。
    ///
    /// DPI 非認識のままだと Windows が座標を仮想化するため、<c>--pick-at</c> に渡した座標が
    /// 狙った要素の外を指し、**例外にならずに別の要素を捕捉する** (docs/DESIGN.md A19)。
    /// T3 の <c>TestTargetProcess.RequireCoordinateSafeProcess</c> と同じ理由・同じ形である。
    /// </summary>
    private static void RequireCoordinateSafeProcess()
    {
        DpiAwareness.TryEnablePerMonitorV2();
        Assert.True(
            DpiAwareness.IsCoordinateSafe(),
            "テストプロセスを PerMonitorV2 にできませんでした。" +
            "この状態では --pick-at に渡した座標が別の要素を指すため、T4 の結果は信用できません: " +
            DpiAwareness.DescribeProblem());
    }

    /// <summary>
    /// ホストの実行ファイルを探す。テストと同じ構成 (Debug/Release) の出力を選ぶ。
    /// </summary>
    /// <remarks>
    /// <para>
    /// T3 の <c>TestTargetProcess.FindExecutable</c> とは 2 点違う。どちらも
    /// 実際に踏んだ失敗の形である:
    /// </para>
    /// <list type="number">
    /// <item><description>
    /// <c>bin/**/native/</c> を除く。これは Native AOT の中間生成物で、隣に
    /// WindowsAppSDK ランタイムも <c>resources.pri</c> も無く**起動しない**。
    /// </description></item>
    /// <item><description>
    /// 候補が無ければ「1 つ目」で妥協せず**落ちる**。妥協すると、別構成の古いビルドを
    /// 黙って起動して「なぜか動かない」だけが残る。
    /// </description></item>
    /// </list>
    /// <para>
    /// **候補が複数あるときは、いちばん新しいものを採る。**
    /// 同じ Release 構成でも出力先は 1 つではない — ソリューションからビルドすると
    /// <c>bin/x64/Release/…</c> に、プロジェクト単体でビルドすると <c>bin/Release/…</c> に出る。
    /// **列挙順で先に来たほう**を採ると、一度でも単体ビルドをしたあとは
    /// T4 が**古いバイナリを黙って起動しつづけ**、「直したはずの挙動が直っていない」
    /// という誤った観測になる (実際に踏んだ)。
    /// これは T4 全体を**間違った理由で緑**にしうる形なので、時刻で選ぶ。
    /// </para>
    /// </remarks>
    private static string ExecutablePathFor(PickerHostProfile profile, bool preferPublished)
    {
        // 発行レイアウトを見るのは**それを主張しているテストだけ**である (S4)。
        //
        // 全体の既定にはしない。publish/ は一度作ると古いまま残るので、既定にすると
        // 他の T4 が「古い発行物を黙って検査しつづける」ことになる (実際に踏んだ)。
        //
        // S4 の側では逆に、時刻で選んではいけない。CI では bin/ と publish/ の両方が在り、
        // どちらが後にできるかはジョブの並びで変わる。「発行物を検査する」ことが
        // テストの主張そのものなので、そこを実行順に委ねられない
        if (preferPublished && PublishedExecutable(profile) is { } published)
        {
            return published;
        }

        string binDir = RepoPaths.Combine("src", profile.ProjectDirectory, "bin");
        if (!Directory.Exists(binDir))
        {
            throw new InvalidOperationException(
                $"{profile.ProjectDirectory} がビルドされていません ({binDir} がありません)。" +
                "ソリューション全体 (dotnet build UiaTrigger.slnx) をビルドしてから走らせてください。");
        }

        string configuration = AppContext.BaseDirectory.Contains(
            Path.Combine("bin", "Debug"), StringComparison.OrdinalIgnoreCase) ? "Debug" : "Release";
        string configurationSegment =
            $"{Path.DirectorySeparatorChar}{configuration}{Path.DirectorySeparatorChar}";
        string nativeSegment = $"{Path.DirectorySeparatorChar}native{Path.DirectorySeparatorChar}";

        string[] candidates =
        [
            .. Directory.EnumerateFiles(binDir, profile.ExecutableName, SearchOption.AllDirectories)
                .Where(p => !p.Contains(nativeSegment, StringComparison.OrdinalIgnoreCase))
                .Where(p => p.Contains(configurationSegment, StringComparison.OrdinalIgnoreCase))
                .Where(p => profile.RequiredSiblingFile.Length == 0 ||
                            File.Exists(Path.Combine(Path.GetDirectoryName(p)!, profile.RequiredSiblingFile)))
                // 新しい順。列挙順に任せると、単体ビルドの古い出力を掴みつづける (上の remarks)
                .OrderByDescending(File.GetLastWriteTimeUtc)
        ];

        return candidates.Length > 0
            ? candidates[0]
            : throw new InvalidOperationException(
                $"起動できる {profile.ExecutableName} ({configuration} 構成" +
                (profile.RequiredSiblingFile.Length == 0
                    ? ""
                    : $"・隣に {profile.RequiredSiblingFile} が在るもの") +
                $") が {binDir} 以下に見つかりません。" +
                "ソリューション全体 (dotnet build UiaTrigger.slnx) をビルドしてから走らせてください。");
    }

    /// <summary>
    /// <c>publish/&lt;profile&gt;/</c> に発行レイアウトが在ればその実行ファイル。無ければ null。
    /// </summary>
    /// <remarks>
    /// CI の <c>aot</c> ジョブがここへ発行し、S4 がそれを起動する。
    /// <c>RequiredSiblingFile</c> (WinUI なら <c>.pri</c>) の判定は発行レイアウトにもそのまま効く —
    /// **むしろここが本番である**。<c>.pri</c> が落ちるのは発行のときだからで、
    /// <c>bin/</c> ではまず起きない。
    /// </remarks>
    internal static string? PublishedExecutable(PickerHostProfile profile)
    {
        string path = RepoPaths.Combine("publish", profile.PublishDirectory, profile.ExecutableName);
        if (!File.Exists(path))
        {
            return null;
        }
        if (profile.RequiredSiblingFile.Length > 0 &&
            !File.Exists(Path.Combine(Path.GetDirectoryName(path)!, profile.RequiredSiblingFile)))
        {
            // 「発行はされたが .pri が落ちている」— S4 が捕まえたい失敗そのものである。
            // ここで黙って bin/ へ落ちると、検査対象を取り違えて緑になる
            throw new InvalidOperationException(
                $"発行レイアウト ({path}) の隣に {profile.RequiredSiblingFile} がありません。" +
                "この状態のアプリはリソースを解決できません (ラベルがキー名になる)。" +
                "bin/ へは落とさずここで落とします — 検査対象を取り違えて緑になるためです。");
        }
        return path;
    }
}
