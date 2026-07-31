using System.Windows.Automation;
using UiaTrigger.Models;
using UiaTrigger.RealUia.Tests;
using Xunit;

namespace UiaTrigger.Picker.UiTests;

/// <summary>
/// D9 — ピッカーで録ったトリガーが**実際に発火し、ログ一覧に出る**ところまでを通しで見る
/// (docs/MANUAL-CHECKS.md §9 / docs/DESIGN.md §12)。
///
/// <para>
/// **発火がログ一覧に出ることは、ここでしか見えない。**`HostMonitorTests` (T1) が
/// 見ているのはソースの形だけで、「発火がログ一覧に出るか」については何も言えない。
/// 実行には対話デスクトップが要る (docs/TESTING.md §6)。
/// </para>
/// <para>
/// **`App.WinUI` だけを見る。**監視 UI を持つのはこのホストだけで、
/// README の "Two asymmetries are deliberate" が明記する意図された非対称である。
/// 他の 2 ホストに監視 UI が**無い**ことは <see cref="MonitoringIsAbsentFromTheOtherHosts"/> で見る。
/// </para>
/// <para>
/// **入力は UIA のコントロールパターンだけで駆動する。**合成入力は一切使わない
/// (docs/TESTING.md §3 の政策。最下層へ入る経路が解禁されているのは
/// `tests/UiaTrigger.Input.Tests` の中だけである)。
///
/// **その API 名をここに書かないこと。**政策の lint は `tests/**/*.cs` を
/// 文字列で走査するので、**「使っていない」と書いた説明文でも赤になる**
/// (実際に 1 度落とした)。lint は意図的に鈍く作ってあるので、
/// 緩めるのではなく**こちらの書き方を変える**のが正しい。
/// </para>
/// </summary>
public sealed class MonitorShowcaseTests
{
    /// <summary>UI が落ち着くのを待つ上限。</summary>
    private static readonly TimeSpan Settle = TimeSpan.FromSeconds(20);

    /// <summary>発火が届くのを待つ上限。解決 → 購読 → 変化の 3 段を含む。</summary>
    private static readonly TimeSpan Fire = TimeSpan.FromSeconds(30);

    /// <summary>画面の文字列を見るので表示カルチャを固定する (機械の表示言語に依存させない)。</summary>
    private const string Culture = "en-US";

    private const string Target = "btnWatched";

    /// <summary>ログ行の書式は `MonitorRow*` リソース側にある。ここでは種別の札だけを見る。</summary>
    private const string FiredMarker = "FIRED";

    /// <summary>拾い所の無い例外の行。**出ていないこと**を見るのに使う。</summary>
    private const string ErrorMarker = "ERROR";

    /// <summary>
    /// 解決の行の札。**小文字である** — ホストは平常を小文字、注意を大文字で出し分けている。
    /// </summary>
    /// <remarks>
    /// **ここを大文字で書くと `UNRESOLVED` にも一致してしまい、**
    /// 「未解決になったのを解決と読んで先へ進む」テストになる。
    /// 一致は序数で見るので、この綴りのままなら取り違えない。
    /// </remarks>
    private const string ResolvedMarker = "resolved";

    private const string UnresolvedMarker = "UNRESOLVED";

    /// <summary>
    /// **録って、監視して、実際に鳴らして、ログ行を読む。**D9 の本体である。
    /// </summary>
    /// <remarks>
    /// <para>
    /// 見ているのは 3 つ:
    /// (1) 「監視を開始」でボタンの活性が入れ替わること、
    /// (2) 対象アプリを変化させると**発火行が出る**こと、
    /// (3) 「監視を停止」で活性が戻ること。
    /// </para>
    /// <para>
    /// **発火行は「増えた」ではなく「FIRED の行が在る」で見る。**
    /// 解決状態の変化も同じ一覧に出るので、行数だけを数えると
    /// **解決の通知が来ただけで緑になる** — 肝心の発火を見ないまま通る。
    /// </para>
    /// </remarks>
    [Fact]
    public void RecordingATrigger_ThenMonitoring_PutsAFiredRowInTheLog()
    {
        using var scenario = ShowcaseScenario.Open();
        string id = scenario.RecordAndCommit();

        // 録れていること。ここが崩れていると、以降は「何も監視していないので鳴らない」になる
        TriggerDefinition saved = Assert.Single(scenario.SavedTriggers());
        Assert.Equal(id, saved.Id);
        Assert.Equal("UiaTrigger.TestTarget.exe", saved.Window.ProcessName);

        scenario.StartMonitoring();
        Assert.False(scenario.IsEnabled("StartMonitorButton"));
        Assert.True(scenario.IsEnabled("StopMonitorButton"));

        // 監視が対象を解決するのを待ってから変化させる。先に変えると、購読が張られる前の
        // 変化になり「鳴らない」が起きうる — テストの取りこぼしであって製品の欠陥ではない
        scenario.WaitForALogRowContaining(saved.Id, "解決状態の行");

        scenario.ChangeTheTarget("fired-once");

        string row = scenario.WaitForALogRowContaining(FiredMarker, "発火の行");
        Assert.Contains(saved.Id, row, StringComparison.Ordinal);
        Assert.Contains("fired-once", row, StringComparison.Ordinal);

        scenario.StopMonitoring();
        Assert.True(scenario.IsEnabled("StartMonitorButton"));
        Assert.False(scenario.IsEnabled("StopMonitorButton"));
    }

    /// <summary>
    /// **監視したまま同じ id を録り直しても、止まらずにその 1 件だけ入れ替わること。**
    /// </summary>
    /// <remarks>
    /// <para>
    /// **assert の置き方は docs/TESTING.md §2 の 7 (監視中の録り直し) が定めている。**
    /// ホストは <c>RemoveAsync</c> → <c>AddAsync</c> の順に依存しており、逆順だと
    /// <c>AddAsync</c> が必ず投げる。しかも投げっぱなしの非同期なので、
    /// **順序を間違えても画面には何も出ず、ログ行が 1 本出るだけ**である。
    /// </para>
    /// <para>
    /// **真偽は「エラー行が無いこと」ではなく「録り直したあとも鳴ること」に置く。**
    /// 登録に失敗するとトリガーは消えたままになるので、鳴らなくなる。
    /// エラー行の不在は補助として見る (種類の違う失敗も拾えるため)。
    /// </para>
    /// <para>
    /// この失敗形は実在する。同じクラスの欠陥は製品側でも実際に起きた —
    /// <c>TriggerDraftValidator.Apply</c> が <c>On</c> を上書きするのに <c>PollInterval</c> を
    /// 落とさず、<c>CreateRuntime</c> が弾く定義が作れていた (docs/DESIGN.md §5)。
    /// </para>
    /// </remarks>
    [Fact]
    public void ReRecordingTheSameIdWhileMonitoring_KeepsItFiring()
    {
        using var scenario = ShowcaseScenario.Open(pickPoints: 2);
        string id = scenario.RecordAndCommit();

        scenario.StartMonitoring();
        scenario.WaitForALogRowContaining(id, "解決状態の行");
        scenario.ChangeTheTarget("before-rerecord");
        scenario.WaitForALogRowContaining("before-rerecord", "録り直す前の発火");

        // 同じ id のまま、もう一度確定してコミットする = 録り直し
        scenario.OpenAnotherPickerAndCommitAs(id);

        scenario.ChangeTheTarget("after-rerecord");
        string row = scenario.WaitForALogRowContaining("after-rerecord", "録り直したあとの発火");
        Assert.Contains(id, row, StringComparison.Ordinal);

        Assert.DoesNotContain(
            scenario.LogRowsSnapshot(),
            r => r.Contains(ErrorMarker, StringComparison.Ordinal));

        // 入れ替わっただけで増えていないこと (RemoveAsync が効いている)
        Assert.Single(scenario.SavedTriggers());
    }

    /// <summary>
    /// **対象アプリが落ちたら「未解決」の行が出ること** (解決状態の変化が両方向で見えること)。
    /// </summary>
    /// <remarks>
    /// 「解決」の行だけを見ていると、**一度解決したきり何も追わなくなった監視**でも緑になる。
    /// 消えたことに気づけるかどうかが、トリガーが黙って動かなくなる形
    /// (docs/DESIGN.md §8 のゾンビ要素) に対する唯一の観測点である。
    /// </remarks>
    [Fact]
    public void WhenTheTargetApplicationExits_AnUnresolvedRowAppears()
    {
        using var scenario = ShowcaseScenario.Open();
        string id = scenario.RecordAndCommit();

        scenario.StartMonitoring();
        scenario.WaitForALogRowContaining(ResolvedMarker, "解決の行");

        scenario.CloseTheTarget();

        string row = scenario.WaitForALogRowContaining(UnresolvedMarker, "未解決の行");
        Assert.Contains(id, row, StringComparison.Ordinal);
    }

    /// <summary>
    /// **監視したまま 1 件消すと、そのトリガーだけ止まり、他は動き続けること。**
    /// </summary>
    /// <remarks>
    /// <para>
    /// **「消したほうが鳴らない」だけでは足りない。**監視ごと止まってしまっても
    /// 同じように鳴らないので、それでは区別がつかない。**残したほうが鳴り続けること**が
    /// この項目の本体である (C1 —「1 件だけ触って他に触らない」)。
    /// </para>
    /// <para>
    /// **2 件は同じ要素に立てる** (id だけ変える)。別々の要素にするより厳しい —
    /// 片方を外したときにもう片方の購読まで落とすと、同じ要素なので即座に鳴らなくなる。
    /// </para>
    /// </remarks>
    [Fact]
    public void DeletingOneTriggerWhileMonitoring_LeavesTheOthersRunning()
    {
        const string Survivor = "keep-me";

        using var scenario = ShowcaseScenario.Open(pickPoints: 2);
        string doomed = scenario.RecordAndCommit();
        scenario.OpenAnotherPickerAndCommitAs(Survivor);
        Assert.Equal(2, scenario.SavedTriggers().Count);

        scenario.StartMonitoring();
        scenario.WaitForALogRowContaining(ResolvedMarker, "解決の行");

        scenario.DeleteTrigger(doomed);
        Assert.Single(scenario.SavedTriggers());

        // **ここが本体。**2 件は同じ要素を見ているので、片方を外したときに
        // もう片方の購読まで落とすと、これが鳴らなくなる
        scenario.ChangeTheTarget("still-alive");
        string row = scenario.WaitForALogRowContaining("still-alive", "残したトリガーの発火");
        Assert.Contains(Survivor, row, StringComparison.Ordinal);

        // 消したほうの id では、もう新しい行が出ない
        Assert.DoesNotContain(
            scenario.LogRowsSnapshot(),
            r => r.Contains("still-alive", StringComparison.Ordinal) &&
                 r.Contains(doomed, StringComparison.Ordinal));
    }

    /// <summary>
    /// **複数選択して「削除」を押すと、選択した全部が消えること。**
    /// </summary>
    /// <remarks>
    /// 一覧は「選択をまとめる」のために <c>SelectionMode=Extended</c> である。
    /// 削除が <c>SelectedIndex</c> (単一) を読むと、2 件選んで押しても
    /// **1 件しか消えず、例外も出ない**。
    /// 選択の読み方は「まとめる」と同じ <c>SelectedRanges</c> に揃える。
    /// </remarks>
    [Fact]
    public void DeletingWithTwoRowsSelected_RemovesBoth()
    {
        using var scenario = ShowcaseScenario.Open(pickPoints: 2);
        _ = scenario.RecordAndCommit();
        scenario.OpenAnotherPickerAndCommitAs("second-trigger");
        Assert.Equal(2, scenario.SavedTriggers().Count);

        scenario.SelectAllTriggerRowsAndDelete();

        Assert.Empty(scenario.SavedTriggers());
    }

    /// <summary>
    /// 「再読込」を押すと監視が止まること (一覧を丸ごと入れ替えるため。意図した挙動)。
    /// </summary>
    [Fact]
    public void Reloading_StopsMonitoring()
    {
        using var scenario = ShowcaseScenario.Open();
        _ = scenario.RecordAndCommit();

        scenario.StartMonitoring();
        Assert.True(scenario.IsEnabled("StopMonitorButton"));

        scenario.Reload();

        Assert.True(scenario.IsEnabled("StartMonitorButton"));
        Assert.False(scenario.IsEnabled("StopMonitorButton"));
    }

    /// <summary>
    /// 監視のスレッドとプロセスが、ウィンドウを閉じたあとに残らないこと。
    /// </summary>
    /// <remarks>
    /// 監視中のホストを閉じるので、
    /// <c>DisposeAsync</c> が発火の配送を待ち切ってから戻る経路をそのまま通る。
    /// </remarks>
    [Fact]
    public void ClosingTheWindowWhileMonitoring_LeavesNoProcessBehind()
    {
        int processId;
        using (var scenario = ShowcaseScenario.Open())
        {
            _ = scenario.RecordAndCommit();
            scenario.StartMonitoring();
            processId = scenario.ProcessId;
        }

        // Dispose がホストを閉じている。残っていれば、監視のスレッドが止まっていない
        Assert.True(
            Ui.Until(
                () => HasExited(processId) ? "ok" : null,
                Settle,
                "監視中のホストを閉じたらプロセスが残らないこと",
                () => $"pid={processId}") is not null);
    }

    /// <summary>
    /// **監視 UI を持つのは `App.WinUI` だけであること** (意図された非対称が保たれている)。
    /// </summary>
    /// <remarks>
    /// docs/TESTING.md §2 の 7 の言うとおり、**「無いこと」は自動で見るほうが確実である** —
    /// 人は「在るはずのものが無い」には気づくが、「無いはずのものが在る」は見落とす。
    /// </remarks>
    [Theory]
    [InlineData("WPF")]
    [InlineData("WinForms")]
    public void MonitoringIsAbsentFromTheOtherHosts(string profileName)
    {
        PickerHostProfile profile = PickerHostProfile.ByName(profileName);
        using var target = TestTargetProcess.Start(TargetProfile.WinForms);
        target.Send(DesktopLayout.PlaceTargetCommand);
        target.Send("add-button " + Target);
        target.Send("ping");

        (int x, int y) = target.CenterOf(Target);
        using var host = PickerHostProcess.Start(profile, Culture, (x, y));

        Assert.Null(host.MainWindow.ById("StartMonitorButton"));
        Assert.Null(host.MainWindow.ById("StopMonitorButton"));
        Assert.Null(host.MainWindow.ById("MonitorLogList"));
    }

    private static bool HasExited(int processId)
    {
        try
        {
            using var p = System.Diagnostics.Process.GetProcessById(processId);
            return p.HasExited;
        }
        catch (ArgumentException)
        {
            // 既に居ない
            return true;
        }
    }

    /// <summary>対象アプリ + `App.WinUI` ホスト + 捕捉済みのピッカー。</summary>
    private sealed class ShowcaseScenario : IDisposable
    {
        private readonly TestTargetProcess _target;
        private readonly PickerHostProcess _host;

        /// <summary>テストの途中で対象アプリを落としたか (Dispose で二重に落とさないため)。</summary>
        private bool _targetClosed;

        private ShowcaseScenario(TestTargetProcess target, PickerHostProcess host)
        {
            _target = target;
            _host = host;
        }

        public int ProcessId => _host.ProcessId;

        /// <param name="pickPoints">
        /// 用意する pick 点の数。**ピッカーは n 枚目が n 番目を受け取る**ので、
        /// 2 枚目を開くテストは 2 つ要る。
        /// </param>
        public static ShowcaseScenario Open(int pickPoints = 1)
        {
            PickerHostProfile profile = PickerHostProfile.ByName("WinUI");
            TestTargetProcess target = TestTargetProcess.Start(TargetProfile.WinForms);
            try
            {
                target.Send(DesktopLayout.PlaceTargetCommand);
                target.Send("add-button " + Target);
                target.Send("ping");

                (int x, int y) = target.CenterOf(Target);
                PickerHostProcess host = PickerHostProcess.Start(
                    profile, Culture, [.. Enumerable.Repeat((x, y), pickPoints)]);
                try
                {
                    host.OpenPicker();
                    var scenario = new ShowcaseScenario(target, host);
                    scenario.WaitForTheTargetToBeSelected();
                    return scenario;
                }
                catch
                {
                    host.Dispose();
                    throw;
                }
            }
            catch
            {
                target.Dispose();
                throw;
            }
        }

        public IReadOnlyList<TriggerDefinition> SavedTriggers() => _host.SavedTriggers();

        /// <summary>
        /// 保存済みのトリガー。**読めない瞬間がある**ので、待ちの中からはこちらを使う。
        /// </summary>
        /// <remarks>
        /// ホストは原子的な置き換え (<c>AtomicFile</c>) で書くため、覗いた瞬間に
        /// ファイルが存在しないことがある。待ちの述語から素の <see cref="SavedTriggers"/> を
        /// 呼ぶと、**「まだ書かれていない」が例外として現れて待ちが打ち切られる**。
        /// **実際にこれで 1 度落ちた**。
        /// </remarks>
        private IReadOnlyList<TriggerDefinition> SavedTriggersOrEmpty()
        {
            try
            {
                return SavedTriggers();
            }
            catch (IOException)
            {
                // 置き換えの最中。次の周で読み直す
                return [];
            }
        }

        public string Diagnostics() => _host.Diagnostics();

        private AutomationElement Picker() => _host.PickerWindow();

        /// <summary>行を確定してコミットし、そのトリガー id を返す。</summary>
        public string RecordAndCommit()
        {
            AutomationElement row = _host.Tree().SelectedRow()
                ?? throw new InvalidOperationException("行が選択されていません。");
            ConfirmButtonOf(row).Invoke();
            _ = Ui.Until(
                () => Picker().ById("CommitButton") is { } b && b.Current.IsEnabled ? "ok" : null,
                Settle,
                "確定してコミットできるようになること",
                Diagnostics);

            string id = Picker().RequireById("KeyBox").ValueOf() ?? string.Empty;
            Assert.NotEmpty(id);

            string before = Picker().ById("CommitStatus")?.NameOf() ?? string.Empty;
            Picker().RequireById("CommitButton").Invoke();
            _ = Ui.Until(
                () => Picker().ById("CommitStatus")?.NameOf() is { Length: > 0 } now &&
                      !string.Equals(now, before, StringComparison.Ordinal) ? now : null,
                Settle,
                "コミットの結果が出ること",
                Diagnostics);
            return id;
        }

        /// <summary>
        /// 監視を開始し、**本当に始まるまで**待つ。
        /// </summary>
        /// <remarks>
        /// **「開始ボタンが無効になったこと」で待ってはいけない。**ホスト側は押された瞬間に
        /// 開始ボタンを無効にし、停止ボタンを有効にするのは**監視の開始が完了してから**である
        /// (失敗すれば開始ボタンは有効に戻る)。前者で待つと、まだ購読が張られていない時点で
        /// 先へ進み、そのあとの変化を取りこぼす。**実際にこれで 1 度落ちた。**
        /// </remarks>
        /// <summary>
        /// 2 枚目のピッカーを開いて、**同じ id で**確定・コミットする (= 録り直し)。
        /// </summary>
        /// <remarks>
        /// 「ピッカーで追加」は既に開いていれば前面に出すだけなので、2 枚目はこちらでしか開かない。
        /// </remarks>
        public void OpenAnotherPickerAndCommitAs(string id)
        {
            _host.OpenAnotherPicker();
            // **操作はすべて「2 枚目」と特定した 1 枚の中で行う。**Picker() (= 最初に
            // 見つかった 1 枚) を使うと、UIA の列挙順は Z オーダーで動くので、id の書き込みと
            // コミットが 1 枚目に届きうる — 1 枚目は自分の KeyBox (元の id) のまま再コミット
            // するので、「id がファイルに入ること」が待ち切る (実測済み。
            // CompositeShowcaseTests.PickerShowing と同じ取り違え)。
            // ここでは 2 枚とも**同じ要素**を捕捉するので行の中身では見分けられない —
            // **CommitStatus がまだ空であること**が「新しい 1 枚」の定義になる
            AutomationElement picker = Ui.Until(
                () => _host.PickerWindows().FirstOrDefault(w =>
                    w.ById(_host.Profile.TreeAutomationId) is not null &&
                    string.IsNullOrEmpty(w.ById("CommitStatus")?.NameOf())),
                Settle,
                "まだコミットしていない 2 枚目のピッカー",
                Diagnostics);
            AutomationElement row = Ui.Until(
                () => picker.ById(_host.Profile.TreeAutomationId)?.SelectedRow() is { } r &&
                      r.NameOf()?.Contains($"[{Target}]", StringComparison.Ordinal) == true ? r : null,
                Settle,
                "2 枚目のピッカーが対象を捕捉すること",
                Diagnostics);
            ConfirmButtonOf(row).Invoke();
            _ = Ui.Until(
                () => picker.ById("CommitButton") is { } b && b.Current.IsEnabled ? "ok" : null,
                Settle,
                "2 枚目で確定してコミットできるようになること",
                Diagnostics);

            // 同じ id にする。ここが違うと「入れ替え」ではなく「追加」になり、
            // RemoveAsync -> AddAsync の順序を確かめたことにならない。
            // Eventually で待つのは確定直後のレイアウト変化への備え (RequireByIdEventually の remarks)
            picker.RequireByIdEventually("KeyBox", Diagnostics).SetText(id);
            picker.RequireByIdEventually("CommitButton", Diagnostics).Invoke();
            // 件数は見ない — 同じ id なら入れ替わり、違う id なら 1 件増える。
            // どちらの使い方もあるので、ここでは「その id が在ること」だけを待つ
            _ = Ui.Until(
                () => SavedTriggersOrEmpty().Any(t => string.Equals(t.Id, id, StringComparison.Ordinal))
                    ? "ok" : null,
                Settle,
                $"'{id}' がファイルに入ること",
                Diagnostics);
        }

        public void StartMonitoring()
        {
            _host.MainWindow.RequireByIdEventually("StartMonitorButton", Diagnostics).Invoke();
            _ = Ui.Until(
                () => IsEnabled("StopMonitorButton") ? "ok" : null,
                Settle,
                "監視が開始され、停止ボタンが有効になること",
                Diagnostics);
        }

        public void StopMonitoring()
        {
            _host.MainWindow.RequireByIdEventually("StopMonitorButton", Diagnostics).Invoke();
            _ = Ui.Until(
                () => IsEnabled("StartMonitorButton") ? "ok" : null,
                Settle,
                "監視の停止でボタンの活性が戻ること",
                Diagnostics);
        }

        public bool IsEnabled(string automationId) =>
            _host.MainWindow.RequireByIdEventually(automationId, Diagnostics).Current.IsEnabled;

        /// <summary>対象アプリのボタンの Name を変える (UIA の通知が上がる変え方)。</summary>
        public void ChangeTheTarget(string text) => _target.Send($"set-text {Target} {text}");

        /// <summary>一覧で該当の行を選んで「削除」を押す。</summary>
        public void DeleteTrigger(string id)
        {
            AutomationElement item = Ui.Until(
                () => _host.MainWindow.RequireByIdEventually("TriggerList", Diagnostics)
                    .FindAll(
                        TreeScope.Descendants,
                        new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.ListItem))
                    .Cast<AutomationElement>()
                    .FirstOrDefault(e => e.NameOf()?.Contains(id, StringComparison.Ordinal) == true),
                Settle,
                $"一覧に '{id}' の行が在ること",
                Diagnostics);

            if (item.TryGetCurrentPattern(SelectionItemPattern.Pattern, out object? pattern))
            {
                ((SelectionItemPattern)pattern).Select();
            }
            _host.MainWindow.RequireByIdEventually("DeleteButton", Diagnostics).Invoke();

            _ = Ui.Until(
                () => SavedTriggersOrEmpty().All(t => !string.Equals(t.Id, id, StringComparison.Ordinal))
                    ? "ok" : null,
                Settle,
                $"'{id}' がファイルから消えること",
                Diagnostics);
        }

        /// <summary>
        /// 一覧の全行を選んで「削除」を押し、**ファイルが空になるまで**待つ。
        /// </summary>
        /// <remarks>
        /// 行はピッカーに覆われたままでも UIA に出るし、<c>SelectionItemPattern</c> も効く
        /// (<c>CompositeShowcaseTests</c> で実測済み)。
        /// </remarks>
        public void SelectAllTriggerRowsAndDelete()
        {
            AutomationElement list = _host.MainWindow.RequireByIdEventually("TriggerList", Diagnostics);
            AutomationElement[] rows = Ui.Until(
                () => list
                    .FindAll(
                        TreeScope.Descendants,
                        new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.ListItem))
                    .Cast<AutomationElement>()
                    .ToArray() is { Length: >= 2 } found ? found : null,
                Settle,
                "一覧に 2 件以上あること",
                Diagnostics);
            bool first = true;
            foreach (AutomationElement row in rows)
            {
                if (!row.TryGetCurrentPattern(SelectionItemPattern.Pattern, out object? pattern))
                {
                    continue;
                }
                var selection = (SelectionItemPattern)pattern;
                if (first)
                {
                    selection.Select();
                    first = false;
                }
                else
                {
                    selection.AddToSelection();
                }
            }
            _host.MainWindow.RequireByIdEventually("DeleteButton", Diagnostics).Invoke();
            _ = Ui.Until(
                () => SavedTriggersOrEmpty().Count == 0 ? "ok" : null,
                Settle,
                "選択した全件がファイルから消えること",
                Diagnostics);
        }

        /// <summary>対象アプリを終了させる (解決していたものが消える経路)。</summary>
        public void CloseTheTarget()
        {
            _target.Dispose();
            _targetClosed = true;
        }

        /// <summary>「再読込」を押す。一覧を丸ごと入れ替えるので監視は止まる。</summary>
        public void Reload()
        {
            _host.MainWindow.RequireByIdEventually("ReloadButton", Diagnostics).Invoke();
            _ = Ui.Until(
                () => IsEnabled("StartMonitorButton") ? "ok" : null,
                Settle,
                "再読込で監視が止まること",
                Diagnostics);
        }

        /// <summary>ログ一覧に指定の文字列を含む行が出るまで待ち、その行を返す。</summary>
        public string WaitForALogRowContaining(string fragment, string what) => Ui.Until(
            () => LogRows().FirstOrDefault(r => r.Contains(fragment, StringComparison.Ordinal)),
            Fire,
            $"{what} ('{fragment}' を含む行) がログ一覧に出ること",
            () => Diagnostics() + Environment.NewLine +
                  "ログ一覧: " + string.Join(" / ", LogRows()));

        /// <summary>いまのログ一覧 (「出ていないこと」を見るため)。</summary>
        public string[] LogRowsSnapshot() => LogRows();

        private string[] LogRows()
        {
            AutomationElement? list = _host.MainWindow.ById("MonitorLogList");
            if (list is null)
            {
                return [];
            }
            return [.. list
                .FindAll(
                    TreeScope.Descendants,
                    new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.ListItem))
                .Cast<AutomationElement>()
                .Select(e => e.NameOf() ?? string.Empty)];
        }

        private void WaitForTheTargetToBeSelected() => Ui.Until(
            () => _host.Tree().SelectedRow()?.NameOf() is { } name &&
                  name.Contains($"[{Target}]", StringComparison.Ordinal)
                ? name
                : null,
            Settle,
            $"ホバー捕捉で {Target} の行が選択されること",
            Diagnostics);

        /// <summary>行の確定ボタン。その行自身のものを取る。</summary>
        private static AutomationElement ConfirmButtonOf(AutomationElement row)
        {
            AutomationElement? nestedRow = row.FindFirst(
                TreeScope.Children,
                new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.TreeItem));
            foreach (AutomationElement button in row.FindAll(
                TreeScope.Descendants,
                new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.Button)))
            {
                if (!button.TryGetCurrentPattern(InvokePattern.Pattern, out _))
                {
                    continue;
                }
                if (nestedRow is not null &&
                    nestedRow.FindFirst(
                        TreeScope.Descendants,
                        new PropertyCondition(
                            AutomationElement.RuntimeIdProperty, button.GetRuntimeId())) is not null)
                {
                    continue;
                }
                return button;
            }
            throw new InvalidOperationException($"行 '{row.NameOf()}' に確定ボタンがありません。");
        }

        public void Dispose()
        {
            _host.Dispose();
            if (!_targetClosed)
            {
                _target.Dispose();
            }
        }
    }
}
