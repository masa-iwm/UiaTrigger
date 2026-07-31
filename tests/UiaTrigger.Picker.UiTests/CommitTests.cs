// S2: 確定 → 条件設定 → コミットの 1 往復 (docs/TESTING.md §1)。
//
// MANUAL-CHECKS §4.3.1 の 4 項目を引き取る。
//
// <b>真偽の根拠は画面の文字列ではなくトリガーファイルに置く。</b>
// 「追加しました」と出ていることと、追加されていることは別である —
// 画面だけを見るテストは、保存が丸ごと落ちても緑のままになる。
//
// 表示カルチャは en-US に固定する。固定しないとホストは OS の表示言語に従い、
// 同じ assert が機械によって別の文字列と比べられる。
using System.Globalization;
using System.Windows.Automation;
using UiaTrigger.Models;
using UiaTrigger.RealUia.Tests;
using Xunit;

namespace UiaTrigger.Picker.UiTests;

public sealed class CommitTests
{
    private static readonly TimeSpan Settle = TimeSpan.FromSeconds(20);

    private const string Culture = "en-US";

    /// <summary>対象アプリに並べるボタン。真ん中を捕捉し、あとから兄弟へ移る。</summary>
    private static readonly string[] Buttons = ["btn-one", "btn-two", "btn-three"];

    private const string Target = "btn-three";

    /// <summary>
    /// 確定するとトリガーの形が画面に出て、コミットするとファイルに入ること。
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>プロパティのコンボが項目で埋まることを見るのは WinUI の罠のためである。</b>
    /// 継ぎ目が渡す <c>IReadOnlyList&lt;T&gt;</c> を <c>ItemsSource</c> へそのまま代入すると、
    /// T が WinRT 射影を持たない .NET 型のとき <c>E_INVALIDARG</c> になる。例外は
    /// 「Value does not fall within the expected range.」としか言わず<b>投げた場所が分からない</b>
    /// (docs/DESIGN.md §12)。View 側で配列に具象化して回避している。
    /// </para>
    /// <para>
    /// ファイル側で見るのは <c>Id</c> / <c>ProcessName</c> / 経路の末端の <c>AutomationId</c> である。
    /// 「1 件入った」だけだと、<b>まったく別の要素の定義が入っても通る</b>。
    /// </para>
    /// </remarks>
    [Theory]
    [MemberData(nameof(PickerHostProfile.AllNames), MemberType = typeof(PickerHostProfile))]
    public void Confirming_ShowsTheShape_AndCommittingWritesTheTrigger(string profileName)
    {
        using var scenario = CommitScenario.Open(profileName);
        scenario.ConfirmTheSelectedRow();

        // 確定の表示。値はリソースから組み立てる (テストに書き写さない)
        Assert.Equal(
            string.Format(
                CultureInfo.InvariantCulture,
                scenario.Resource("Confirmed"),
                $"Button: {Target}",
                "UiaTrigger.TestTarget.exe"),
            scenario.Read("ConfirmedText"));

        // ShowTriggerShape が通ったこと。0 項目のままなら WinUI の ItemsSource 罠である。
        // そろうまで待つ (そろわなければ ComboItems が見えている項目を添えて落とす)
        _ = scenario.ComboItems("PropCombo", atLeast: 10);

        // プロパティ一覧はプレゼンターが組み立てる。ControlType の行だけ書式が違う
        string[] rows = scenario.PropertyRows();
        Assert.Contains(
            string.Format(CultureInfo.InvariantCulture, scenario.Resource("PropertyRow"), "AutomationId", Target),
            rows);
        Assert.Contains(rows, r => r.StartsWith("ControlType: ", StringComparison.Ordinal) &&
                                   r.Contains("Button", StringComparison.Ordinal));

        string id = scenario.KeyText();
        scenario.SelectOperator("Always");
        scenario.Commit();

        Assert.Equal(
            string.Format(CultureInfo.InvariantCulture, scenario.Resource("TriggerAdded"), id),
            scenario.Read("CommitStatus"));

        TriggerDefinition saved = Assert.Single(scenario.SavedTriggers());
        Assert.Equal(id, saved.Id);
        Assert.Equal("UiaTrigger.TestTarget.exe", saved.Window.ProcessName);
        Assert.Equal(Target, saved.Locator.Steps[^1].AutomationId);
    }

    /// <summary>
    /// 通らない下書きは<b>ファイルに増えず</b>、理由が画面に出ること。
    /// </summary>
    /// <remarks>
    /// <para>
    /// <c>Between</c> を文字列プロパティ (<c>Name</c>) に掛ける。検証は UI から切り離してあり
    /// (<c>TriggerDraftValidator</c>、T1 が網羅している)、ここで見るのは
    /// <b>コミットが検証を通ること</b>と<b>落ちたときに何も書かないこと</b>だけである。
    /// </para>
    /// <para>
    /// 退行: <c>Commit()</c> から <c>Validate</c> の分岐を外す → 不正な条件のまま 1 件書かれ、
    /// 保存件数が 0 でなくなって落ちる。
    /// </para>
    /// </remarks>
    [Theory]
    [MemberData(nameof(PickerHostProfile.AllNames), MemberType = typeof(PickerHostProfile))]
    public void AnInvalidDraft_IsNotWritten_AndSaysWhy(string profileName)
    {
        using var scenario = CommitScenario.Open(profileName);
        scenario.ConfirmTheSelectedRow();

        scenario.SelectOperator("Between");
        scenario.Commit();

        Assert.NotEmpty(scenario.Read("CommitStatus"));
        Assert.Empty(scenario.SavedTriggers());

        // 対照: 通る形にすれば同じ操作で書かれる。
        // これが無いと「コミットのボタンが壊れている」でも上の 2 行は通る
        scenario.SelectOperator("Always");
        scenario.Commit();
        Assert.Single(scenario.SavedTriggers());
    }

    /// <summary>
    /// 続けて別の要素を確定すると id もその要素のものになる。<b>自分で書き換えた後は変わらない。</b>
    /// </summary>
    /// <remarks>
    /// <para>
    /// 既定 id を「欄が空のときだけ」入れる実装だと、
    /// 続けて別の要素を確定したときに前の id が残り、コミットが
    /// <b>前のトリガーを黙って置き換える</b> (<c>TriggerStore.Save</c> は <c>Id</c> で dedupe する)。
    /// 追加したつもりが差し替えになるので、症状は「1 件しか増えない」である。
    /// </para>
    /// <para>
    /// 後半が本題である。<b>ユーザーが書いた値は尊重する</b> — 見分けは
    /// 「こちらが最後に入れた値と一致するか」で行っている。
    /// 「空のときだけ入れる」へ戻すと前半が落ち、「常に入れる」にすると後半が落ちる。
    /// <b>両方向に検出力がある。</b>
    /// </para>
    /// </remarks>
    [Theory]
    [MemberData(nameof(PickerHostProfile.AllNames), MemberType = typeof(PickerHostProfile))]
    public void ConfirmingAnotherElement_ChangesTheSuggestedId_UnlessYouEditedIt(string profileName)
    {
        using var scenario = CommitScenario.Open(profileName);
        scenario.ConfirmTheSelectedRow();

        string first = scenario.KeyText();
        Assert.Contains(Target, first, StringComparison.Ordinal);

        // 兄弟を実体化させてから、別の要素を確定する
        scenario.RevealTheSiblings();
        scenario.ConfirmTheRowFor("btn-one");
        string second = Ui.Until(
            () => scenario.KeyText() is { } k && !string.Equals(k, first, StringComparison.Ordinal) ? k : null,
            Settle,
            "別の要素を確定すると id が付け直されること",
            scenario.Diagnostics);
        Assert.Contains("btn-one", second, StringComparison.Ordinal);

        // 自分で書き換えたら、以後は尊重される
        const string Mine = "my-own-id";
        scenario.SetKeyText(Mine);
        scenario.ConfirmTheRowFor("btn-two");
        Ui.Never(
            () => !string.Equals(scenario.KeyText(), Mine, StringComparison.Ordinal),
            TimeSpan.FromSeconds(6),
            "自分で書き換えた id が上書きされる",
            scenario.Diagnostics);
    }

    /// <summary>
    /// 検索が<b>何件中の何件目か</b>を出すこと (<c>SearchPosition</c>)。
    /// </summary>
    /// <remarks>
    /// S4 が見ているのは当たりが無いときの <c>SearchNoMatch</c> だけで、
    /// 当たったときの経路を通すのはここだけである。
    /// </remarks>
    [Theory]
    [MemberData(nameof(PickerHostProfile.AllNames), MemberType = typeof(PickerHostProfile))]
    public void Search_ReportsThePositionAmongTheMatches(string profileName)
    {
        using var scenario = CommitScenario.Open(profileName);

        scenario.Search(Target);

        string want = string.Format(
            CultureInfo.InvariantCulture, scenario.Resource("SearchPosition"), 1, 1);
        _ = Ui.Until(
            () => string.Equals(scenario.Read("HintText"), want, StringComparison.Ordinal) ? "ok" : null,
            Settle,
            $"検索のヒントが '{want}' になること",
            scenario.Diagnostics);
    }

    /// <summary>
    /// 捕捉した要素が<b>消えた後</b>に確定すると、そう言って何も確定しないこと。
    /// </summary>
    /// <remarks>
    /// <para>
    /// <c>ConfirmFailedElementGone</c> の経路である。対象アプリの <c>remove</c> で
    /// 実際に起こせることを測ってある — <c>BuildDefinitionAsync</c> は
    /// <b>例外ではなく null を返す</b> (docs/DESIGN.md §3)。
    /// </para>
    /// <para>
    /// <b>ネガティブコントロールは <see cref="Confirming_ShowsTheShape_AndCommittingWritesTheTrigger"/>
    /// が兼ねている。</b>同じシナリオで <c>remove</c> を挟まなければ確定は成功し、
    /// コミットのボタンが有効になってファイルに 1 件入る。つまりこのテストが見ているのは
    /// 「確定がいつも失敗する」ことではない。
    /// </para>
    /// <para>
    /// <b>コミットのボタンが無効なままであることまで見る。</b>ここが有効になると、
    /// 直前に確定した<b>別の</b>定義がそのまま書かれうる — 失敗したのに何かが保存される形は
    /// 例外を出さないので、件数でしか捕まらない。
    /// </para>
    /// </remarks>
    [Theory]
    [MemberData(nameof(PickerHostProfile.AllNames), MemberType = typeof(PickerHostProfile))]
    public void ConfirmingAnElementThatIsGone_SaysSoAndConfirmsNothing(string profileName)
    {
        using var scenario = CommitScenario.Open(profileName);

        scenario.RemoveTheTargetFromTheApp();

        Assert.Equal(
            scenario.Resource("ConfirmFailedElementGone"),
            scenario.ConfirmTheSelectedRowAndReadTheOutcome());

        Assert.False(
            scenario.CanCommit(),
            "要素が消えていて確定できなかったのに、コミットのボタンが有効になっています。");
        Assert.Empty(scenario.SavedTriggers());
    }

    /// <summary>対象アプリ + ホスト + 捕捉済みのピッカー。</summary>
    private sealed class CommitScenario : IDisposable
    {
        private readonly TestTargetProcess _target;
        private readonly PickerHostProcess _host;
        private readonly Dictionary<string, string> _resources;

        private CommitScenario(
            TestTargetProcess target, PickerHostProcess host, Dictionary<string, string> resources)
        {
            _target = target;
            _host = host;
            _resources = resources;
        }

        public static CommitScenario Open(string profileName)
        {
            PickerHostProfile profile = PickerHostProfile.ByName(profileName);
            TestTargetProcess target = TestTargetProcess.Start(TargetProfile.WinForms);
            try
            {
                target.Send(DesktopLayout.PlaceTargetCommand);
                foreach (string button in Buttons)
                {
                    target.Send("add-button " + button);
                }
                target.Send("ping");

                (int x, int y) = target.CenterOf(Target);
                PickerHostProcess host = PickerHostProcess.Start(profile, Culture, (x, y));
                try
                {
                    host.OpenPicker();
                    var scenario = new CommitScenario(
                        target, host, PickerResources.For(profile, Culture));
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

        public string Resource(string key) => _resources[key];

        public IReadOnlyList<TriggerDefinition> SavedTriggers() => _host.SavedTriggers();

        public string Diagnostics() => _host.Diagnostics();

        private AutomationElement Picker() => _host.PickerWindow();

        public string Read(string automationId) => Picker().ById(automationId)?.NameOf() ?? string.Empty;

        public string KeyText() => Picker().RequireById("KeyBox").ValueOf() ?? string.Empty;

        public void SetKeyText(string text) => Picker().RequireById("KeyBox").SetText(text);

        private void WaitForTheTargetToBeSelected() => Ui.Until(
            () => _host.Tree().SelectedRow()?.NameOf() is { } name &&
                  name.Contains($"[{Target}]", StringComparison.Ordinal)
                ? name
                : null,
            Settle,
            $"ホバー捕捉で {Target} の行が選択されること",
            Diagnostics);

        /// <summary>
        /// いま選択されている行を確定する。
        /// </summary>
        /// <remarks>
        /// <b>「確定した」は「コミットできるようになった」で待つ。</b>
        /// 確定の表示 (<c>ConfirmedText</c>) は未確定のときも非空なので、
        /// 「非空になるまで」で待つと<b>待たずに通り抜ける</b> (探りで実際にそうなった)。
        /// </remarks>
        public void ConfirmTheSelectedRow()
        {
            AutomationElement row = _host.Tree().SelectedRow()
                ?? throw new InvalidOperationException("行が選択されていません。");
            ConfirmButtonOf(row).Invoke();
            WaitUntilCommitIsPossible();
        }

        public void ConfirmTheRowFor(string automationId)
        {
            AutomationElement row = Ui.Until(
                () => _host.Tree().Descendants(ControlType.TreeItem)
                    .FirstOrDefault(r => r.NameOf().Contains($"[{automationId}]", StringComparison.Ordinal)),
                Settle,
                $"{automationId} の行",
                Diagnostics);
            ConfirmButtonOf(row).Invoke();
            WaitUntilCommitIsPossible();
        }

        /// <summary>捕捉した要素を対象アプリから消す。掴んでいる要素はゾンビになる。</summary>
        public void RemoveTheTargetFromTheApp()
        {
            _target.Send("remove " + Target);
            _target.Send("ping");
        }

        /// <summary>
        /// いま選択されている行を確定し、<b>結果の表示が変わるまで待って</b>それを返す。
        /// </summary>
        /// <remarks>
        /// <see cref="ConfirmTheSelectedRow"/> と違って「コミットできるようになる」では待てない —
        /// 失敗する経路ではそれが永久に来ないためである。かわりに表示の変化で待つ。
        /// <b>押す前に読むこと。</b>押した後に読むと、既に変わった値を基準にしてしまい
        /// 「変わるまで待つ」が永久に成立しない (探りで実際にそうなった)。
        /// </remarks>
        public string ConfirmTheSelectedRowAndReadTheOutcome()
        {
            string before = Read("ConfirmedText");
            AutomationElement row = _host.Tree().SelectedRow()
                ?? throw new InvalidOperationException("行が選択されていません。");
            ConfirmButtonOf(row).Invoke();
            return Ui.Until(
                () => Read("ConfirmedText") is { Length: > 0 } now &&
                      !string.Equals(now, before, StringComparison.Ordinal) ? now : null,
                Settle,
                "確定の結果が表示に出ること",
                Diagnostics);
        }

        /// <summary>コミットのボタンが押せる状態か。</summary>
        public bool CanCommit() => Picker().ById("CommitButton") is { } b && b.Current.IsEnabled;

        private void WaitUntilCommitIsPossible() => Ui.Until(
            () => Picker().ById("CommitButton") is { } b && b.Current.IsEnabled ? "ok" : null,
            Settle,
            "確定してコミットできるようになること",
            Diagnostics);

        /// <summary>
        /// 行の確定ボタン。<b>その行自身のもの</b>を取る (WPF は行が入れ子)。
        /// </summary>
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
                // 子の行の中に居るボタンは、その行のものではない
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

        /// <summary>捕捉直後はチェーンだけなので、親を開き直して兄弟を実体化させる。</summary>
        public void RevealTheSiblings()
        {
            IReadOnlyList<AutomationElement> rows = _host.Tree().Descendants(ControlType.TreeItem);
            Assert.True(rows.Count >= 2, "親の行がありません。");
            ExpandCollapsePattern expander = rows[^2].Expander();
            expander.Collapse();
            expander.Expand();
            _ = Ui.Until(
                () => _host.Tree().RowNames().Any(
                    r => r.Contains("[btn-one]", StringComparison.Ordinal)) ? "ok" : null,
                Settle,
                "兄弟の行が出そろうこと",
                Diagnostics);
        }

        /// <summary>
        /// コンボを開いて項目名を読む。<b><paramref name="atLeast"/> 件そろうまで待つ。</b>
        /// </summary>
        /// <remarks>
        /// <para>
        /// 「1 件でも出たら返す」にはしない。<b>項目は一度に出そろうとは限らない</b> —
        /// 実際に落ちたときは、<c>Length &gt;= 10</c> が落ちたのに、直後にメッセージを
        /// 組み立てるための 2 回目の読みでは 10 件見えていた。つまり<b>「埋まっていない」ではなく
        /// 「読むのが早かった」</b>のであり、症状は「項目が足りない」という顔で出る。
        /// </para>
        /// <para>
        /// <c>SelectComboItem</c> が項目の出現を待つのも同じ理由である。
        /// </para>
        /// </remarks>
        public string[] ComboItems(string automationId, int atLeast)
        {
            AutomationElement combo = Picker().RequireById(automationId);
            var expander = (ExpandCollapsePattern)combo.GetCurrentPattern(ExpandCollapsePattern.Pattern);
            expander.Expand();
            try
            {
                return Ui.Until(
                    () => combo.Descendants(ControlType.ListItem) is { } items && items.Count >= atLeast
                        ? items.Select(Ui.NameOf).ToArray()
                        : null,
                    Settle,
                    $"{automationId} の項目が {atLeast} 件そろうこと",
                    () => $"見えている項目: [{string.Join(", ", combo.Descendants(ControlType.ListItem).Select(Ui.NameOf))}]" +
                          $"{Environment.NewLine}{Diagnostics()}");
            }
            finally
            {
                expander.Collapse();
            }
        }

        public string[] PropertyRows() => Ui.Until(
            () => Picker().RequireById("PropsList").Descendants(ControlType.ListItem) is { Count: > 0 } rows
                ? rows.Select(Ui.NameOf).ToArray()
                : null,
            Settle,
            "プロパティ一覧が埋まること",
            Diagnostics);

        public void SelectOperator(string name)
        {
            Picker().RequireByIdEventually("CondCombo", Diagnostics).SelectComboItem(name);
            // 演算子を変えると欄の出し分けが走る。走り終わる前にコミットすると
            // 「何を見て落ちたのか」が読めなくなる
            _ = Ui.Until(
                () => Picker().ById("CondCombo") is not null ? "ok" : null,
                Settle,
                "条件のコンボが落ち着くこと",
                Diagnostics);
        }

        public void Commit()
        {
            string before = Read("CommitStatus");
            Picker().RequireById("CommitButton").Invoke();
            _ = Ui.Until(
                () => Read("CommitStatus") is { Length: > 0 } now &&
                      !string.Equals(now, before, StringComparison.Ordinal) ? now : null,
                Settle,
                "コミットの結果が出ること",
                Diagnostics);
        }

        public void Search(string query)
        {
            Picker().RequireById("SearchBox").SetText(query);
            Picker().RequireById("SearchNextButton").Invoke();
        }

        public void Dispose()
        {
            _host.Dispose();
            _target.Dispose();
        }
    }
}
