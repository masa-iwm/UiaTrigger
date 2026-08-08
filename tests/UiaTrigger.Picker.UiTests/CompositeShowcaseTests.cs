using System.Windows.Automation;
using UiaTrigger.Models;
using UiaTrigger.RealUia.Tests;
using Xunit;

namespace UiaTrigger.Picker.UiTests;

/// <summary>
/// 複合条件 (docs/DESIGN.md §4) を、ピッカーから監視まで通しで見る
/// (docs/MANUAL-CHECKS.md §9 の「複合条件」の節)。
///
/// <para>
/// **別プロセスを 2 つ立てる。**同一ウィンドウ内の 2 要素だと、スロットが割れていようが
/// まとまっていようが素通りしてしまい何も証明しない。プロセスをまたぐと
/// 「ウィンドウごとに解決し、ウィンドウごとに購読する」が本当に効いていないと通らない。
/// </para>
/// <para>
/// **片方だけ満たしたときに鳴らないこと (ネガティブコントロール) がこの一群の主眼である。**
/// 「両方満たしたら鳴った」だけなら、条件を無視して鳴っているだけかもしれない。
/// docs/TESTING.md §2 の 7 も「片方だけでは鳴らない」(発火行の不在を待ち切る) をいちばん大事と書いている。
/// </para>
/// <para>
/// T3 の <c>CompositeScenarioTests</c> は**ライブラリを直に**叩いて同じ性質を見ている。
/// こちらは**ホストの画面から**作った定義で同じことが成り立つかを見る —
/// 「まとめる」の UI が式や句ごとの要素を正しく組み立てているか、はここでしか分からない。
/// </para>
///
/// <para>
/// **ピッカーは閉じずに進める。**「ピッカーがメインウィンドウを覆っていると
/// WinUI の <c>ListView</c> が項目を実体化せず、<c>TriggerList</c> の行が UIA に出ない」
/// という仮説は**実測で崩れている**: 行は 2 枚のピッカーに完全に覆われたまま
/// 1 秒以内に UIA へ出る。「行が出ない」ように見える本当の原因は
/// <see cref="CompositeScenario.PickerShowing"/> の remarks にある取り違えで、
/// 2 件録ったつもりで 1 件目を上書きすると、一覧が永遠に 1 行になる。
/// </para>
/// <para>
/// ついでに記録しておく: <c>WindowPattern.Close()</c> は 2 枚のうち**1 枚しか閉じなかった**
/// (どちらも <c>WindowPattern</c> を持ち、<c>Close()</c> は例外なく返る)。閉じる必要が
/// なくなったので原因は追っていない。もし将来ピッカーを UIA から閉じたくなったら、
/// タイトルバーの閉じるボタン (AutomationId <c>'Close'</c>, <c>InvokePattern</c> あり) が
/// 見えることを同じ実測で確かめてある。
/// </para>
/// <para>
/// **演算子を既定 (<c>Always</c>) のまま録ってはいけない。**それでは
/// **まとめた時点で両方の句が成立している**ため、監視を始めた瞬間に鳴る
/// (<c>FIRED [composite-1] WhileMatching : '' → 'btnA'</c> がログに出る)。
/// 「片方だけでは鳴らない」を見るには、**どちらも成立していない状態から始める**必要が
/// あるので、<c>Equals</c> + 値で録っている。
/// </para>
/// </summary>
public sealed class CompositeShowcaseTests
{
    private static readonly TimeSpan Settle = TimeSpan.FromSeconds(20);

    private static readonly TimeSpan Fire = TimeSpan.FromSeconds(30);

    /// <summary>鳴らないことを見る窓。</summary>
    private static readonly TimeSpan NoFire = TimeSpan.FromSeconds(5);

    private const string Culture = "en-US";

    private const string ButtonA = "btnA";

    private const string ButtonB = "btnB";

    private const string FiredMarker = "FIRED";

    /// <summary>A の条件が成立する値。**初期値ではない**ので、最初は成立していない。</summary>
    private const string ExpectedA = "ready";

    /// <summary>B の条件が成立する値。同上。</summary>
    private const string ExpectedB = "done";

    /// <summary>
    /// **2 つのアプリの条件をまとめ、片方だけでは鳴らず、両方揃うと 1 回だけ鳴ること。**
    /// </summary>
    /// <remarks>
    /// <para>
    /// 見ているのは 5 つ:
    /// (1) 「選択をまとめる」が 2 件から 1 件の複合条件を作ること、
    /// (2) 保存された JSON に <c>Expression</c> と**句ごとの** <c>Window</c> / <c>Locator</c> が入ること、
    /// (3) 監視を始めると状態欄に**「要素 2 個」**と出ること、
    /// (4) **片方だけ満たしても鳴らない**こと、
    /// (5) もう片方も満たすと**1 回だけ**鳴ること。
    /// </para>
    /// <para>
    /// (2) が要る理由: 句ごとの要素が入っていなければ、複合条件は「同じ 1 要素に対する
    /// 2 つの条件」に退化する。それでも (4)(5) は**たまたま通りうる**ので、
    /// ファイルの中身を直接見る。
    /// </para>
    /// <para>
    /// (3) の数字は <c>TriggerMonitorDiagnostics.ElementSlotCount</c> そのもの、つまり
    /// **ライブラリが実際にいくつの要素を見張るか**である (§9 が同じ項目を持つ)。
    /// スロットは**トリガーごと**に (Window, Locator) でまとまる (docs/DESIGN.md §4 の
    /// <c>SlotBuilder</c>) ので、元の 2 件が一覧に残るこの流れでは
    /// **4 = 元 A (1) + 元 B (1) + 複合の 2 句 (2)** が設計どおりの値である。
    /// 複合の句が同じ 1 要素へ潰れる退行では 3 になるので、4 の主張が
    /// 「句ごとの要素が別々に監視へ渡った」の検査になっている。
    /// </para>
    /// <para>
    /// **発火行は「<c>FIRED</c> かつ複合条件の id」で探す。**まとめた後も元の 2 件は
    /// ファイルに残って一緒に監視されるので、片方を変えた時点で**元トリガー自身の**
    /// <c>FIRED</c> 行が先に出る。<c>FIRED</c> だけで探すと、最初に見つかるのはそちらである。
    /// </para>
    /// </remarks>
    [Fact]
    public void CombiningTwoApplications_FiresOnlyWhenBothHold()
    {
        using var scenario = CompositeScenario.Open();

        string a = scenario.RecordFirst();
        string b = scenario.RecordSecond();
        Assert.NotEqual(a, b);

        scenario.Combine();

        // まとまった 1 件を確かめる。**ファイルの中身で見る** —
        // 画面の「まとめました」は、句ごとの要素が落ちていても同じように出る
        TriggerDefinition composite = Assert.Single(
            scenario.SavedTriggers(), t => t.Clauses.Count == 2);
        Assert.Equal(2, composite.Clauses.Count);
        Assert.All(composite.Clauses, c => Assert.NotNull(c.Window));
        Assert.All(composite.Clauses, c => Assert.NotNull(c.Locator));
        Assert.NotEqual(
            composite.Clauses[0].Locator!.Steps[^1].AutomationId,
            composite.Clauses[1].Locator!.Steps[^1].AutomationId);

        scenario.StartMonitoring();
        // 要素数 (remarks の (3))。数えているのはライブラリであり、句ごとの要素が本当に
        // 別々に監視へ渡ったことの、画面から見える唯一の証拠である。
        // 4 = 元 A + 元 B + 複合の 2 句。複合の句が 1 要素へ潰れる退行では 3 になる
        scenario.WaitForTheStatusToContain("4 element(s)", "要素数の表示");
        scenario.WaitForALogRowContaining(composite.Id, "複合条件の解決");

        // (4) 片方だけ。**ここが主眼である**
        scenario.SetFirst(ExpectedA);
        scenario.AssertNoFire(composite.Id);

        // (5) もう片方も満たすと 1 回だけ
        scenario.SetSecond(ExpectedB);
        _ = scenario.WaitForTheFiredRowOf(composite.Id);
        scenario.AssertNoSecondFire(composite.Id);
    }

    /// <summary>
    /// **壊れた式は、まとめる前にその場で断られること** (トリガーが作られないこと)。
    /// </summary>
    /// <remarks>
    /// 監視を開始してから <c>ArgumentException</c> になるのでは遅い。
    /// 式は入力中に検証できる唯一の場所であり、`TriggerDraftValidator.ValidateExpression` は
    /// そのために public にしてある。
    /// </remarks>
    [Theory]
    [InlineData("a &&")]
    [InlineData("a & b")]
    public void ABrokenExpression_IsRefusedAndAddsNothing(string expression)
    {
        using var scenario = CompositeScenario.Open();
        _ = scenario.RecordFirst();
        _ = scenario.RecordSecond();
        int before = scenario.SavedTriggers().Count;

        scenario.CombineWithExpression(expression, expectSuccess: false);

        Assert.Equal(before, scenario.SavedTriggers().Count);
    }

    /// <summary>2 つの対象アプリ + `App.WinUI` ホスト。</summary>
    private sealed class CompositeScenario : IDisposable
    {
        private readonly TestTargetProcess _first;
        private readonly TestTargetProcess _second;
        private readonly PickerHostProcess _host;

        private CompositeScenario(
            TestTargetProcess first, TestTargetProcess second, PickerHostProcess host)
        {
            _first = first;
            _second = second;
            _host = host;
        }

        public static CompositeScenario Open()
        {
            DesktopLayout.RequireTwoTargetsFitOnThisScreen();

            PickerHostProfile profile = PickerHostProfile.ByName("WinUI");
            TestTargetProcess first = TestTargetProcess.Start(TargetProfile.WinForms);
            try
            {
                first.Send(DesktopLayout.PlaceTargetCommand);
                first.Send("add-button " + ButtonA);
                first.Send("ping");

                TestTargetProcess second = TestTargetProcess.Start(TargetProfile.WinForms);
                try
                {
                    second.Send(DesktopLayout.PlaceSecondTargetCommand);
                    second.Send("add-button " + ButtonB);
                    second.Send("ping");

                    // 重なっていないことを確かめる。重なると記録が別の窓を掴み、
                    // 「複合条件を確かめた」と思ったまま何も確かめないテストになる
                    (int _, int aTop, int _, int aBottom) = first.RectOf(ButtonA);
                    (int _, int bTop, int _, int bBottom) = second.RectOf(ButtonB);
                    // RectOf は (Left, Top, Right, Bottom)
                    Assert.True(
                        bBottom < aTop || aBottom < bTop,
                        $"2 つの対象アプリのボタンが縦に重なっています (A {aTop}..{aBottom} / B {bTop}..{bBottom})。");

                    PickerHostProcess host = PickerHostProcess.Start(
                        profile, Culture, first.CenterOf(ButtonA), second.CenterOf(ButtonB));
                    try
                    {
                        return new CompositeScenario(first, second, host);
                    }
                    catch
                    {
                        host.Dispose();
                        throw;
                    }
                }
                catch
                {
                    second.Dispose();
                    throw;
                }
            }
            catch
            {
                first.Dispose();
                throw;
            }
        }

        public IReadOnlyList<TriggerDefinition> SavedTriggers() => _host.SavedTriggers();

        public string Diagnostics() => _host.Diagnostics();

        public string RecordFirst()
        {
            _host.OpenPicker();
            return ConfirmAndCommit(ButtonA, ExpectedA);
        }

        public string RecordSecond()
        {
            _host.OpenAnotherPicker();
            return ConfirmAndCommit(ButtonB, ExpectedB);
        }

        /// <summary>
        /// **その行を捕捉しているピッカーの窓。**2 枚のピッカーは AutomationId でも
        /// タイトルでも区別できない (<see cref="PickerHostProcess.PickerWindows"/> の remarks)
        /// ので、**選択されている行の中身**で見分ける。
        /// </summary>
        /// <remarks>
        /// ここを <see cref="PickerHostProcess.PickerWindow"/> (= 最初に見つかった 1 枚)
        /// にしてはいけない。UIA の列挙順は Z オーダーで動くため、**2 枚目を操作した
        /// つもりの確定・コミットが 1 枚目に届きうる**。実際にそうなることを
        /// 実測している: 2 件目の KeyBox として 1 枚目のものを読むと id が
        /// 1 件目と同じになり、コミットは**1 件目の上書き**になる。ファイルには
        /// 永遠に 1 件しか無いので、一覧の「2 件以上」の待ちが必ず待ち切る —
        /// 症状だけ見ると「ListView が実体化しない」に見える。
        /// </remarks>
        private AutomationElement PickerShowing(string button) => Ui.Until(
            () => _host.PickerWindows().FirstOrDefault(w =>
                w.ById(_host.Profile.TreeAutomationId) is { } tree &&
                tree.SelectedRow()?.NameOf()?.Contains($"[{button}]", StringComparison.Ordinal) == true),
            Settle,
            $"{button} を捕捉しているピッカー",
            Diagnostics);

        /// <summary>
        /// いま捕捉されている行を確定し、**「Name が指定の値と等しい」**にしてコミットする。
        /// **すべての操作を <see cref="PickerShowing"/> で特定した 1 枚の中で行う。**
        /// </summary>
        /// <remarks>
        /// **既定の演算子 (<c>Always</c>) では検査にならない。**まとめた時点で両方の句が
        /// 最初から成立しているので、<c>FireOnInitialMatch</c> で**監視を始めた瞬間に鳴る** —
        /// 「片方だけでは鳴らない」を見ようがない。**実際に 1 度それで落ちた**
        /// (ログに <c>FIRED [composite-1] WhileMatching : '' → 'btnA'</c> が出た)。
        /// 最初は**どちらも成立していない**状態から始める必要がある。
        /// </remarks>
        private string ConfirmAndCommit(string button, string expected)
        {
            AutomationElement picker = PickerShowing(button);
            AutomationElement row = Ui.Until(
                () => picker.ById(_host.Profile.TreeAutomationId)?.SelectedRow(),
                Settle,
                $"ピッカーが {button} を捕捉すること",
                Diagnostics);
            row.ConfirmButtonOf().Invoke();
            _ = Ui.Until(
                () => picker.ById("CommitButton") is { } b && b.Current.IsEnabled ? "ok" : null,
                Settle,
                "確定してコミットできるようになること",
                Diagnostics);

            // 「Name が expected と等しい」にする。演算子を変えると欄の出し分けが走るので、
            // 値の欄が出てくるのを待ってから書く
            picker.RequireByIdEventually("CondCombo", Diagnostics).SelectComboItem("Equals");
            AutomationElement operand = Ui.Until(
                () => picker.ById("TextOperand"),
                Settle,
                "値の欄が出ること (演算子の出し分けが済むこと)",
                Diagnostics);
            operand.SetText(expected);

            // Eventually で待つこと。演算子の出し分けはレイアウトを動かすので、
            // その直後は**既に在る要素が一時的に UIA から消える**
            // (RequireByIdEventually の remarks)。一発の RequireById は
            // 「'KeyBox' が見つかりません」という顔で間欠に落ちる (実測で約 1/3)
            string id = picker.RequireByIdEventually("KeyBox", Diagnostics).ValueOf() ?? string.Empty;
            Assert.NotEmpty(id);
            picker.RequireByIdEventually("CommitButton", Diagnostics).Invoke();
            _ = Ui.Until(
                () => SavedTriggersOrEmpty().Any(t => string.Equals(t.Id, id, StringComparison.Ordinal))
                    ? "ok" : null,
                Settle,
                $"'{id}' がファイルに入ること",
                Diagnostics);
            return id;
        }

        /// <summary>一覧の 2 件を選んで「選択をまとめる」を押す (式は既定 = All)。</summary>
        public void Combine() => CombineWithExpression(expression: null, expectSuccess: true);

        public void CombineWithExpression(string? expression, bool expectSuccess)
        {
            SelectAllTriggerRows();
            if (expression is not null)
            {
                _host.MainWindow.RequireByIdEventually("ExpressionText", Diagnostics).SetText(expression);
            }

            int before = SavedTriggersOrEmpty().Count;
            _host.MainWindow.RequireByIdEventually("CombineButton", Diagnostics).Invoke();

            if (expectSuccess)
            {
                _ = Ui.Until(
                    () => SavedTriggersOrEmpty().Any(t => t.Clauses.Count == 2) ? "ok" : null,
                    Settle,
                    "まとめた 1 件がファイルに入ること",
                    Diagnostics);
                return;
            }

            // 断られる側。**「増えていない」を待ち切って確かめる** —
            // 押した直後に数えると、まだ書かれていないだけで通ってしまう
            Ui.Never(
                () => SavedTriggersOrEmpty().Count != before,
                NoFire,
                "壊れた式でトリガーが増えないこと",
                Diagnostics);
        }

        /// <summary>
        /// 一覧の全行を選ぶ。**ピッカーは開いたままでよい。**
        /// </summary>
        /// <remarks>
        /// メインウィンドウはこの時点で 2 枚のピッカーに完全に覆われている
        /// (退かし先は全部同じ矩形 — <see cref="DesktopLayout.Host"/>) が、
        /// **覆われていても行は UIA に出るし、<c>SelectionItemPattern</c> も効く**
        /// (実測済み。<c>MonitorShowcaseTests</c> の削除テストも同じ形で通っている)。
        /// </remarks>
        private void SelectAllTriggerRows()
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
                // 1 つ目は Select (それまでの選択を捨てる)、以降は AddToSelection
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
        }

        public void StartMonitoring()
        {
            _host.MainWindow.RequireByIdEventually("StartMonitorButton", Diagnostics).Invoke();
            _ = Ui.Until(
                () => _host.MainWindow.RequireByIdEventually("StopMonitorButton", Diagnostics)
                    .Current.IsEnabled ? "ok" : null,
                Settle,
                "監視が開始され、停止ボタンが有効になること",
                Diagnostics);
        }

        public void SetFirst(string text) => _first.Send($"set-text {ButtonA} {text}");

        public void SetSecond(string text) => _second.Send($"set-text {ButtonB} {text}");

        /// <summary>状態欄に指定の文字列が出るまで待つ。</summary>
        public void WaitForTheStatusToContain(string fragment, string what) => Ui.Until(
            () => _host.MainWindow.ById("StatusText")?.NameOf() is { } status &&
                  status.Contains(fragment, StringComparison.Ordinal) ? status : null,
            Settle,
            $"{what} ('{fragment}' を含む状態欄)",
            () => Diagnostics() + Environment.NewLine +
                  "状態欄: " + (_host.MainWindow.ById("StatusText")?.NameOf() ?? "(無し)"));

        public string WaitForALogRowContaining(string fragment, string what) => Ui.Until(
            () => LogRows().FirstOrDefault(r => r.Contains(fragment, StringComparison.Ordinal)),
            Fire,
            $"{what} ('{fragment}' を含む行) がログ一覧に出ること",
            () => Diagnostics() + Environment.NewLine + "ログ一覧: " + string.Join(" / ", LogRows()));

        /// <summary>
        /// その id の**発火**行が出るまで待つ。
        /// </summary>
        /// <remarks>
        /// <c>FIRED</c> だけで探してはいけない。まとめた後も元の 2 件は残って一緒に
        /// 監視されるので、片方を動かした時点で**元トリガーの発火行が先に出ている**。
        /// </remarks>
        public string WaitForTheFiredRowOf(string id) => Ui.Until(
            () => LogRows().FirstOrDefault(r => IsFiredRowOf(r, id)),
            Fire,
            $"'{id}' の発火行がログ一覧に出ること",
            () => Diagnostics() + Environment.NewLine + "ログ一覧: " + string.Join(" / ", LogRows()));

        /// <summary>その id の**発火**行が出ないこと (解決の行は出てよい)。</summary>
        public void AssertNoFire(string id) => Ui.Never(
            () => LogRows().Any(r => IsFiredRowOf(r, id)),
            NoFire,
            $"'{id}' が片方だけでは鳴らないこと",
            () => Diagnostics() + Environment.NewLine + "ログ一覧: " + string.Join(" / ", LogRows()));

        /// <summary>その id の発火行が**1 行のまま増えない**こと (「1 回だけ鳴る」の後半)。</summary>
        public void AssertNoSecondFire(string id) => Ui.Never(
            () => LogRows().Count(r => IsFiredRowOf(r, id)) > 1,
            NoFire,
            $"'{id}' が 1 回だけ鳴ること",
            () => Diagnostics() + Environment.NewLine + "ログ一覧: " + string.Join(" / ", LogRows()));

        private static bool IsFiredRowOf(string row, string id)
            => row.Contains(FiredMarker, StringComparison.Ordinal) &&
               row.Contains(id, StringComparison.Ordinal);

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

        private IReadOnlyList<TriggerDefinition> SavedTriggersOrEmpty()
        {
            try
            {
                return SavedTriggers();
            }
            catch (IOException)
            {
                // 原子的な置き換えの最中。次の周で読み直す
                return [];
            }
        }


        public void Dispose()
        {
            _host.Dispose();
            _second.Dispose();
            _first.Dispose();
        }
    }
}
