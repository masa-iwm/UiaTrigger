// S4 の続き: 発行レイアウトで、**ホスト自身の MainWindow** のラベルが解決されること
// (docs/MANUAL-CHECKS.md §4.1)。
//
// PublishedResourceTests が見るのはピッカーの窓 (Picker 側リソース) だけで、
// ホストの App 側リソースはどのテストも値を照合していなかった — T4 はホストのボタンを
// AutomationId で押すだけで、Name はリソースと突き合わせていない
// (SplitterTests の「非空 + キー名と不一致」が唯一で、それも ListSplitter 1 キーである)。
//
// クラス名は ci.yml の aot ジョブのフィルタ
// (--filter "FullyQualifiedName~PublishedResourceTests") に**部分一致で乗るための名前**である。
// 改名するなら ci.yml も合わせて見ること。
using System.Globalization;
using System.Windows.Automation;
using Xunit;

namespace UiaTrigger.Picker.UiTests;

/// <summary>
/// 発行レイアウトでの、ホスト MainWindow のリソース解決 (S4)。
/// </summary>
/// <remarks>
/// <para>
/// assert の形は <see cref="PublishedResourceTests"/> と同じ —
/// 「空でない」でも「キー名でない」でもなく**リソースファイルの値と一致する**で書く。
/// 失敗形が 2 つ (解決失敗で空になる側 / <c>GetString</c> がキー名を返す側) あり、
/// どちらも例外を出さないためである (あちらの remarks を参照)。
/// </para>
/// <para>
/// **WinUI の App 側にだけ、3 つ目の失敗形がある** (退行確認で実測)。
/// <c>AppStrings.Get</c> の「キー名が返る」のは <c>ResourceLoader</c> 自体が立たなかったとき
/// (<c>.pri</c> 欠落) で、Loader が立っていて**キーだけが無い**ときは
/// <c>GetString</c> が例外を投げ、**ホストが起動直後に落ちる** (0xC000027B)。
/// その場合このテストはラベル照合まで進まず、ハーネスの
/// 「ホストが起動直後に終了しました」で落ちる — それも検出のうちである。
/// </para>
/// <para>
/// **App.WinForms は実行時照合していない** (「やっていない」側)。起動対象は
/// <see cref="PickerHostProfile.AllNames"/> の既存判断 (resx 経路の実行時解決は WPF が代表)
/// に従っている。ただしピッカーと違いホストのリソースはプロジェクトごとに別ファイルなので、
/// 代表の論拠はあちらより弱い — <c>publish/AppWinForms</c> 自体は CI が作っているので、
/// 広げると決めたらこのクラスに 1 プロファイル足すだけである。
/// </para>
/// </remarks>
[Trait("Layer", "PublishedResources")]
public sealed class HostPublishedResourceTests
{
    /// <summary>
    /// キー → そのラベルを載せている UIA 要素の AutomationId。
    /// </summary>
    /// <remarks>
    /// <see cref="PublishedResourceTests"/> の <c>LabelHosts</c> と同じく、
    /// **規則から導かずに表で持つ。**MainWindow では両変種とも x:Name = AutomationId で、
    /// ピッカー側にあった「WPF は隣の <c>*Label</c> に出る」例外は無い (Header を持つ
    /// コントロールが無いため)。それでも表にするのは、被覆の勘定 (下の検査) が
    /// 表を数えることで成り立つからである。
    /// </remarks>
    private static Dictionary<string, string> MainWindowLabelHosts(PickerHostProfile profile)
    {
        var hosts = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["OpenPickerButton.Content"] = "OpenPickerButton",
            ["OpenAnotherPickerButton.Content"] = "OpenAnotherPickerButton",
            ["DeleteButton.Content"] = "DeleteButton",
            ["ReloadButton.Content"] = "ReloadButton",
            ["EditListButton.Content"] = "EditListButton",
        };
        if (profile == PickerHostProfile.WinUI)
        {
            // 監視・複合・ログ一覧の区切りは WinUI ホストだけが持つ (D9 の意図された非対称。
            // 他ホストに**無いこと**は MonitorShowcaseTests が assert している)
            hosts["StartMonitorButton.Content"] = "StartMonitorButton";
            hosts["StopMonitorButton.Content"] = "StopMonitorButton";
            hosts["CombineButton.Content"] = "CombineButton";
            hosts["ExpressionLabel.Text"] = "ExpressionLabel";
            hosts["UnwatchedLabel.Text"] = "UnwatchedLabel";
            hosts["ListSplitter.AutomationProperties.Name"] = "ListSplitter";
        }
        return hosts;
    }

    /// <summary>
    /// 対応表ではなく**専用の assert** で見ているキー。
    /// </summary>
    /// <remarks>
    /// <c>WindowTitle</c> はウィンドウ自身の Name として、<c>TriggerCount</c> は起動直後の
    /// 状態欄 (<c>StatusText</c>) として見る。後者が S4 に置ける理由は決定的だからである —
    /// <c>--triggers</c> は毎回新しい一時ファイルで、無いファイルは空として読まれるので
    /// (<c>TriggerStore.Load</c>)、起動直後は必ず「0 件」の書式になる。
    /// これが MainWindow の <c>GetString</c>/<c>Format</c> 経路 (<c>AppStrings</c>) の網で、
    /// WinUI ではピッカーの <c>MrtPickerStrings</c> とは**別の ResourceMap** (App 既定の
    /// <c>Resources</c>) なので、ピッカー側のテストでは代わりに見られない。
    /// </remarks>
    private static readonly string[] CheckedSeparately = ["WindowTitle", "TriggerCount"];

    /// <summary>
    /// **トリガーファイルの種まきか操作が要る**キー。起こせそうだが測っていない。
    /// </summary>
    /// <remarks>
    /// <c>--triggers</c> のファイルを起動前に書いておけば出るはずである —
    /// 正常な JSON なら一覧の行 (<c>TriggerRow</c> / <c>NoClauses</c>)、壊れた JSON なら
    /// <c>LoadFailed</c>、種まき後に削除を押せば <c>TriggerCountSaved</c>。
    /// 要るのは <c>PickerHostProcess</c> への種まきの口 1 つで、回収可能である。
    /// 理由を書かずに落とすと、この回収の機会ごと消える
    /// (<see cref="PublishedResourceTests"/> の <c>ConfirmFailedElementGone</c> が
    /// 実際にその形で回収された)。
    /// </remarks>
    private static readonly string[] NeedsASeededTriggerFile =
    [
        "TriggerCountSaved", "TriggerRow", "NoClauses", "LoadFailed",
    ];

    /// <summary>
    /// **決定的に起こせない**キー。
    /// </summary>
    /// <remarks>
    /// <c>SaveFailed</c> は <c>catch (IOException …)</c> の中で例外の文言をメッセージに挟む。
    /// 書き込み失敗を決定的に起こす手立てが無い — ピッカー側の <c>CaptureFailed</c> 系と同形。
    /// </remarks>
    private static readonly string[] NotDeterministicallyFailable = ["SaveFailed"];

    /// <summary>
    /// ショーケース (WinUI ホストのみ) のうち、**この起動口では出せない**か、
    /// 出せるかを**まだ測っていない**キー。
    /// </summary>
    /// <remarks>
    /// <para>理由はまとめない — まとめると「いつか誰かが」の袋になる:</para>
    /// <list type="bullet">
    /// <item><description>
    /// <c>CombineDone</c> / <c>CompositeRow</c> — トリガー 2 件と選択操作が要る。
    /// 種まきの口 (上記) ができれば回収可能。
    /// </description></item>
    /// <item><description>
    /// <c>CombineFailed</c> — 選択 0 件で押せば出るはずだが、<c>{0}</c> に入る理由文は
    /// Core のリソース (<c>Compose_*</c>) なので、期待値の導出に Core 側の resx 読みが要る。
    /// 起こせそうだが測っていない。
    /// </description></item>
    /// <item><description>
    /// <c>MonitorStarted</c> / <c>MonitorStopped</c> — トリガー 0 件で監視の開始が
    /// 通るかを測っていない。通るなら対象アプリ無しで出せる。
    /// </description></item>
    /// <item><description>
    /// <c>MonitorStartFailed</c> と <c>MonitorRow*</c> 4 種 — この起動口では出せない。
    /// 発火には対象アプリが要る。経路そのものは <c>MonitorShowcaseTests</c> (開発ビルド) が
    /// 通すが、**あちらは行の種別の札しか見ておらず、値をリソースと突き合わせていない** —
    /// 弱いことは書いておく。
    /// </description></item>
    /// </list>
    /// </remarks>
    private static readonly string[] ShowcaseKeysThisLaunchPathCannotShow =
    [
        "CombineDone", "CompositeRow", "CombineFailed",
        "MonitorStarted", "MonitorStopped",
        "MonitorStartFailed",
        "MonitorRowFired", "MonitorRowResolved", "MonitorRowUnresolved", "MonitorRowError",
    ];

    /// <summary>
    /// App 側リソースのキーが**1 つ残らず**「見る」か「理由付きで見ない」に
    /// 振り分けられていること。
    /// </summary>
    /// <remarks>
    /// <see cref="PublishedResourceTests.EveryResourceKeyIsEitherCheckedOrExcludedForAStatedReason"/>
    /// と同じ被覆の要である — これが無いと、新しいキーは**どの表にも載っていないので
    /// 緑のまま**被覆から消える。2 つのカルチャの和を見る理由もあちらと同じ
    /// (キー集合の一致自体は T1 の <c>HostStringTests</c> が見ているが、
    /// それに依存していることをここに書いておかないと見えない)。
    /// </remarks>
    [Theory]
    [MemberData(nameof(PickerHostProfile.AllNames), MemberType = typeof(PickerHostProfile))]
    public void EveryHostResourceKeyIsEitherCheckedOrExcludedForAStatedReason(string profileName)
    {
        PickerHostProfile profile = PickerHostProfile.ByName(profileName);
        var expected = new HashSet<string>(StringComparer.Ordinal);
        expected.UnionWith(PickerResources.ForHost(profile, "en-US").Keys);
        expected.UnionWith(PickerResources.ForHost(profile, "ja-JP").Keys);

        var accounted = new HashSet<string>(StringComparer.Ordinal);
        accounted.UnionWith(MainWindowLabelHosts(profile).Keys);
        accounted.UnionWith(CheckedSeparately);
        accounted.UnionWith(NeedsASeededTriggerFile);
        accounted.UnionWith(NotDeterministicallyFailable);
        if (profile == PickerHostProfile.WinUI)
        {
            accounted.UnionWith(ShowcaseKeysThisLaunchPathCannotShow);
        }

        string[] unaccounted = [.. expected.Except(accounted, StringComparer.Ordinal).Order(StringComparer.Ordinal)];
        Assert.True(
            unaccounted.Length == 0,
            $"{profile.Name}: どの表にも載っていない App 側リソースキーがあります: {string.Join(", ", unaccounted)}。" +
            "対応表に足すか、理由付きの除外リストへ入れてください — " +
            "放置すると、そのキーは黙って被覆から消えます。");

        string[] stale = [.. accounted.Except(expected, StringComparer.Ordinal).Order(StringComparer.Ordinal)];
        Assert.True(
            stale.Length == 0,
            $"{profile.Name}: リソースに存在しないキーが表に残っています: {string.Join(", ", stale)}。" +
            "キーを消したなら表からも消してください (残すと、見ているつもりの行が空振りします)。");
    }

    /// <summary>
    /// 発行レイアウトのホストを起動し、MainWindow の各ラベルが
    /// **リソースファイルの値と一致する**こと。
    /// </summary>
    /// <remarks>
    /// カルチャを 2 つ回す理由は <see cref="PublishedResourceTests"/> と同じ —
    /// <c>ja-JP</c> でしか落ちない失敗形 (WinUI の <c>.pri</c> に ja の修飾子が無い /
    /// WPF の発行から <c>ja/</c> サテライトが落ちる) は en では緑のままになる。
    /// ピッカーは開かない — 見るのはホストの窓だけなので、この分は S4 より軽い。
    /// </remarks>
    [Theory]
    [InlineData("WPF", "en-US")]
    [InlineData("WPF", "ja-JP")]
    [InlineData("WinUI", "en-US")]
    [InlineData("WinUI", "ja-JP")]
    public void EveryMainWindowLabelResolvesToItsResourceValue(string profileName, string culture)
    {
        PickerHostProfile profile = PickerHostProfile.ByName(profileName);
        Dictionary<string, string> expected = PickerResources.ForHost(profile, culture);

        using var host = PickerHostProcess.StartForLabels(profile, culture);

        // WinUI はウィンドウハンドルが出た直後、中身の UIA ツリーへの反映が間に合わない
        // ことがある (実測 — タイトルは読めるのにボタンがまだ見つからない)。
        // 最初の 1 個を待つ。残りは同じ XAML ツリーなので一緒に出る
        _ = host.MainWindow.RequireByIdEventually("OpenPickerButton", host.Diagnostics);

        // ウィンドウのタイトル。ハーネスは意図的にタイトルで窓を探さない (ローカライズされるため) が、
        // 見つけた窓のタイトルを読むぶんには構わない
        Assert.Equal(expected["WindowTitle"], host.MainWindow.Current.Name);

        var wrong = new List<string>();
        foreach ((string key, string automationId) in MainWindowLabelHosts(profile))
        {
            AutomationElement? element = host.MainWindow.ById(automationId);
            if (element is null)
            {
                wrong.Add($"{key}: AutomationId '{automationId}' の要素が見つかりません");
                continue;
            }

            string actual = element.Current.Name;
            string want = expected[key];
            if (!string.Equals(actual, want, StringComparison.Ordinal))
            {
                wrong.Add($"{key} ('{automationId}'): 期待 '{want}' / 実際 '{actual}'");
            }
        }

        // GetString/Format 経路 (AppStrings)。CheckedSeparately の remarks を参照
        string statusWant = string.Format(CultureInfo.InvariantCulture, expected["TriggerCount"], 0);
        string? statusActual = host.MainWindow.ById("StatusText")?.Current.Name;
        if (!string.Equals(statusActual, statusWant, StringComparison.Ordinal))
        {
            wrong.Add($"TriggerCount ('StatusText'): 期待 '{statusWant}' / 実際 '{statusActual ?? "(要素なし)"}'");
        }

        Assert.True(
            wrong.Count == 0,
            $"{profile.Name} / {culture}: 発行レイアウトでホストのリソースが解決できていません " +
            $"({wrong.Count} 件)。空ならラベルの供給 (WinUI は x:Uid、WPF はコードビハインドの代入) が" +
            $"通っておらず、キー名そのままなら GetString が解決できていません:{Environment.NewLine}" +
            string.Join(Environment.NewLine, wrong) + Environment.NewLine +
            host.Diagnostics());
    }
}
