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
/// 共有できないぶん、**ずれていないことをテストで縛る**。
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
    /// **"Two asymmetries are deliberate"** が明記している決定であり、
    /// その README は NuGet パッケージに同梱されて nuget.org で読まれる。
    /// キー集合の一致を機械的に守るために、公開済みの非対称のほうを曲げることはしない。
    /// </para>
    /// <para>
    /// 差し引くのは **<c>missing</c> 側だけ**である。<c>extra</c> 側 (WinUI に無いのに
    /// WPF / WinForms に在る) は素通しにしてある。
    /// </para>
    /// <para>
    /// **ただし <c>extra</c> はここに載ったキーの紛れ込みを検出できない。**
    /// これらは WinUI に**在る**ので、WPF 側へ複製されても
    /// <c>other.Keys.Except(reference.Keys)</c> から消える。紛れ込みは
    /// <see cref="NoOtherHostCarriesTheShowcaseOnlyKeys"/> が名指しで見る。
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
        // **ログ一覧が WinUI ホストにしか無いので、割る境界そのものが他の 2 つには無い** —
        // 上の 2 群と同じく D9 の非対称から出ている
        "ListSplitter.AutomationProperties.Name",
    ];

    /// <summary>
    /// T4 のショーケースが「どの行か」を見分けるのに使う札と、その出所のキー。
    /// </summary>
    /// <remarks>
    /// **T4 側はこの文字列を定数として書き写している。**行の書式はリソース側にあるので、
    /// 文言を直すと札が消え、T4 は「行が出ない」で期限まで待って落ちる — 原因が
    /// リソースの文言だとは読めない失敗になる。ここで名指しで結び付けておくと、
    /// リソースを触った時点で**この T1 が**落ちる。
    /// </remarks>
    public static TheoryData<string, string> ShowcaseMarkers =>
    [
        ("MonitorRowFired", "FIRED"),
        ("MonitorRowResolved", "resolved"),
        ("MonitorRowUnresolved", "UNRESOLVED"),
        ("MonitorRowError", "ERROR"),
    ];

    /// <summary>
    /// ショーケースの札が、実際に WinUI ホストのリソース文言に含まれていること。
    /// </summary>
    /// <remarks>
    /// 見るのは en-US 側だけである。T4 のショーケースはホストを <c>--culture en-US</c> で
    /// 起こすので、札が一致すべき相手はプライマリの文言だけになる。
    /// </remarks>
    [Theory]
    [MemberData(nameof(ShowcaseMarkers))]
    public void EveryShowcaseMarkerComesFromTheHostResource(string key, string marker)
    {
        Dictionary<string, string> english = Read($"{WinUi}/Strings/en-us/Resources.resw");

        Assert.True(english.TryGetValue(key, out string? value), $"リソースにキーがありません: {key}");
        Assert.True(
            value!.Contains(marker, StringComparison.Ordinal),
            $"{key} の文言 '{value}' に札 '{marker}' が含まれていません。" +
            "T4 のショーケース (MonitorShowcaseTests / CompositeShowcaseTests) は" +
            "この札で行を見分けます — 文言を変えるなら両方を一緒に直してください。");
    }

    /// <summary>
    /// 「解決」の札が「未解決」の札に**含まれない**こと。
    /// </summary>
    /// <remarks>
    /// T4 は序数の部分一致で行を選ぶので、片方がもう片方の部分文字列だと
    /// **未解決の行を解決と読んで先へ進む**。ホストが平常を小文字・注意を大文字で
    /// 出し分けているのはそのためであり、その出し分けをここで固定する。
    /// </remarks>
    [Fact]
    public void TheResolvedMarkerIsNotASubstringOfTheUnresolvedOne()
    {
        Dictionary<string, string> english = Read($"{WinUi}/Strings/en-us/Resources.resw");

        Assert.DoesNotContain("resolved", english["MonitorRowUnresolved"], StringComparison.Ordinal);
    }

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
    /// ショーケース専用のキーが、WinUI 以外のホストへ紛れ込んでいないこと。
    /// </summary>
    /// <remarks>
    /// <see cref="EveryHostCarriesTheSameKeys"/> の <c>extra</c> ではこれを検出できない —
    /// 除外対象のキーは WinUI 側に**在る**ので、WPF / WinForms へ複製されても
    /// 差集合から消えてしまう。除外表を持つ以上、除外したものが片側だけの決定として
    /// 保たれていることは別に見る必要がある (docs/TESTING.md §2)。
    /// </remarks>
    [Theory]
    [MemberData(nameof(HostResources))]
    public void NoOtherHostCarriesTheShowcaseOnlyKeys(string language, string winui, string wpf, string winforms)
    {
        // WinUI 側には在ること。無ければ除外表のほうが腐っている
        Dictionary<string, string> reference = Read(winui);
        string[] vanished = [.. ShowcaseOnlyKeys.Except(reference.Keys, StringComparer.Ordinal).Order(StringComparer.Ordinal)];
        Assert.True(
            vanished.Length == 0,
            $"{language}: 除外表に在るのに WinUI から消えたキー: {string.Join(", ", vanished)}。" +
            "除外表のほうを直してください。");

        foreach ((string label, string path) in new[] { ("WPF", wpf), ("WinForms", winforms) })
        {
            string[] leaked = [.. Read(path).Keys
                .Intersect(ShowcaseOnlyKeys, StringComparer.Ordinal)
                .Order(StringComparer.Ordinal)];

            Assert.True(
                leaked.Length == 0,
                $"{language}/{label}: ショーケース専用のキーが紛れ込んでいます: {string.Join(", ", leaked)}。" +
                "ピッカー → 監視の E2E は WinUI ホストだけが持つ決定です (README)。");
        }
    }

    /// <summary>
    /// 同じキーの**文言**も 3 ホストで一致すること。
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
    /// A18 (オーバーレイの static singleton 廃止) を実機で確かめる**唯一の手段**である。
    /// ホストが 2 つ目を開けないと、docs/MANUAL-CHECKS.md §6 は実施不能なまま
    /// 「未実施」と「緑」の区別がつかなくなる。
    /// </para>
    /// <para>
    /// ボタンを消しても**何も落ちない**ので、消えたことに気づける仕掛けがここに要る。
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
    /// <see cref="ShowcaseOnlyKeys"/> に挙げたキーが WinUI ホストに**実在する**こと。
    ///
    /// <para>
    /// 例外表は「在る」の主張なので、これが無いと**綴りを間違えても、キーを消しても緑のまま**になる
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
