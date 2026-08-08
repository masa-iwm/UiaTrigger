// S2: 確定 → 条件設定 → コミットの 1 往復 (docs/TESTING.md §1)。
//
// MANUAL-CHECKS §4.3.1 の 4 項目を引き取る。
//
// **真偽の根拠は画面の文字列ではなくトリガーファイルに置く。**
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
    /// **プロパティのコンボが項目で埋まることを見るのは WinUI の罠のためである。**
    /// 継ぎ目が渡す <c>IReadOnlyList&lt;T&gt;</c> を <c>ItemsSource</c> へそのまま代入すると、
    /// T が WinRT 射影を持たない .NET 型のとき <c>E_INVALIDARG</c> になる。例外は
    /// 「Value does not fall within the expected range.」としか言わず**投げた場所が分からない**
    /// (docs/DESIGN.md §12)。View 側で配列に具象化して回避している。
    /// </para>
    /// <para>
    /// ファイル側で見るのは <c>Id</c> / <c>ProcessName</c> / 経路の末端の <c>AutomationId</c> である。
    /// 「1 件入った」だけだと、**まったく別の要素の定義が入っても通る**。
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

        // プロパティ一覧はプレゼンターが組み立てる。ControlType の行だけ書式が違う。
        // ここの主張は「往復の中で行が出る」まで — 表示名も含めた厳密な対の検査は
        // TheTreeAndThePropertyList_NameTheControlTypeTheWayTheTargetDoes が持つ
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
    /// ツリーの行とプロパティ一覧が、**対象アプリの言い方**でコントロール型を出すこと
    /// (docs/MANUAL-CHECKS.md §4.2)。
    /// </summary>
    /// <remarks>
    /// <para>
    /// 期待値は pick 点の要素からテスト側の UIA で**独立に**読む
    /// (<see cref="CommitScenario.ProbeTheTarget"/> — 別実装の相互検算)。
    /// 「対象アプリの言語になっているか」そのものは自動では言えない — 対象アプリの言語を
    /// 切り替える口が無く、実言語の確認は MANUAL-CHECKS §4.2 に残る。ここで固定するのは
    /// **対象アプリのプロバイダーが言ったとおりの表示名が画面に出ていて、
    /// 条件に書く安定名と併記されている**ことである。
    /// </para>
    /// <para>
    /// 前提検証を 2 つ置く: 独立読みが本当に対象のボタンを読んでいること /
    /// 表示名が安定名 "Button" と序数で一致しないこと (en でも "button" と小文字なので
    /// 成立する — T3 の <c>ControlTypeNameScenarioTests</c> と同じ言語非依存の根拠)。
    /// どちらかが崩れた環境では以降の assert に検出力が無いので、
    /// 時間切れではなく理由付きで落とす。
    /// </para>
    /// <para>
    /// 退行の向き (どちらも実測済み): プレゼンターの行の書式で表示名と安定名を
    /// 入れ替える → 厳密一致だけが落ちる (往復テストの <c>Contains("Button")</c> は
    /// 緑のまま)。継ぎ目の中継 (<c>PickerServices.DisplayLabel</c>) が <c>Label</c> を
    /// 返す → 行の先頭が安定名になり、序数比較で落ちる。
    /// なお「<c>CreateNode</c> を <c>Label</c> で作る」形の退行は**書けない** —
    /// <c>IPickerElement</c> は表示用に <c>DisplayLabel</c> しか出しておらず CS1061 になる
    /// (実測)。型が既に半分を守っており、このテストが見るのは残り半分
    /// (中継と <c>UiaElement</c> 自身の組み立て) である。
    /// </para>
    /// </remarks>
    [Theory]
    [MemberData(nameof(PickerHostProfile.AllNames), MemberType = typeof(PickerHostProfile))]
    public void TheTreeAndThePropertyList_NameTheControlTypeTheWayTheTargetDoes(string profileName)
    {
        using var scenario = CommitScenario.Open(profileName);

        (string automationId, string localized, int controlTypeId) = scenario.ProbeTheTarget();

        // 前提検証 (remarks を参照)
        Assert.True(
            string.Equals(automationId, Target, StringComparison.Ordinal),
            $"pick 点の独立読みが対象のボタンではなく AutomationId '{automationId}' を返しました。" +
            "このままでは期待値が別の要素のものになります。" + scenario.Diagnostics());
        Assert.False(
            string.Equals(localized, "Button", StringComparison.Ordinal),
            "対象の LocalizedControlType が安定名と同じ文字列で、表示名と安定名を区別する " +
            "assert がこの環境では成立しません (実測では en でも 'button' と小文字で別物)。");

        // ツリーの行は表示名で始まる —
        // DisplayLabel = "{LocalizedControlType} \"{Name}\" [{AutomationId}]" (UiaElement)
        string row = scenario.SelectedRowName();
        Assert.StartsWith(localized + " \"", row, StringComparison.Ordinal);

        scenario.ConfirmTheSelectedRow();

        // ControlType の行は表示名・安定名・数値 id を併記する。書式ごと厳密に見る
        string want = string.Format(
            CultureInfo.InvariantCulture,
            scenario.Resource("PropertyRowControlType"),
            localized,
            "Button",
            controlTypeId);
        Assert.Contains(want, scenario.PropertyRows());
    }

    /// <summary>
    /// 通らない下書きは**ファイルに増えず**、理由が画面に出ること。
    /// </summary>
    /// <remarks>
    /// <para>
    /// <c>Between</c> を文字列プロパティ (<c>Name</c>) に掛ける。検証は UI から切り離してあり
    /// (<c>TriggerDraftValidator</c>、T1 が網羅している)、ここで見るのは
    /// **コミットが検証を通ること**と**落ちたときに何も書かないこと**だけである。
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
    /// 続けて別の要素を確定すると id もその要素のものになる。**自分で書き換えた後は変わらない。**
    /// </summary>
    /// <remarks>
    /// <para>
    /// 既定 id を「欄が空のときだけ」入れる実装だと、
    /// 続けて別の要素を確定したときに前の id が残り、コミットが
    /// **前のトリガーを黙って置き換える** (<c>TriggerStore.Save</c> は <c>Id</c> で dedupe する)。
    /// 追加したつもりが差し替えになるので、症状は「1 件しか増えない」である。
    /// </para>
    /// <para>
    /// 後半が本題である。**ユーザーが書いた値は尊重する** — 見分けは
    /// 「こちらが最後に入れた値と一致するか」で行っている。
    /// 「空のときだけ入れる」へ戻すと前半が落ち、「常に入れる」にすると後半が落ちる。
    /// **両方向に検出力がある。**
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
    /// 検索が**何件中の何件目か**を出すこと (<c>SearchPosition</c>)。
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
    /// 捕捉した要素が**消えた後**に確定すると、そう言って何も確定しないこと。
    /// </summary>
    /// <remarks>
    /// <para>
    /// <c>ConfirmFailedElementGone</c> の経路である。対象アプリの <c>remove</c> で
    /// 実際に起こせることを測ってある — <c>BuildDefinitionAsync</c> は
    /// **例外ではなく null を返す** (docs/DESIGN.md §3)。
    /// </para>
    /// <para>
    /// **ネガティブコントロールは <see cref="Confirming_ShowsTheShape_AndCommittingWritesTheTrigger"/>
    /// が兼ねている。**同じシナリオで <c>remove</c> を挟まなければ確定は成功し、
    /// コミットのボタンが有効になってファイルに 1 件入る。つまりこのテストが見ているのは
    /// 「確定がいつも失敗する」ことではない。
    /// </para>
    /// <para>
    /// **コミットのボタンが無効なままであることまで見る。**ここが有効になると、
    /// 直前に確定した**別の**定義がそのまま書かれうる — 失敗したのに何かが保存される形は
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
        private readonly (int X, int Y) _pickPoint;

        private CommitScenario(
            TestTargetProcess target,
            PickerHostProcess host,
            Dictionary<string, string> resources,
            (int X, int Y) pickPoint)
        {
            _target = target;
            _host = host;
            _resources = resources;
            _pickPoint = pickPoint;
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
                        target, host, PickerResources.For(profile, Culture), (x, y));
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

        public string SelectedRowName() => _host.Tree().SelectedRow()?.NameOf() ?? string.Empty;

        /// <summary>
        /// pick 点の要素をテスト側の UIA で**独立に**読む。
        /// </summary>
        /// <remarks>
        /// 製品は手書き interop、こちらは <c>System.Windows.Automation</c> —
        /// 別実装の相互検算である。枠のオーバーレイは pick 点の上に居るが、
        /// <c>WS_EX_TRANSPARENT</c> なのでヒットテストに入らない (docs/DESIGN.md §10)。
        /// 別の要素を掴んだ場合は呼び出し側の前提検証が名指しで落とす。
        /// </remarks>
        public (string AutomationId, string LocalizedControlType, int ControlTypeId) ProbeTheTarget()
        {
            AutomationElement element = AutomationElement.FromPoint(
                new System.Windows.Point(_pickPoint.X, _pickPoint.Y));
            return (
                element.Current.AutomationId,
                element.Current.LocalizedControlType,
                element.Current.ControlType.Id);
        }

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
        /// **「確定した」は「コミットできるようになった」で待つ。**
        /// 確定の表示 (<c>ConfirmedText</c>) は未確定のときも非空なので、
        /// 「非空になるまで」で待つと**待たずに通り抜ける** (探りで実際にそうなった)。
        /// </remarks>
        public void ConfirmTheSelectedRow()
        {
            AutomationElement row = _host.Tree().SelectedRow()
                ?? throw new InvalidOperationException("行が選択されていません。");
            row.ConfirmButtonOf().Invoke();
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
            row.ConfirmButtonOf().Invoke();
            WaitUntilCommitIsPossible();
        }

        /// <summary>捕捉した要素を対象アプリから消す。掴んでいる要素はゾンビになる。</summary>
        public void RemoveTheTargetFromTheApp()
        {
            _target.Send("remove " + Target);
            _target.Send("ping");
        }

        /// <summary>
        /// いま選択されている行を確定し、**結果の表示が変わるまで待って**それを返す。
        /// </summary>
        /// <remarks>
        /// <see cref="ConfirmTheSelectedRow"/> と違って「コミットできるようになる」では待てない —
        /// 失敗する経路ではそれが永久に来ないためである。かわりに表示の変化で待つ。
        /// **押す前に読むこと。**押した後に読むと、既に変わった値を基準にしてしまい
        /// 「変わるまで待つ」が永久に成立しない (探りで実際にそうなった)。
        /// </remarks>
        public string ConfirmTheSelectedRowAndReadTheOutcome()
        {
            string before = Read("ConfirmedText");
            AutomationElement row = _host.Tree().SelectedRow()
                ?? throw new InvalidOperationException("行が選択されていません。");
            row.ConfirmButtonOf().Invoke();
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
        /// 行の確定ボタン。**その行自身のもの**を取る (WPF は行が入れ子)。
        /// </summary>

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
        /// コンボを開いて項目名を読む。**<paramref name="atLeast"/> 件そろうまで待つ。**
        /// </summary>
        /// <remarks>
        /// <para>
        /// 「1 件でも出たら返す」にはしない。**項目は一度に出そろうとは限らない** —
        /// 実際に落ちたときは、<c>Length &gt;= 10</c> が落ちたのに、直後にメッセージを
        /// 組み立てるための 2 回目の読みでは 10 件見えていた。つまり**「埋まっていない」ではなく
        /// 「読むのが早かった」**のであり、症状は「項目が足りない」という顔で出る。
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
