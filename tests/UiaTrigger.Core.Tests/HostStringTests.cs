using System.Xml.Linq;
using Xunit;

namespace UiaTrigger.Tests;

/// <summary>
/// 3 つのサンプルホストが同じ文字列を持っていることの回帰
/// (docs/DESIGN.md L3 / docs/LOCALIZATION.md §4)。
///
/// <para>
/// ピッカーのほうは値の出所を 1 つにできた (WPF と WinForms が Picker.Core の <c>.resx</c> を
/// 共有する) が、ホストは 3 つとも実行可能プロジェクトなので、リソースをそれぞれが持つしかない。
/// 共有できないぶん、<b>ずれていないことをテストで縛る</b>。
/// </para>
/// <para>
/// 「同じサンプルの同じ画面なのに、版によってボタンの文言が違う」は放っておくと必ず起きる。
/// どのテストも落ちないまま起き、しかもユーザーからは 1 つの製品に見える。
/// </para>
/// </summary>
public sealed class HostStringTests
{
    private const string WinUi = "src/UiaTrigger.App.WinUI";
    private const string Wpf = "src/UiaTrigger.App.Wpf";
    private const string WinForms = "src/UiaTrigger.App.WinForms";

    /// <summary>
    /// WinUI ホストだけが持つキー (D9 のショーケース。docs/DESIGN.md §12)。
    ///
    /// <para>
    /// 「<c>App.WinUI</c> だけがピッカー → 監視の E2E を兼ねる」は README の
    /// <b>"Two asymmetries are deliberate"</b> が明記している決定であり、
    /// その README は NuGet パッケージに同梱されて nuget.org で読まれる。
    /// キー集合の一致を機械的に守るために、公開済みの非対称のほうを曲げることはしない。
    /// </para>
    /// <para>
    /// 差し引くのは <b><c>missing</c> 側だけ</b>である。<c>extra</c> 側 (WinUI に無いのに
    /// WPF / WinForms に在る) は素通しにしてあり、監視用のキーが片方へ紛れ込めば落ちる。
    /// </para>
    /// </summary>
    private static readonly string[] ShowcaseOnlyKeys =
    [
        "StartMonitorButton.Content",
        "StopMonitorButton.Content",
        "MonitorStarted",
        "MonitorStopped",
        "MonitorStartFailed",
        "MonitorRowFired",
        "MonitorRowResolved",
        "MonitorRowUnresolved",
        "MonitorRowError",
        // 複合条件のショーケース (docs/DESIGN.md §4)。監視と同じく WinUI ホストだけが持つ。
        // まとめられない理由 (NeedsTwo / UnknownName) はここに無い —
        // TriggerComposer と一緒に Core (Compose_*) に在る (docs/DESIGN.md §4)
        "CombineButton.Content",
        "ExpressionLabel.Text",
        "UnwatchedLabel.Text",
        "CompositeRow",
        "CombineFailed",
        "CombineDone",
        // 2 つの一覧の境界にある GridSplitter の読み上げ名。
        // <b>ログ一覧が WinUI ホストにしか無いので、割る境界そのものが他の 2 つには無い</b> —
        // 上の 2 群と同じく D9 の非対称から出ている
        "ListSplitter.AutomationProperties.Name",
    ];

    /// <summary>言語 1 つぶんの、3 ホストのリソースファイル。</summary>
    public static TheoryData<string, string, string, string> HostResources =>
    [
        ("en-US",
            $"{WinUi}/Strings/en-us/Resources.resw",
            $"{Wpf}/Resources/Strings.resx",
            $"{WinForms}/Resources/Strings.resx"),
        ("ja",
            $"{WinUi}/Strings/ja-jp/Resources.resw",
            $"{Wpf}/Resources/Strings.ja.resx",
            $"{WinForms}/Resources/Strings.ja.resx"),
    ];

    private static Dictionary<string, string> Read(string relativePath)
    {
        string path = RepoPaths.Combine(relativePath.Split('/'));
        Assert.True(File.Exists(path), $"リソースファイルがありません: {path}");
        return XDocument.Load(path).Root!
            .Elements("data")
            .ToDictionary(e => (string)e.Attribute("name")!, e => (string)e.Element("value")!, StringComparer.Ordinal);
    }

    /// <summary>
    /// 3 ホストのキー集合が一致すること (<see cref="ShowcaseOnlyKeys"/> を除く)。
    /// </summary>
    [Theory]
    [MemberData(nameof(HostResources))]
    public void EveryHostCarriesTheSameKeys(string language, string winui, string wpf, string winforms)
    {
        Dictionary<string, string> reference = Read(winui);
        Assert.NotEmpty(reference);

        foreach ((string label, string path) in new[] { ("WPF", wpf), ("WinForms", winforms) })
        {
            Dictionary<string, string> other = Read(path);
            string[] missing = [.. reference.Keys
                .Except(ShowcaseOnlyKeys, StringComparer.Ordinal)
                .Except(other.Keys, StringComparer.Ordinal)
                .Order(StringComparer.Ordinal)];
            string[] extra = [.. other.Keys.Except(reference.Keys, StringComparer.Ordinal).Order(StringComparer.Ordinal)];

            Assert.True(missing.Length == 0, $"{language}/{label}: WinUI にあって無いキー: {string.Join(", ", missing)}");
            Assert.True(extra.Length == 0, $"{language}/{label}: WinUI に無いキー: {string.Join(", ", extra)}");
        }
    }

    /// <summary>
    /// 同じキーの<b>文言</b>も 3 ホストで一致すること。
    ///
    /// キー集合だけを見ていると、片方の文言を直したときに他方が古いまま残る。
    /// </summary>
    [Theory]
    [MemberData(nameof(HostResources))]
    public void EveryHostCarriesTheSameWording(string language, string winui, string wpf, string winforms)
    {
        Dictionary<string, string> reference = Read(winui);

        foreach ((string label, string path) in new[] { ("WPF", wpf), ("WinForms", winforms) })
        {
            Dictionary<string, string> other = Read(path);
            string[] different = [.. reference
                .Where(kv => other.TryGetValue(kv.Key, out string? value) && !string.Equals(kv.Value, value, StringComparison.Ordinal))
                .Select(kv => kv.Key)
                .Order(StringComparer.Ordinal)];

            Assert.True(
                different.Length == 0,
                $"{language}/{label}: WinUI と文言が食い違うキー: {string.Join(", ", different)}");
        }
    }

    /// <summary>
    /// 「ピッカーをもう 1 つ開く」がどのホストにも在ること。
    ///
    /// <para>
    /// A18 (オーバーレイの static singleton 廃止) を実機で確かめる<b>唯一の手段</b>である。
    /// ホストが 2 つ目を開けないと、docs/MANUAL-CHECKS.md §6 は実施不能なまま
    /// 「未実施」と「緑」の区別がつかなくなる。
    /// </para>
    /// <para>
    /// ボタンを消しても<b>何も落ちない</b>ので、消えたことに気づける仕掛けがここに要る。
    /// </para>
    /// </summary>
    [Theory]
    [InlineData($"{WinUi}/Strings/en-us/Resources.resw")]
    [InlineData($"{Wpf}/Resources/Strings.resx")]
    [InlineData($"{WinForms}/Resources/Strings.resx")]
    public void EveryHostOffersToOpenASecondPicker(string resources)
    {
        Assert.Contains("OpenAnotherPickerButton.Content", Read(resources).Keys);
    }

    /// <summary>
    /// <see cref="ShowcaseOnlyKeys"/> に挙げたキーが WinUI ホストに<b>実在する</b>こと。
    ///
    /// <para>
    /// 例外表は「在る」の主張なので、これが無いと<b>綴りを間違えても、キーを消しても緑のまま</b>になる
    /// (差し引く対象が消えるだけで、誰も文句を言わない)。
    /// 「『無いこと』の主張は探し損ねても緑になる」と同じ形である。
    /// </para>
    /// <para>
    /// 両言語を見るので、片方だけ足した場合もここで落ちる。
    /// </para>
    /// </summary>
    [Theory]
    [InlineData($"{WinUi}/Strings/en-us/Resources.resw")]
    [InlineData($"{WinUi}/Strings/ja-jp/Resources.resw")]
    public void TheShowcaseOnlyKeysExistInTheWinUiHost(string resources)
    {
        Dictionary<string, string> actual = Read(resources);
        string[] absent = [.. ShowcaseOnlyKeys
            .Except(actual.Keys, StringComparer.Ordinal)
            .Order(StringComparer.Ordinal)];

        Assert.True(
            absent.Length == 0,
            $"{resources}: 例外表に在るのにリソースに無いキー: {string.Join(", ", absent)}");
    }
}
