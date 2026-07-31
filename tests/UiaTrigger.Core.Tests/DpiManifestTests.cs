using System.Xml.Linq;
using Xunit;

namespace UiaTrigger.Tests;

/// <summary>
/// DPI 認識の宣言が失われていないことを固定する (docs/TESTING.md §1 T2 / §4)。
///
/// unpackaged な WinUI 3 アプリで PerMonitorV2 の宣言が無いと、Windows が座標を仮想化し、
/// スクリーン座標で届く WM_MOUSEWHEEL のヒットテストがコンテンツ外に落ちて
/// マウスホイールが効かなくなる。キーボードはヒットテスト不要なので影響を受けないため、
/// 「キーボードは効くのにホイールだけ死ぬ」という切り分けの難しい症状になる。
///
/// 過去にこの原因の特定へ数日を要した。このテスト群はその再発を初日に検出するためにある。
/// </summary>
public sealed class DpiManifestTests
{
    private static readonly XNamespace Settings2016 = "http://schemas.microsoft.com/SMI/2016/WindowsSettings";
    private static readonly XNamespace Settings2005 = "http://schemas.microsoft.com/SMI/2005/WindowsSettings";

    /// <summary>
    /// manifest が PerMonitorV2 を宣言しているか検査する。
    /// 問題があれば理由を返し、無ければ null を返す。
    /// </summary>
    private static string? Validate(XDocument manifest)
    {
        string? dpiAwareness = manifest
            .Descendants(Settings2016 + "dpiAwareness")
            .Select(e => e.Value.Trim())
            .FirstOrDefault();

        if (dpiAwareness is null)
        {
            return "<dpiAwareness> (SMI/2016) の宣言がありません。" +
                   "これが無いとプロセスが DPI 非認識になり、マウスホイールが効かなくなります。";
        }
        if (!dpiAwareness.Contains("PerMonitorV2", StringComparison.Ordinal))
        {
            return $"<dpiAwareness> が PerMonitorV2 ではありません: '{dpiAwareness}'";
        }

        // 旧 OS 向けフォールバック。PerMonitorV2 を解釈できない環境で PerMonitor として動く。
        string? dpiAware = manifest
            .Descendants(Settings2005 + "dpiAware")
            .Select(e => e.Value.Trim())
            .FirstOrDefault();

        if (dpiAware is null)
        {
            return "<dpiAware> (SMI/2005) のフォールバック宣言がありません。";
        }
        return dpiAware == "true/pm" ? null : $"<dpiAware> が 'true/pm' ではありません: '{dpiAware}'";
    }

    private static void AssertValid(XDocument manifest, string what)
        => Assert.True(Validate(manifest) is null, $"{what}: {Validate(manifest)}");

    /// <summary>
    /// PerMonitorV2 を宣言していなければならないプロジェクト。
    ///
    /// <para>
    /// ホスト (App) だけでなく **T3 の対象アプリ** も含む。DPI 非認識のプロセスは
    /// 座標を仮想化され、UIA の <c>BoundingRectangle</c> (物理座標) と食い違う。
    /// 対象アプリ側でこれが崩れると、座標から記録したつもりの定義が**別の要素**を指し、
    /// T3 は落ちるのではなく静かに嘘をつく (docs/DESIGN.md A19)。
    /// </para>
    /// <para>
    /// WinForms のプロジェクトがここに無いのは、WinForms が manifest ではなく
    /// <c>Application.SetHighDpiMode</c> (WFO0003) での宣言を要求するためである。
    /// アナライザーが manifest 側の DPI 設定をエラーにするので、**置きたくても置けない**。
    /// そちらは <see cref="HighDpiModeProjects"/> の側で見る。
    /// </para>
    /// </summary>
    private static readonly string[] ManifestProjectPaths =
    [
        Path.Combine("src", "UiaTrigger.App.WinUI"),
        Path.Combine("src", "UiaTrigger.App.Wpf"),
        Path.Combine("tests", "UiaTrigger.TestTarget.Wpf"),
    ];

    public static TheoryData<string> ManifestProjects => [.. ManifestProjectPaths];

    /// <summary>
    /// PerMonitorV2 を <c>Application.SetHighDpiMode</c> で宣言するプロジェクト (Windows Forms)。
    ///
    /// <para>
    /// manifest を全ホストに置く形は**成立しない**。
    /// Windows Forms のアナライザー WFO0003 が「高 DPI 設定を app.manifest から削除して
    /// SetHighDpiMode を使え」とエラーにし、<c>TreatWarningsAsErrors=true</c> の
    /// この repo ではビルドが通らない。
    /// </para>
    /// <para>
    /// 経路が 2 つに分かれる以上、**どちらも通っていないプロジェクトが出ないこと**を
    /// 見る必要がある (<see cref="EveryHostDeclaresPerMonitorV2SomeWay"/>)。
    /// 「どちらの表にも載っていないから緑」が起きるのがいちばん困る形である。
    /// </para>
    /// </summary>
    private static readonly string[] HighDpiModeProjectPaths =
    [
        Path.Combine("src", "UiaTrigger.App.WinForms"),
        Path.Combine("tests", "UiaTrigger.TestTarget"),
    ];

    public static TheoryData<string> HighDpiModeProjects => [.. HighDpiModeProjectPaths];

    /// <summary>ピッカーを開けるホスト。DPI 認識はどれか 1 つの経路で宣言されていなければならない。</summary>
    private static readonly string[] HostPaths =
    [
        Path.Combine("src", "UiaTrigger.App.WinUI"),
        Path.Combine("src", "UiaTrigger.App.Wpf"),
        Path.Combine("src", "UiaTrigger.App.WinForms"),
    ];

    public static TheoryData<string> Hosts => [.. HostPaths];

    /// <summary>ソースの app.manifest が PerMonitorV2 を宣言していること。</summary>
    [Theory]
    [MemberData(nameof(ManifestProjects))]
    public void SourceManifest_DeclaresPerMonitorV2(string project)
    {
        string path = Path.Combine(RepoPaths.Root.FullName, project, "app.manifest");
        Assert.True(File.Exists(path), $"app.manifest が見つかりません: {path}");

        AssertValid(XDocument.Load(path), $"{project}/app.manifest");
    }

    /// <summary>
    /// csproj が app.manifest を実際に取り込んでいること。
    /// ファイルが存在していても &lt;ApplicationManifest&gt; の指定が外れると無意味になるため、
    /// 配線そのものを検証する。
    /// </summary>
    [Theory]
    [MemberData(nameof(ManifestProjects))]
    public void Project_WiresUpApplicationManifest(string project)
    {
        string directory = Path.Combine(RepoPaths.Root.FullName, project);
        string csproj = Path.Combine(directory, Path.GetFileName(directory) + ".csproj");
        string? value = XDocument.Load(csproj)
            .Descendants("ApplicationManifest")
            .Select(e => e.Value.Trim())
            .FirstOrDefault();

        Assert.True(
            value is not null,
            $"{project} の csproj に <ApplicationManifest> がありません。" +
            "app.manifest はビルドに取り込まれず、DPI 非認識のプロセスになります。");
        Assert.Equal("app.manifest", value);
    }

    /// <summary>
    /// PerMonitorV2 の宣言が**ビルド成果物にまで残っている**こと。
    ///
    /// <para>
    /// ソースに app.manifest があり csproj が指していても、取り込みの過程で落ちれば
    /// プロセスは DPI 非認識になる。埋め込み方はツールチェーンで違うので両方見る:
    /// </para>
    /// <list type="bullet">
    ///   <item>Windows App SDK は <c>obj/**/Manifests/app.manifest</c> に**マージ済み**の
    ///     manifest を出す (自前の manifest と統合される)。存在すればそれを XML として検査する</item>
    ///   <item>素の apphost (WPF / Win32) はマージ段階を持たず、manifest を exe の
    ///     Win32 リソースへ**直接埋め込む**。中間ファイルが無いので、
    ///     成果物のバイト列に宣言が入っていることで確かめる
    ///     (発行出力の <c>.pri</c> を同じやり方で確かめている — docs/LOCALIZATION.md §2)</item>
    /// </list>
    /// </summary>
    [Theory]
    [MemberData(nameof(ManifestProjects))]
    public void BuiltBinary_CarriesThePerMonitorV2Declaration(string project)
    {
        string projectDir = Path.Combine(RepoPaths.Root.FullName, project);
        string objDir = Path.Combine(projectDir, "obj");
        Assert.True(
            Directory.Exists(objDir),
            $"{project} がビルドされていません ({objDir} がありません)。先に dotnet build を実行してください。");

        // (1) マージ段階を持つツールチェーンなら、その成果物を XML として検査する
        foreach (string path in Directory
            .EnumerateFiles(objDir, "app.manifest", SearchOption.AllDirectories)
            .Where(p => p.Contains(Path.Combine("Manifests", ""), StringComparison.OrdinalIgnoreCase)))
        {
            AssertValid(XDocument.Load(path), Path.GetRelativePath(RepoPaths.Root.FullName, path));
        }

        // (2) どのツールチェーンでも、最終的な exe には宣言が入っていなければならない
        string exeName = Path.GetFileName(projectDir) + ".exe";
        string binDir = Path.Combine(projectDir, "bin");
        Assert.True(Directory.Exists(binDir), $"{project} の bin がありません: {binDir}");

        string[] executables = [.. Directory.EnumerateFiles(binDir, exeName, SearchOption.AllDirectories)];
        Assert.True(
            executables.Length > 0,
            $"{exeName} が見つかりません ({binDir})。先に dotnet build を実行してください。");

        foreach (string exe in executables)
        {
            byte[] bytes = File.ReadAllBytes(exe);
            string where = Path.GetRelativePath(RepoPaths.Root.FullName, exe);
            Assert.True(
                Contains(bytes, "PerMonitorV2"),
                $"{where} に PerMonitorV2 の宣言が埋め込まれていません。" +
                "<ApplicationManifest> の配線か manifest の取り込みが外れています。");
            Assert.True(
                Contains(bytes, "true/pm"),
                $"{where} に dpiAware='true/pm' のフォールバック宣言が埋め込まれていません。");
        }
    }

    /// <summary>manifest は Win32 リソースに UTF-8 で埋め込まれる。</summary>
    private static bool Contains(byte[] haystack, string ascii)
        => haystack.AsSpan().IndexOf(System.Text.Encoding.ASCII.GetBytes(ascii).AsSpan()) >= 0;

    /// <summary>
    /// Windows Forms のプロジェクトが <c>Application.SetHighDpiMode(PerMonitorV2)</c> を
    /// **ウィンドウを作る前に**呼んでいること。
    ///
    /// <para>
    /// 呼ぶ位置が要点である。<c>SetHighDpiMode</c> はプロセスが最初のウィンドウを作るまでしか
    /// 効かず、遅れると**黙って無視される** (戻り値 false)。とくに
    /// <c>ApplicationConfiguration.Initialize()</c> は既定値でこれを呼んでしまうので、
    /// その後に書くと自分の指定が通らない。
    /// </para>
    /// </summary>
    [Theory]
    [MemberData(nameof(HighDpiModeProjects))]
    public void DeclaresPerMonitorV2TheWindowsFormsWay(string project)
    {
        string directory = Path.Combine(RepoPaths.Root.FullName, project);
        string[] sources = [.. Directory
            .EnumerateFiles(directory, "*.cs", SearchOption.AllDirectories)
            .Where(p => !p.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.Ordinal)
                     && !p.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}", StringComparison.Ordinal))];

        string? declaring = sources.FirstOrDefault(p =>
            File.ReadAllText(p).Contains("SetHighDpiMode(HighDpiMode.PerMonitorV2)", StringComparison.Ordinal));

        Assert.True(
            declaring is not null,
            $"{project}: Application.SetHighDpiMode(HighDpiMode.PerMonitorV2) の呼び出しがありません。" +
            "DPI 非認識のプロセスになり、座標から記録した定義が別の要素を指します。");

        string text = File.ReadAllText(declaring!);
        int declaration = text.IndexOf("SetHighDpiMode(HighDpiMode.PerMonitorV2)", StringComparison.Ordinal);
        int initialize = text.IndexOf("ApplicationConfiguration.Initialize()", StringComparison.Ordinal);
        if (initialize >= 0)
        {
            Assert.True(
                declaration < initialize,
                $"{project}: SetHighDpiMode が ApplicationConfiguration.Initialize() より後にあります。" +
                "Initialize は既定値で SetHighDpiMode を呼ぶため、後から指定しても効きません。");
        }
    }

    /// <summary>
    /// ピッカーを開けるホストが、**どれか 1 つの経路で**必ず PerMonitorV2 を宣言していること。
    ///
    /// <para>
    /// 経路が 2 つに分かれた以上、これが要る。ホストを 1 つ足して**どちらの表にも
    /// 載せ忘れる**と、上の検査はどちらも「対象外」として素通りし、全部緑のまま
    /// DPI 非認識のホストが 1 つ増える。ホイールバグはそこから始まった (docs/TESTING.md §4)。
    /// </para>
    /// </summary>
    [Theory]
    [MemberData(nameof(Hosts))]
    public void EveryHostDeclaresPerMonitorV2SomeWay(string host)
    {
        bool viaManifest = ManifestProjectPaths.Contains(host, StringComparer.Ordinal);
        bool viaApi = HighDpiModeProjectPaths.Contains(host, StringComparer.Ordinal);

        Assert.True(
            viaManifest || viaApi,
            $"{host} が DPI 宣言の検査対象になっていません。" +
            "ManifestProjects か HighDpiModeProjects のどちらかに載せてください — " +
            "どちらにも無いホストは、宣言が無くても全テストが緑のまま通ります。");
    }

    // ---- ネガティブコントロール ----
    // 「検出力を証明できないハーネスは信頼しない」(docs/TESTING.md §2)。
    // 上の検査が、実際に壊れた manifest を落とせることをここで示す。

    private const string ManifestWithoutDpi = """
        <assembly manifestVersion="1.0" xmlns="urn:schemas-microsoft-com:asm.v1">
          <assemblyIdentity version="1.0.0.0" name="Broken" />
          <application xmlns="urn:schemas-microsoft-com:asm.v3">
            <windowsSettings />
          </application>
        </assembly>
        """;

    private const string ManifestWithSystemDpiOnly = """
        <assembly manifestVersion="1.0" xmlns="urn:schemas-microsoft-com:asm.v1">
          <assemblyIdentity version="1.0.0.0" name="Broken" />
          <application xmlns="urn:schemas-microsoft-com:asm.v3">
            <windowsSettings>
              <dpiAware xmlns="http://schemas.microsoft.com/SMI/2005/WindowsSettings">true</dpiAware>
              <dpiAwareness xmlns="http://schemas.microsoft.com/SMI/2016/WindowsSettings">System</dpiAwareness>
            </windowsSettings>
          </application>
        </assembly>
        """;

    [Theory]
    [InlineData(ManifestWithoutDpi)]
    [InlineData(ManifestWithSystemDpiOnly)]
    public void Validate_RejectsManifestsThatWouldReintroduceTheWheelBug(string xml)
    {
        string? error = Validate(XDocument.Parse(xml));

        Assert.False(
            error is null,
            "DPI 宣言が欠けた manifest を検査が通してしまいました。" +
            "この検査には検出力がなく、ホイールバグの再発を防げません。");
    }
}
