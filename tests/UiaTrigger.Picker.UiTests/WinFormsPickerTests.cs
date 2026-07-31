// T4: Windows Forms のピッカーにしか無い UI (docs/TESTING.md §1)。
//
// **この変種を [Theory] で横断させない。**ツリーの同期は TreeMirrorTests が
// ウィンドウ無しで丸ごと覆っているので、広げると二重になるだけである
// (docs/DESIGN.md §12)。ここに在るのは
// **他の 2 変種に存在しない UI** のためのものだけである。
//
// 行の中にボタンを置けないので、Windows Forms だけは
// 「選択中の行を確定する」ボタンで確定する。配線は 1 行:
//
//     _confirmNode.Click += (_, _) => _presenter.ConfirmNode(_tree.SelectedNode?.Tag as PickerTreeNode);
//
// Tag にノードが入っていなければ as は null になり、ConfirmNode(null) は
// **例外を出さずに何もしない**。TreeMirror が Tag を設定し続けることへの暗黙の依存である。
using System.Windows.Automation;
using UiaTrigger.Models;
using UiaTrigger.RealUia.Tests;
using Xunit;

namespace UiaTrigger.Picker.UiTests;

public sealed class WinFormsPickerTests
{
    private static readonly TimeSpan Settle = TimeSpan.FromSeconds(25);

    private const string Culture = "en-US";
    private const string Target = "btn-three";

    private static readonly string[] Buttons = ["btn-one", "btn-two", "btn-three"];

    /// <summary>
    /// 「選択中の行を確定する」ボタンで確定でき、コミットまで通ること。
    /// </summary>
    /// <remarks>
    /// <para>
    /// **T1 では書けない。**Windows Forms は仮想化が無いのでノードはウィンドウ無しでも
    /// 存在するが、<c>Button.PerformClick()</c> は <c>CanSelect</c> (= 可視かつ有効) を要求し、
    /// <c>Show()</c> していないフォームでは何も起きない。ウィンドウを出す検査は
    /// T1 の担当ではない (docs/TESTING.md §2) ので T4 が引き取る。
    /// </para>
    /// <para>
    /// **コミットまで見るのは、確定の表示だけでは足りないからである。**
    /// <c>ConfirmNode</c> が動いたことは <c>ConfirmedText</c> に出るが、
    /// そこで見えているのは presenter の中の話でしかない。
    /// トリガーファイルに正しい要素が入って初めて、この UI が仕事をしたと言える。
    /// </para>
    /// <para>
    /// 退行: <c>TreeMirror</c> の <c>Tag</c> 設定を外す → <c>as</c> が null になり、
    /// **例外なしで何も起きない** → 確定が来ないので落ちる。
    /// </para>
    /// </remarks>
    [Fact]
    public void TheConfirmButton_ConfirmsTheSelectedRow_AndTheTriggerCanBeCommitted()
    {
        using var scenario = WinFormsScenario.AimedAt(Target);
        scenario.WaitForTheTargetToBeSelected();

        scenario.Picker().RequireById("ConfirmNodeButton").Invoke();
        scenario.WaitUntilCommitIsPossible();

        Assert.Equal(
            string.Format(
                System.Globalization.CultureInfo.InvariantCulture,
                scenario.Resource("Confirmed"),
                $"Button: {Target}",
                "UiaTrigger.TestTarget.exe"),
            scenario.Read("ConfirmedText"));

        scenario.Picker()
            .RequireByIdEventually("CondCombo", scenario.Diagnostics)
            .SelectComboItem("Always");
        scenario.Commit();

        TriggerDefinition saved = Assert.Single(scenario.SavedTriggers());
        Assert.Equal(Target, saved.Locator.Steps[^1].AutomationId);
    }

    /// <summary>
    /// 確定できない行を選んで押しても、**例外を出さずに何も起きない**こと。
    /// </summary>
    /// <remarks>
    /// <para>
    /// <c>ConfirmNode</c> の早期 return は正当な経路である
    /// (<c>node?.Element is not { }</c>)。プロセスルートの行は要素を持たない合成ノードなので、
    /// これを選んで押せばその経路に入る。
    /// </para>
    /// <para>
    /// **「捕捉させなければ選択が無い」では書けない。**
    /// 画面の外 (-30000,-30000) を指しても <c>ElementFromPoint</c> は
    /// **デスクトップを返す** (実測)。ピッカーはそれを普通に捕捉して選択するので、
    /// 「選択が無い状態」は作れない。プロセスルートの行を選ぶほうは
    /// **捕捉に依存しない**ぶん決定的である。
    /// </para>
    /// <para>
    /// **「落ちない」だけを見ると弱い。**ボタンの配線が丸ごと外れていても通る。
    /// コミットのボタンが無効のままであることまで見る — <c>ConfirmNode</c> が
    /// 早期に戻れば <c>SetCommitEnabled(true)</c> は呼ばれない。
    /// 上の 1 件目が「配線は在る」ほうを押さえている。
    /// </para>
    /// </remarks>
    [Fact]
    public void TheConfirmButton_OnARowThatCannotBeConfirmed_DoesNothing()
    {
        using var scenario = WinFormsScenario.AimedAt(Target);
        scenario.WaitForTheTargetToBeSelected();

        scenario.SelectTheProcessRootRow();
        scenario.Picker().RequireById("ConfirmNodeButton").Invoke();

        Ui.Never(
            () => scenario.Picker().ById("CommitButton")?.Current.IsEnabled == true,
            TimeSpan.FromSeconds(5),
            "確定できない行を押したのにコミットが有効になる",
            scenario.Diagnostics);
        Assert.Empty(scenario.SavedTriggers());
        scenario.RequireTheHostIsStillHealthy();
    }

    /// <summary>
    /// Windows Forms の <c>TreeView</c> は <c>ScrollPattern</c> を出さない。
    /// </summary>
    /// <remarks>
    /// <para>
    /// **これは回帰の網ではなく、事実の記録である。**製品の何も守っていない。
    /// MANUAL-CHECKS §4.3.4 の「深い階層で横に切れず読める」を自動化できるかを調べ、
    /// **できないと分かった**ことをここに残している。
    /// WPF / WinUI では同じ検査が <c>TreePresentationTests</c> で成立する。
    /// </para>
    /// <para>
    /// 「出なければ <c>Assert.Skip</c> で理由付きでスキップ」には**しない**。
    /// スキップは「まだ書いていない」と区別がつかない。
    /// 出ないことを assert しておけば、将来 Windows Forms が出すようになったときに
    /// **このテストが落ちて教えてくれる** — そのときは手動項目を回収できる。
    /// </para>
    /// </remarks>
    [Fact]
    public void TheTree_DoesNotExposeScrolling_SoThatCheckStaysManual()
    {
        using var scenario = WinFormsScenario.AimedAt(Target);
        scenario.WaitForTheTargetToBeSelected();

        Assert.Null(scenario.Tree().CanScrollHorizontally());
    }

    /// <summary>対象アプリ + Windows Forms ホスト + 開いたピッカー。</summary>
    private sealed class WinFormsScenario : IDisposable
    {
        private readonly TestTargetProcess? _target;
        private readonly PickerHostProcess _host;
        private readonly Dictionary<string, string> _resources;

        private WinFormsScenario(TestTargetProcess? target, PickerHostProcess host)
        {
            _target = target;
            _host = host;
            _resources = PickerResources.For(PickerHostProfile.WinForms, Culture);
        }

        public static WinFormsScenario AimedAt(string automationId)
        {
            TestTargetProcess target = TestTargetProcess.Start(TargetProfile.WinForms);
            try
            {
                target.Send(DesktopLayout.PlaceTargetCommand);
                foreach (string button in Buttons)
                {
                    target.Send("add-button " + button);
                }
                target.Send("ping");

                (int x, int y) = target.CenterOf(automationId);
                return Open(target, (x, y));
            }
            catch
            {
                target.Dispose();
                throw;
            }
        }

        private static WinFormsScenario Open(TestTargetProcess? target, (int X, int Y) pickPoint)
        {
            PickerHostProcess host = PickerHostProcess.Start(
                PickerHostProfile.WinForms, Culture, pickPoint);
            try
            {
                host.OpenPicker();
                return new WinFormsScenario(target, host);
            }
            catch
            {
                host.Dispose();
                throw;
            }
        }

        public string Resource(string key) => _resources[key];

        public IReadOnlyList<TriggerDefinition> SavedTriggers() => _host.SavedTriggers();

        public string Diagnostics() => _host.Diagnostics();

        /// <summary>
        /// ピッカーの**コントロールを探す起点**。
        /// </summary>
        /// <remarks>
        /// Windows Forms では所有フォームが UIA 上でメインウィンドウの下に入るので、
        /// ここが返すのは実際にはホストのウィンドウである (実測)。
        /// ツリーを持つ窓を返す、という定義は満たしているので探索の起点としては正しい。
        /// </remarks>
        public AutomationElement Picker() => _host.PickerWindow();

        public AutomationElement Tree() => _host.Tree();

        public string Read(string automationId)
            => Picker().RequireByIdEventually(automationId, Diagnostics).NameOf();

        public void WaitForTheTargetToBeSelected() => Ui.Until(
            () => Tree().SelectedRow()?.NameOf() is { } name &&
                  name.Contains($"[{Target}]", StringComparison.Ordinal)
                ? name
                : null,
            Settle,
            $"ホバー捕捉で {Target} の行が選択されること",
            Diagnostics);

        /// <summary>プロセスルートの行を選ぶ。**要素を持たない合成ノード**である。</summary>
        public void SelectTheProcessRootRow()
        {
            IReadOnlyList<AutomationElement> rows = Tree().Descendants(ControlType.TreeItem);
            Assert.True(rows.Count > 0, "ツリーに行がありません。");
            ((SelectionItemPattern)rows[0].GetCurrentPattern(SelectionItemPattern.Pattern)).Select();
            _ = Ui.Until(
                () => Tree().SelectedRow()?.NameOf() is { } name &&
                      !name.Contains($"[{Target}]", StringComparison.Ordinal) ? name : null,
                Settle,
                "プロセスルートの行が選択されること",
                Diagnostics);
        }

        public void WaitUntilCommitIsPossible() => Ui.Until(
            () => Picker().ById("CommitButton") is { } b && b.Current.IsEnabled ? "ok" : null,
            Settle,
            "確定してコミットできるようになること",
            Diagnostics);

        public void Commit()
        {
            Picker().RequireById("CommitButton").Invoke();
            _ = Ui.Until(
                () => SavedTriggers().Count > 0 ? "ok" : null,
                Settle,
                "トリガーがファイルに書かれること",
                () => $"コミットの状態: '{Picker().ById("CommitStatus")?.NameOf()}'" +
                      Environment.NewLine + Diagnostics());
        }

        /// <summary>
        /// ホストが生きていて、未処理例外を残していないこと。
        /// </summary>
        /// <remarks>
        /// 「例外を出さずに何もしない」を確かめるので、**例外が出ていないこと**を
        /// 見に行かないと意味が半分になる。WinUI ほどではないが Windows Forms でも
        /// UI スレッドの例外はログにしか出ない (docs/DESIGN.md D7)。
        /// </remarks>
        public void RequireTheHostIsStillHealthy()
        {
            string diagnostics = Diagnostics();
            Assert.DoesNotContain("ThreadException", diagnostics, StringComparison.Ordinal);
            Assert.DoesNotContain("UnhandledException", diagnostics, StringComparison.Ordinal);
            // 生きていることは、いま UIA に答えられることで見る
            Assert.NotNull(Picker().ById("ConfirmNodeButton"));
        }

        public void Dispose()
        {
            _host.Dispose();
            _target?.Dispose();
        }
    }
}
