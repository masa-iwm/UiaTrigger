// S4: 発行レイアウトで、ピッカーのラベルが実際に解決されること (docs/TESTING.md §1)。
//
// ここでしか見られないものが 2 つある:
//   - WinUI / MRT 経路。resources.pri が発行に含まれ、ResourceMap 名が合っていること。
//     T1 が見ているのは MrtPickerStrings.cs のソース正規表現 1 本だけで、
//     .pri が落ちても map 名が間違っても<b>T1 は全件緑のまま</b>である
//   - WPF / resx 経路の<b>サテライト</b> (ja/)。実行時解決そのものは T1 の
//     PickerStringTests が見ているが、発行時に ja/ が落ちないことは見ていない
//     (Directory.Build.props の SatelliteResourceLanguages)
//
// 座標も対象アプリも使わない。StartForLabels がそのための起動口である。
using System.Globalization;
using System.Windows.Automation;
using System.Xml.Linq;
using UiaTrigger.Picker;
using UiaTrigger.Tests;
using Xunit;

namespace UiaTrigger.Picker.UiTests;

/// <summary>
/// 発行レイアウトでのリソース解決 (S4)。
/// </summary>
/// <remarks>
/// <para>
/// <b>assert は「キー名と一致しない」ではなく「リソースファイルの値と一致する」で書く。</b>
/// 失敗形が 2 つあり症状が違うためである:
/// </para>
/// <list type="bullet">
/// <item><description>
/// <c>GetString</c> が解決できない (<c>.pri</c> 欠落 / map 名の誤り) → <b>キー名がそのまま返る</b>
/// </description></item>
/// <item><description>
/// <c>x:Uid</c> が XAML ロード時に解決できない → <b>ラベルが空になる</b>
/// (XAML ローダーにキーへのフォールバックが無い)
/// </description></item>
/// </list>
/// <para>
/// 「どのラベルもキー名と一致しない」は後者を捕まえず、「どのラベルも空でない」は
/// 正当に空の要素が多すぎて使えない。値と比べれば<b>空・キー名・カルチャ違いの 3 つ</b>を
/// 1 つの assert が捕まえ、期待文字列をソースに書かないので drift もしない。
/// </para>
/// </remarks>
[Trait("Layer", "PublishedResources")]
public sealed class PublishedResourceTests
{
    /// <summary>
    /// キー → そのラベルを載せている UIA 要素の AutomationId。
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>規則から導かずに表で持つ。</b>接尾辞から機械的に決めようとすると、
    /// 変種ごとの例外 (WPF の <c>AutoSelectToggle.Header</c> → <c>AutoSelectLabel</c>、
    /// WinUI のトグルが Header と OnContent を 1 つの Name に混ぜること) を
    /// どのみち書くことになり、規則と例外の両方を読まないと何を見ているのか分からなくなる。
    /// </para>
    /// <para>
    /// 対応は<b>実測して作った</b>。WinUI では
    /// <c>x:Uid</c> の対象そのものが値を Name に載せる (<c>.Header</c> も含めて) が、
    /// WPF では <c>.Header</c> だけが別の <c>*Label</c> テキストに出る。
    /// </para>
    /// </remarks>
    private static Dictionary<string, string> LabelHosts(PickerHostProfile profile) =>
        profile == PickerHostProfile.WinUI
            ? new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["AutoSelectToggle.Header"] = "AutoSelectToggle",
                ["ViewCombo.Header"] = "ViewCombo",
                ["SearchBox.Header"] = "SearchBox",
                ["KeyBox.Header"] = "KeyBox",
                ["OnCombo.Header"] = "OnCombo",
                ["PropCombo.Header"] = "PropCombo",
                ["CondCombo.Header"] = "CondCombo",
                ["MinIntervalOperand.Header"] = "MinIntervalOperand",
                ["SearchNextButton.Content"] = "SearchNextButton",
                ["CommitButton.Content"] = "CommitButton",
                ["HintText.Text"] = "HintText",
                ["ConditionHeading.Text"] = "ConditionHeading",
                ["ConfirmedText.Text"] = "ConfirmedText",
                // 区切り。<b>attached property のキーが MRT に効くかどうか</b>を
                // 実際に見ている唯一の場所である — 綴りや形式が違えば x:Uid は黙って解決せず、
                // Name が空になるだけで例外は出ない
                ["TreeSplitter.AutomationProperties.Name"] = "TreeSplitter",
                ["ConditionSplitter.AutomationProperties.Name"] = "ConditionSplitter",
            }
            : new Dictionary<string, string>(StringComparer.Ordinal)
            {
                // WPF には x:Uid の実行時解決が無く、ラベルは隣の TextBlock に代入される
                ["AutoSelectToggle.Header"] = "AutoSelectLabel",
                ["ViewCombo.Header"] = "ViewComboLabel",
                ["SearchBox.Header"] = "SearchBoxLabel",
                ["KeyBox.Header"] = "KeyBoxLabel",
                ["OnCombo.Header"] = "OnComboLabel",
                ["PropCombo.Header"] = "PropComboLabel",
                ["CondCombo.Header"] = "CondComboLabel",
                ["MinIntervalOperand.Header"] = "MinIntervalOperandLabel",
                ["SearchNextButton.Content"] = "SearchNextButton",
                ["CommitButton.Content"] = "CommitButton",
                ["HintText.Text"] = "HintText",
                ["ConditionHeading.Text"] = "ConditionHeading",
                ["ConfirmedText.Text"] = "ConfirmedText",
                // WPF はコードビハインドが AutomationProperties.SetName で載せる
                ["TreeSplitter.AutomationProperties.Name"] = "TreeSplitter",
                ["ConditionSplitter.AutomationProperties.Name"] = "ConditionSplitter",
            };

    /// <summary>
    /// UIA には構造的に出ないので、<b>永久に</b>ここでは見られないキー。
    /// </summary>
    private static readonly string[] NeverVisibleInUia =
    [
        // ツールチップは UIA の Name に出ない。HelpText でもない (実測)
        "ConfirmNodeButton.ToolTipService.ToolTip",
    ];

    /// <summary>
    /// <b>操作しないと出ない</b>キーと、それを出すための操作。
    /// </summary>
    /// <remarks>
    /// <para>
    /// 比較演算子のコンボはウィンドウを作るときに埋まっており
    /// (<c>CondCombo.ItemsSource = Enum.GetValues&lt;ComparisonOp&gt;()</c>)、
    /// <c>ConditionShapeChanged</c> は確定済みの要素を要求しない。
    /// つまり<b>捕捉も対象アプリも座標も無しで出せる</b>ので、
    /// S4 と同じ<b>発行レイアウト</b>のまま検査できる。
    /// </para>
    /// <para>
    /// キーは 6 つとも「見る」側にあり、除外リストには入っていない。
    /// </para>
    /// </remarks>
    private static readonly (string Key, string Operator)[] KeysThatNeedAnOperator =
    [
        ("TextOperand.Header", "Equals"),
        ("ToleranceOperand.Header", "Equals"),
        ("ValueOperand.Header", "GreaterThan"),
        ("LowOperand.Header", "Between"),
        ("HighOperand.Header", "Between"),
    ];

    /// <summary>
    /// 対応表ではなく<b>専用の assert</b> で見ているキー。
    /// </summary>
    /// <remarks>
    /// 変種で載り方が違うため、1 つの表に収まらない。WinUI の <c>ToggleSwitch</c> は
    /// Header と現在の内容を 1 つの Name に混ぜる ('Follow the mouse On') が、
    /// WPF の <c>CheckBox</c> は内容だけを Name に出す ('On')。
    /// <b>ここに挙げておかないと被覆の勘定から漏れる</b> — 実際、最初に書いたときは
    /// 専用 assert があるのに表のどこにも無く、被覆テストがそれを捕まえた。
    /// </remarks>
    private static readonly string[] CheckedSeparately =
    [
        "AutoSelectToggle.OnContent",
        "AutoSelectToggle.OffContent",
        // 唯一の GetString 経路。EveryStaticLabel… とは別のテストで見る (下記)
        "SearchNoMatch",
    ];

    /// <summary>
    /// <b>発行レイアウトではなく開発ビルドで</b>見ているキー (<c>CommitTests</c>)。
    /// </summary>
    /// <remarks>
    /// <para>
    /// どれも<b>要素を確定していないと出ない</b>ので、捕捉が要る =
    /// 対象アプリと座標が要る。S4 の起動口 (<see cref="PickerHostProcess.StartForLabels"/>) は
    /// まさにそれを避けるために在るので、ここへは持ち込まない。
    /// </para>
    /// <para>
    /// <b>弱いことは書いておく。</b>これらは「解決できる」ことしか言えず、
    /// 「<b>発行したもので</b>解決できる」は言えない。<c>.pri</c> が落ちる類の失敗は
    /// 発行のときにしか起きないので、そこは S4 の 13 キーが代表して見ていることに頼っている。
    /// </para>
    /// </remarks>
    private static readonly string[] CheckedByTheCommitTests =
    [
        "Confirmed", "TriggerAdded", "PropertyRow", "PropertyRowControlType", "SearchPosition",
        // 対象アプリから remove してから確定すると出ることは実測済みで、
        // CommitTests.ConfirmingAnElementThatIsGone_SaysSoAndConfirmsNothing が見ている
        "ConfirmFailedElementGone",
    ];

    /// <summary>
    /// 画面に出せないキーと、その理由。
    /// </summary>
    /// <remarks>
    /// <para>
    /// 理由が 2 通りあるので、まとめずに書く — まとめると
    /// 「いつか誰かが」の袋になり、二度と見直されない。
    /// </para>
    /// <para>
    /// <b>「起こせそうだが測っていない」ものは、理由付きでこのリストに置くこと。</b>
    /// 実際に <c>ConfirmFailedElementGone</c> がその形で置かれ、測ったら起こせた —
    /// 対象アプリから <c>remove</c> してから確定すると <c>BuildDefinitionAsync</c> が
    /// <b>例外ではなく null を返し</b>、このメッセージが出る (docs/DESIGN.md §3)。
    /// いまは「見る」側 (<c>CommitTests</c>) が引き取っている。
    /// 理由を書かずに落とすと、こうした回収の機会ごと消える。
    /// </para>
    /// <list type="bullet">
    /// <item><description>
    /// <c>OverlapPosition</c> — ←/→ の重なり切替でしか出ない。低レベルフックは
    /// <b>実キー入力でしか起こせない</b>ので、擬似入力を解禁している T5 の担当である。
    /// </description></item>
    /// <item><description>
    /// <c>SelectTriggerShape</c> — <b>UI からは到達できない。</b><c>Commit()</c> が
    /// これを出すのは <c>ReadDraft()</c> が null のときだが、コミットのボタンは確定するまで
    /// 無効で、確定すると 3 つのコンボが必ず埋まる。防御的な分岐であって、
    /// 現在の View では通らない。<b>「まだやっていない」ではなく「通せない」</b>。
    /// </description></item>
    /// <item><description>
    /// 残り 4 つ (<c>CaptureFailed</c> / <c>ViewSwitchFailed</c> / <c>OverlapFailed</c> /
    /// <c>ConfirmFailed</c>) — いずれも <c>catch (Exception ex)</c> の中で、
    /// メッセージに例外の文言を挟む。UIA の往復を<b>決定的に</b>失敗させる手立てが無い。
    /// </description></item>
    /// </list>
    /// </remarks>
    private static readonly string[] NotReachableFromTheScreen =
    [
        "OverlapPosition", "SelectTriggerShape",
        "CaptureFailed", "ViewSwitchFailed", "OverlapFailed", "ConfirmFailed",
    ];

    /// <summary>
    /// <b>別の窓</b>のキー — トリガ一覧エディタのものである (docs/DESIGN.md §4)。
    /// </summary>
    /// <remarks>
    /// <para>
    /// S4 が起こすのはピッカーの窓だけ (<see cref="PickerHostProcess.StartForLabels"/>) で、
    /// エディタはそこから開かない別のウィンドウなので、この経路では<b>構造的に出せない</b>。
    /// 供給経路 (resx / resw) は 2 つの窓で共有しているため、キーだけがここに現れる。
    /// </para>
    /// <para>
    /// <b>ここは「まだやっていない」ではなく「この起動口では出せない」である。</b>
    /// エディタのラベルを実物で見るのは、ホストから開く T4
    /// (<c>EditorShowcaseTests</c>) の担当になる。キー集合が 2 経路で揃っていること自体は
    /// T1 の <c>PickerStringTests</c> が見ている。
    /// </para>
    /// </remarks>
    private static readonly string[] TheOtherWindowsKeys = [.. EditorStringKeys.All];

    /// <summary>
    /// リソースのキーが<b>1 つ残らず</b>「見る」か「理由付きで見ない」に振り分けられていること。
    /// </summary>
    /// <remarks>
    /// <b>これが被覆の要である。</b>新しいキーを足した人が対応表に載せ忘れると、
    /// そのキーは<b>どの表にも載っていないので緑のまま</b>被覆から消える —
    /// 計画が「表に無い接尾辞が現れたら落ちるようにする」と書いたのはこの形のことである。
    /// ここで落とせば、載せるか除外するかを必ず選ぶことになる。
    ///
    /// <para>
    /// <b>2 つのカルチャの和を見る。</b>片方だけを読むと、ja にしか無いキーが
    /// この検査を素通りし、そのぶんの照合は
    /// <see cref="EveryStaticLabelResolvesToItsResourceValue"/> の
    /// <c>expected[key]</c> で <c>KeyNotFoundException</c> になる — つまり
    /// <b>診断ではなくクラッシュ</b>で出る。
    /// en と ja のキーが一致すること自体は T1 の
    /// <c>XamlLocalizationTests.EnglishAndJapaneseResourcesHaveTheSameKeys</c> が
    /// 見ているが、<b>それに依存していることをここに書いておかないと見えない</b>。
    /// </para>
    /// </remarks>
    [Theory]
    [MemberData(nameof(PickerHostProfile.AllNames), MemberType = typeof(PickerHostProfile))]
    public void EveryResourceKeyIsEitherCheckedOrExcludedForAStatedReason(string profileName)
    {
        PickerHostProfile profile = PickerHostProfile.ByName(profileName);
        var expected = new HashSet<string>(StringComparer.Ordinal);
        expected.UnionWith(ExpectedLabels(profile, "en-US").Keys);
        expected.UnionWith(ExpectedLabels(profile, "ja-JP").Keys);

        var accounted = new HashSet<string>(StringComparer.Ordinal);
        accounted.UnionWith(LabelHosts(profile).Keys);
        accounted.UnionWith(KeysThatNeedAnOperator.Select(k => k.Key));
        accounted.UnionWith(CheckedSeparately);
        accounted.UnionWith(NeverVisibleInUia);
        accounted.UnionWith(CheckedByTheCommitTests);
        accounted.UnionWith(NotReachableFromTheScreen);
        accounted.UnionWith(TheOtherWindowsKeys);
        // ウィンドウのタイトルは要素ではなくウィンドウ自身の Name として見る
        accounted.Add("WindowTitle");

        string[] unaccounted = [.. expected.Except(accounted, StringComparer.Ordinal).Order(StringComparer.Ordinal)];
        Assert.True(
            unaccounted.Length == 0,
            $"{profile.Name}: どの表にも載っていないリソースキーがあります: {string.Join(", ", unaccounted)}。" +
            "対応表に足すか、理由付きの除外リストへ入れてください — " +
            "放置すると、そのキーは黙って被覆から消えます。");

        string[] stale = [.. accounted.Except(expected, StringComparer.Ordinal).Order(StringComparer.Ordinal)];
        Assert.True(
            stale.Length == 0,
            $"{profile.Name}: リソースに存在しないキーが表に残っています: {string.Join(", ", stale)}。" +
            "キーを消したなら表からも消してください (残すと、見ているつもりの行が空振りします)。");
    }

    /// <summary>
    /// 発行レイアウトのホストを起動し、各ラベルが<b>リソースファイルの値と一致する</b>こと。
    /// </summary>
    /// <remarks>
    /// カルチャを 2 つ回すのは、<c>ja-JP</c> でしか落ちない失敗形があるからである —
    /// WinUI なら <c>.pri</c> に ja の修飾子が入っていないこと、WPF なら
    /// 発行から <c>ja/</c> サテライトが落ちることで、どちらも en では緑のままになる。
    /// </remarks>
    [Theory]
    [InlineData("WPF", "en-US")]
    [InlineData("WPF", "ja-JP")]
    [InlineData("WinUI", "en-US")]
    [InlineData("WinUI", "ja-JP")]
    public void EveryStaticLabelResolvesToItsResourceValue(string profileName, string culture)
    {
        PickerHostProfile profile = PickerHostProfile.ByName(profileName);
        Dictionary<string, string> expected = ExpectedLabels(profile, culture);

        using var host = PickerHostProcess.StartForLabels(profile, culture);
        host.OpenPicker();
        AutomationElement picker = host.PickerWindow();

        // ウィンドウのタイトル。ハーネスは意図的にタイトルで窓を探さない (ローカライズされるため) が、
        // 見つけた窓のタイトルを読むぶんには構わない
        Assert.Equal(expected["WindowTitle"], picker.Current.Name);

        var wrong = new List<string>();
        foreach ((string key, string automationId) in LabelHosts(profile))
        {
            AutomationElement? element = picker.ById(automationId);
            if (element is null)
            {
                wrong.Add($"{key}: AutomationId '{automationId}' の要素が見つかりません");
                continue;
            }

            string actual = element.Current.Name;
            string want = expected[key];

            // WinUI の ToggleSwitch は Header と現在の内容を 1 つの Name に混ぜる
            // ('Follow the mouse On')。区切り文字はテンプレートの都合なので、
            // 一致ではなく前後で見る — ここを "{Header} {OnContent}" で書くと、
            // テンプレートが変わっただけで<b>リソースと無関係な理由で</b>落ちる
            bool ok = profile == PickerHostProfile.WinUI && automationId == "AutoSelectToggle"
                ? actual.StartsWith(want, StringComparison.Ordinal)
                : string.Equals(actual, want, StringComparison.Ordinal);

            if (!ok)
            {
                wrong.Add($"{key} ('{automationId}'): 期待 '{want}' / 実際 '{actual}'");
            }
        }

        // WinUI のトグルは OnContent も同じ Name の末尾に入っている
        if (profile == PickerHostProfile.WinUI &&
            picker.ById("AutoSelectToggle") is { } toggle &&
            !toggle.Current.Name.EndsWith(expected["AutoSelectToggle.OnContent"], StringComparison.Ordinal))
        {
            wrong.Add(
                $"AutoSelectToggle.OnContent: 期待 '{expected["AutoSelectToggle.OnContent"]}' で終わること / " +
                $"実際 '{toggle.Current.Name}'");
        }
        else if (profile == PickerHostProfile.Wpf &&
                 picker.ById("AutoSelectToggle") is { } check &&
                 !string.Equals(check.Current.Name, expected["AutoSelectToggle.OnContent"], StringComparison.Ordinal))
        {
            wrong.Add(
                $"AutoSelectToggle.OnContent: 期待 '{expected["AutoSelectToggle.OnContent"]}' / " +
                $"実際 '{check.Current.Name}'");
        }

        Assert.True(
            wrong.Count == 0,
            $"{profile.Name} / {culture}: 発行レイアウトでリソースが解決できていません " +
            $"({wrong.Count} 件)。空ならラベルが x:Uid で解決できておらず、" +
            $"キー名そのままなら GetString が解決できていません:{Environment.NewLine}" +
            string.Join(Environment.NewLine, wrong) + Environment.NewLine +
            host.Diagnostics());
    }

    /// <summary>
    /// <b>操作してからでないと出ない</b>ラベルも、発行レイアウトで解決できること。
    /// </summary>
    /// <remarks>
    /// <para>
    /// トグルの OFF 表示と、比較演算子を選ぶまで <c>Collapsed</c> な 5 つのオペランド見出し。
    /// </para>
    /// <para>
    /// <b>欄の出入りそのものも見る。</b><c>Always</c> を選ぶと 5 つとも消えること —
    /// これが無いと「全部出しっぱなし」の実装でもラベルの照合は通る。
    /// 「条件の演算子を変えると値の欄の出入りが追随する」ことの一次の網はこれである。
    /// </para>
    /// <para>
    /// 見出しの載り方は静的ラベルと同じ規則に従う (WinUI は <c>x:Uid</c> の対象そのもの、
    /// WPF は隣の <c>*Label</c>)。実測でそう確かめた。
    /// </para>
    /// </remarks>
    [Theory]
    [InlineData("WPF", "en-US")]
    [InlineData("WPF", "ja-JP")]
    [InlineData("WinUI", "en-US")]
    [InlineData("WinUI", "ja-JP")]
    public void TheLabelsThatOnlyAppearAfterInteractionAlsoResolve(string profileName, string culture)
    {
        PickerHostProfile profile = PickerHostProfile.ByName(profileName);
        Dictionary<string, string> expected = ExpectedLabels(profile, culture);

        using var host = PickerHostProcess.StartForLabels(profile, culture);
        host.OpenPicker();
        AutomationElement picker = host.PickerWindow();

        var wrong = new List<string>();

        // (1) トグルを OFF にすると OffContent が出る
        AutomationElement toggle = picker.RequireById("AutoSelectToggle");
        ((TogglePattern)toggle.GetCurrentPattern(TogglePattern.Pattern)).Toggle();
        string offWant = expected["AutoSelectToggle.OffContent"];
        _ = Ui.Until(
            () => picker.ById("AutoSelectToggle")?.Current.Name is { } name &&
                  name.EndsWith(offWant, StringComparison.Ordinal) ? "ok" : null,
            TimeSpan.FromSeconds(10),
            $"{profile.Name} / {culture}: トグルの OFF 表示が '{offWant}' で終わること",
            () => $"実際 '{picker.ById("AutoSelectToggle")?.Current.Name}'。" + Environment.NewLine +
                  host.Diagnostics());

        // (2) 比較演算子を選ぶと、その演算子が使う欄だけが出る
        foreach ((string key, string op) in KeysThatNeedAnOperator)
        {
            picker.RequireByIdEventually("CondCombo", host.Diagnostics).SelectComboItem(op);
            string automationId = OperandHost(profile, key);
            AutomationElement? element = Ui.Until(
                () => picker.ById(automationId),
                TimeSpan.FromSeconds(10),
                $"{profile.Name} / {culture}: 演算子 {op} で '{automationId}' が出ること",
                host.Diagnostics);

            string actual = element.Current.Name;
            if (!string.Equals(actual, expected[key], StringComparison.Ordinal))
            {
                wrong.Add($"{key} ('{automationId}', {op}): 期待 '{expected[key]}' / 実際 '{actual}'");
            }
        }

        // (3) 対照: 条件を使わない演算子にすると、5 つとも消える
        picker.RequireByIdEventually("CondCombo", host.Diagnostics).SelectComboItem("Always");
        foreach ((string key, string _) in KeysThatNeedAnOperator)
        {
            string automationId = OperandHost(profile, key);
            Ui.Never(
                () => picker.ById(automationId) is not null,
                TimeSpan.FromSeconds(3),
                $"演算子 Always で '{automationId}' が残る",
                host.Diagnostics);
        }

        Assert.True(
            wrong.Count == 0,
            $"{profile.Name} / {culture}: 操作後に出るラベルが解決できていません ({wrong.Count} 件):" +
            $"{Environment.NewLine}{string.Join(Environment.NewLine, wrong)}{Environment.NewLine}" +
            host.Diagnostics());
    }

    /// <summary>
    /// オペランドの見出しを載せている要素の AutomationId。
    /// </summary>
    /// <remarks>
    /// 静的ラベルの <see cref="LabelHosts"/> と同じ規則である — WinUI は <c>x:Uid</c> の
    /// 対象そのものが <c>Header</c> を <c>Name</c> に出し、WPF は隣の <c>*Label</c> に出る。
    /// </remarks>
    private static string OperandHost(PickerHostProfile profile, string key)
    {
        string control = key[..key.IndexOf('.', StringComparison.Ordinal)];
        return profile == PickerHostProfile.WinUI ? control : control + "Label";
    }

    /// <summary>
    /// <b>コードから引く経路</b> (<c>IPickerStrings.GetString</c>) も発行レイアウトで解決できること。
    /// </summary>
    /// <remarks>
    /// <para>
    /// 上のラベルのテストが見ているのはほとんどが <c>x:Uid</c> 経路 =
    /// <b>XAML ローダー自身の</b> MRT 参照であって、<c>MrtPickerStrings</c> を通らない。
    /// 2 つは別の解決経路である。
    /// </para>
    /// <para>
    /// <b>ただし「上は空振りする」と書くのは誤りである</b> (退行確認で実測済み)。
    /// <c>WindowTitle</c> だけは <c>Window</c> が <c>FrameworkElement</c> でなく
    /// <c>x:Uid</c> が効かないため<b>コードから引いている</b>ので、
    /// <c>ResourceMap</c> をリネームすると上のテストも落ちる (タイトルがキー名になる)。
    /// それでもこのテストを別に置くのは、上の被覆が<b>たまたま 1 キーに乗っている</b>ためである —
    /// <c>WindowTitle</c> の実装が変われば、上は GetString 経路を 1 つも見なくなる。
    /// </para>
    /// <para>
    /// 駆動は<b>空のツリーに対する検索</b>で行う。捕捉も対象アプリも座標も要らず、
    /// プレゼンターが <c>SearchNoMatch</c> をヒントへ出す経路だけが動く。
    /// </para>
    /// </remarks>
    [Theory]
    [InlineData("WPF", "en-US")]
    [InlineData("WPF", "ja-JP")]
    [InlineData("WinUI", "en-US")]
    [InlineData("WinUI", "ja-JP")]
    public void TheStringsTheCodeLooksUpAlsoResolve(string profileName, string culture)
    {
        const string Query = "zzz-no-such-element";

        PickerHostProfile profile = PickerHostProfile.ByName(profileName);
        Dictionary<string, string> expected = ExpectedLabels(profile, culture);

        using var host = PickerHostProcess.StartForLabels(profile, culture);
        host.OpenPicker();
        AutomationElement picker = host.PickerWindow();

        picker.RequireById("SearchBox").SetText(Query);
        picker.RequireById("SearchNextButton").Invoke();

        string want = string.Format(CultureInfo.InvariantCulture, expected["SearchNoMatch"], Query);
        _ = Ui.Until(
            () => picker.ById("HintText") is { } hint
                  && string.Equals(hint.Current.Name, want, StringComparison.Ordinal)
                ? "ok"
                : null,
            TimeSpan.FromSeconds(10),
            $"{profile.Name} / {culture}: 検索のヒントがリソースの値になること",
            () =>
                $"期待 '{want}' / 実際 '{picker.ById("HintText")?.Current.Name}'。" +
                "キー名がそのまま出ているなら GetString が解決できていません " +
                "(.pri 欠落 / ResourceMap 名の誤り)。" + Environment.NewLine + host.Diagnostics());
    }

    /// <summary>
    /// そのカルチャで期待されるラベル。<b>リポジトリのリソースファイルから読む</b>ので、
    /// 期待文字列がテストにハードコードされず drift しない。
    /// </summary>
    /// <remarks>
    /// 供給元が変種で違う: WinUI は自分の <c>.resw</c> (MRT)、WPF / WinForms は
    /// <c>Picker.Core</c> の <c>.resx</c> (<c>ResxPickerStrings</c>)。
    /// <b>キーの集合は同じ</b> (<c>PickerStringKeys</c>) だが、値の出所と解決経路が違う —
    /// S4 が見ているのはまさにそこである。
    /// </remarks>
    private static Dictionary<string, string> ExpectedLabels(PickerHostProfile profile, string culture)
        => PickerResources.For(profile, culture);

}
