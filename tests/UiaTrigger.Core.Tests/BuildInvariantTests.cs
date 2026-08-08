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
}
