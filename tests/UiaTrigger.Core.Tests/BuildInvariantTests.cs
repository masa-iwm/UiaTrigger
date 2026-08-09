// ビルドとパッケージの不変条件 (docs/DESIGN.md D3 / D4)。
//
// **どちらも「設定ファイルに 1 行在るだけ」で保たれている。**外した瞬間に壊れるが、
// 壊れ方が「テストが落ちる」ではないので誰も気づかない — 警告が通るようになるだけ、
// ライセンスが空のパッケージが出るだけである。CI とコンパイラは実効的な門ではあるが、
// 「なぜそうなっているか」を指す先が無い。
using System.Xml.Linq;
using Xunit;

namespace UiaTrigger.Tests;

public sealed class BuildInvariantTests
{
    private static XDocument BuildProps()
    {
        string path = RepoPaths.Combine("Directory.Build.props");
        Assert.True(File.Exists(path), $"Directory.Build.props がありません: {path}");
        return XDocument.Load(path);
    }

    /// <summary>すべての <c>PropertyGroup</c> を横断して、その名前の値を集める。</summary>
    private static string[] ValuesOf(XDocument document, string name) =>
        [.. document.Descendants(name).Select(e => e.Value.Trim())];

    /// <summary>
    /// 警告 0 がビルドの不変条件であること (docs/DESIGN.md D3)。
    /// </summary>
    /// <remarks>
    /// これを外すと、**抑制ではなく無視で**警告が積もる。積もった状態からは
    /// 戻せない (戻した日に何百件も出る) ので、外れていること自体をここで捕まえる。
    /// </remarks>
    [Fact]
    public void WarningsAreErrorsForEveryProject()
    {
        string[] values = ValuesOf(BuildProps(), "TreatWarningsAsErrors");

        Assert.NotEmpty(values);
        Assert.All(values, v => Assert.Equal("true", v, ignoreCase: true));
    }

    /// <summary>
    /// 配るパッケージのライセンスが MIT であること (docs/DESIGN.md D4)。
    /// </summary>
    /// <remarks>
    /// 抜けても <c>dotnet pack</c> は成功する — ライセンスの無いパッケージが
    /// nuget.org に出るだけである。取り下げても、取り込んだ先には残る。
    /// </remarks>
    [Fact]
    public void ThePackagesDeclareTheMitLicense()
    {
        string[] values = ValuesOf(BuildProps(), "PackageLicenseExpression");

        Assert.NotEmpty(values);
        Assert.All(values, v => Assert.Equal("MIT", v, StringComparer.Ordinal));
    }

    /// <summary>
    /// 版数がプレリリースであるかぎり、<c>1.0.0</c> を名乗らないこと (docs/DESIGN.md D4)。
    /// </summary>
    /// <remarks>
    /// 公開 API はまだ動いており、README と CHANGELOG がそう告知している。
    /// <c>1.0.0</c> は「破壊的変更をしない」という約束であって、単なる次の番号ではない。
    /// </remarks>
    [Fact]
    public void TheVersionStaysBelowOnePointZeroWhileTheApiMoves()
    {
        string version = Assert.Single(ValuesOf(BuildProps(), "Version"));

        Assert.StartsWith("0.", version, StringComparison.Ordinal);
    }

    /// <summary>
    /// ライブラリの実行時依存が <c>Microsoft.Extensions.Logging.Abstractions</c> 1 つだけであること
    /// (docs/DESIGN.md C9)。
    /// </summary>
    /// <remarks>
    /// 診断の出口を <c>ILogger</c> にしてあるのは、**実装ではなく抽象**だけに依存するためである。
    /// ここに 1 つ足すと、このライブラリを取り込むすべてのアプリの依存グラフに乗る —
    /// ビルドは通り、テストも通り、気づくのは配ったあとになる。
    ///
    /// <para>
    /// **全プロジェクト共通の <c>PackageReference</c> が居ないことも一緒に見る。**
    /// csproj だけを読む形にすると、`Directory.Build.props` 側に 1 行足された依存を
    /// 「Core は 1 件だけ」と言いながら見逃す。版の集中管理 (`Directory.Packages.props`) は
    /// <c>PackageVersion</c> なので依存そのものではない — 混同しないこと。
    /// </para>
    /// </remarks>
    [Fact]
    public void TheLibraryHasExactlyOneRuntimeDependency()
    {
        string path = RepoPaths.Combine("src", "UiaTrigger.Core", "UiaTrigger.Core.csproj");
        string[] packages = [.. XDocument.Load(path)
            .Descendants("PackageReference")
            .Select(e => (string?)e.Attribute("Include") ?? string.Empty)
            .Where(name => name.Length > 0)];

        Assert.Equal(["Microsoft.Extensions.Logging.Abstractions"], packages);
        Assert.Empty(BuildProps().Descendants("PackageReference"));
    }

    /// <summary>
    /// 3 つのプラットフォームすべてを宣言していること (docs/DESIGN.md D5)。
    /// </summary>
    /// <remarks>
    /// ライブラリは AnyCPU、ホストは <c>Platform</c> から RID を導く。ARM64 が
    /// この一覧から落ちると、**ARM64 のビルドが失敗するのではなく構成そのものが消える** —
    /// CI のマトリクスから静かに落ちる形になる。
    /// </remarks>
    [Fact]
    public void EveryPlatformTheProjectShipsForIsDeclared()
    {
        string platforms = Assert.Single(ValuesOf(BuildProps(), "Platforms"));

        Assert.Equal(["AnyCPU", "x64", "ARM64"], platforms.Split(';', StringSplitOptions.TrimEntries));
    }

    /// <summary>
    /// 配るプロジェクトがソリューションでプラットフォームに固定されていないこと
    /// (docs/DESIGN.md D4 / D5)。
    /// </summary>
    /// <remarks>
    /// <c>UiaTrigger.slnx</c> の <c>&lt;Platform Project="x64" /&gt;</c> は
    /// <c>dotnet pack UiaTrigger.slnx</c> にも効く。**配るプロジェクトに付けると、
    /// アーキ非依存の <c>lib/</c> に特定 CPU の dll が入ったパッケージが出来上がる。**
    /// ARM64 / x86 の利用者は復元だけ通り、読み込みで <c>BadImageFormatException</c> に
    /// なる — 復元時には警告も出ない。<c>Picker.WinUI</c> が実際にこの形で配られた。
    ///
    /// <para>
    /// **Release ワークフローの PE 検査はタグを打った時にしか撃たない。**こちらは毎 push
    /// 撃つので、固定を足した当日に落ちる。アプリ (<c>App.WinUI</c>) の固定は WinUI 3 の
    /// アプリに要るものであり、配らないので対象外である。
    /// </para>
    /// </remarks>
    [Fact]
    public void NoPackableProjectIsPinnedToAPlatformInTheSolution()
    {
        XDocument solution = XDocument.Load(RepoPaths.Combine("UiaTrigger.slnx"));

        var packablePinned = new List<string>();
        int packable = 0;
        foreach (XElement project in solution.Descendants("Project"))
        {
            string relative = ((string?)project.Attribute("Path") ?? string.Empty).Replace('/', Path.DirectorySeparatorChar);
            if (relative.Length == 0)
            {
                continue;
            }
            string full = RepoPaths.Combine(relative.Split(Path.DirectorySeparatorChar));
            if (!File.Exists(full))
            {
                continue;
            }
            bool isPackable = XDocument.Load(full)
                .Descendants("IsPackable")
                .Any(e => string.Equals(e.Value.Trim(), "true", StringComparison.OrdinalIgnoreCase));
            if (!isPackable)
            {
                continue;
            }
            packable++;
            if (project.Elements("Platform").Any())
            {
                packablePinned.Add(relative);
            }
        }

        // **0 件で緑にしない。**slnx の書式が変われば、この検査は何も見ないまま通る
        Assert.Equal(5, packable);
        Assert.True(
            packablePinned.Count == 0,
            "配るプロジェクトが UiaTrigger.slnx でプラットフォームに固定されています: " +
            string.Join(", ", packablePinned) +
            "。ソリューション pack がそれを拾い、lib/ に特定 CPU の dll を配ることになります (docs/DESIGN.md D4/D5)。");
    }

    /// <summary>
    /// 推移的ピン留めが切れていること (docs/DESIGN.md D10)。
    /// </summary>
    /// <remarks>
    /// <c>CentralPackageTransitivePinningEnabled</c> を立てると、推移的に届いた
    /// パッケージのうち <c>PackageVersion</c> の項があるものが**直接参照へ昇格し、
    /// 配る nuspec に直接依存として載る**。README と <c>docs/RELEASING.md</c> §1 の
    /// 依存表は「<c>Core</c> 経由で届く」と案内しており、nuget.org は同梱 README と
    /// Dependencies 欄を**同じページに並べる**ので、立てた日から案内が目の前で食い違う。
    ///
    /// <para>
    /// **立てても利用者が入れるものは変わらない** (実測: 復元は同じ版に解決する)。
    /// 変わるのは宣言だけなので、**ビルドもテストも緑のまま**で誰も気づかない —
    /// だからここで縛る。配る nuspec の側は release.yml が数える。
    /// </para>
    /// </remarks>
    [Fact]
    public void TransitivePinningIsOffSoTheShippedDependenciesAreTheDeclaredOnes()
    {
        XDocument packages = XDocument.Load(RepoPaths.Combine("Directory.Packages.props"));

        // **既定に頼らず明示を要求する。**書いていない状態と切っている状態は
        // 振る舞いこそ同じだが、消した人に理由が伝わらない
        string value = Assert.Single(ValuesOf(packages, "CentralPackageTransitivePinningEnabled"));

        Assert.Equal("false", value, ignoreCase: true);
    }

    /// <summary>
    /// いま配ろうとしている版が <c>CHANGELOG.md</c> に節を持っていること (docs/DESIGN.md D4)。
    /// </summary>
    /// <remarks>
    /// <c>PackageReleaseNotes</c> は CHANGELOG へのリンクなので、節が無いまま出すと
    /// **利用者はリンクを踏んで自分の版が載っていないページに着く**。nuget.org は公開済みの
    /// 版の表示を後から直せないので、その版は永久にそうなる。
    ///
    /// <para>
    /// **これが捕まえるのは「節が丸ごと無い」だけである。**節の中身が実装と合っているかは
    /// 機械では見られない (散文と実装の一致に機械的な判定基準が無い) — そこは人が読むしかない。
    /// 検出力を過大に読まないこと。
    /// </para>
    /// </remarks>
    [Fact]
    public void TheVersionBeingShippedHasAChangelogSection()
    {
        string version = Assert.Single(ValuesOf(BuildProps(), "Version"));
        string[] headings = [.. File.ReadAllLines(RepoPaths.Combine("CHANGELOG.md"))
            .Where(line => line.StartsWith("## ", StringComparison.Ordinal))
            .Select(line => line[3..].Trim())];

        // **0 件で緑にしない。**見出しの書式が変われば、この検査は何も見ないまま通る
        Assert.NotEmpty(headings);
        Assert.True(
            headings.Contains(version, StringComparer.Ordinal),
            $"CHANGELOG.md に「## {version}」の節がありません (在る節: {string.Join(", ", headings)})。");
    }
}
