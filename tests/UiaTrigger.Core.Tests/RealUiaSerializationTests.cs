using System.Text.RegularExpressions;
using Xunit;

namespace UiaTrigger.Tests;

/// <summary>
/// **実 UIA を起こしうる T1 が、直列化の collection に入っていること** (docs/TESTING.md §5)。
///
/// <para>
/// <c>CUIAutomation8</c> の生成と <c>GetRootElement</c> は並列に何本も走らせると単発で
/// <c>COMException</c> (E_FAIL) になる — 実測に基づいて <see cref="RealUiaLiteTests"/> が
/// 直列化の collection を定義している。ところが**その括りは文章にしか無く**、
/// 属性を付け忘れたクラスを見つける手段が無かった。症状は「ときどき落ちるテスト」であり、
/// 検出力を失ったテストと同じである。§5 は「名前で覚えず『実 UIA を触る T1』で括ること」と
/// 書いている — その括りをここで機械化する。
/// </para>
/// <para>
/// **判定は「作ったか」で行い、「実際に UIA へ届くか」では行わない。**
/// <c>UiaSession.Context</c> は遅延生成なので、構築しただけのテストは今のところ
/// <c>CUIAutomation8</c> を作らない。だが届くかどうかは呼び先の遅延生成に依存し、
/// テストに 1 行足すだけで裏返る — 判定条件にすると「昨日まで正しかった検査」が
/// 黙って穴を開ける。過大に括る側へ倒す (余分な直列化のコストは、間欠的な赤より安い)。
/// </para>
/// </summary>
public sealed class RealUiaSerializationTests
{
    /// <summary>
    /// 実 UIA を起こしうる合図。名前空間で完全修飾した形も拾う — 実際にその書き方の
    /// クラスが括りの外に居た。
    /// </summary>
    private static readonly Regex CreatesRealUia = new(
        @"\bnew\s+(?:[\w.]+\.)?(?:TriggerMonitor|UiaSession)\s*\(",
        RegexOptions.CultureInvariant,
        TimeSpan.FromSeconds(1));

    /// <summary>
    /// **合図を探す前に、文字列リテラルとコメントを落とす。**
    /// この規律を書いた側のテスト (<c>HostMonitorTests</c> やこのクラス自身) は合図を
    /// データや例示として持っており、素で数えると自分を告発する。
    /// </summary>
    /// <remarks>
    /// リテラル → ブロックコメント → 行コメントの順。コメント中の <c>"</c> が
    /// リテラル扱いになると、その行の残りを余分に食う — 落とす方向にしかずれないので
    /// 見落としにはならない。
    /// </remarks>
    private static readonly Regex[] NotCode =
    [
        new("\"[^\"\\n]*\"", RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1)),
        new(@"/\*.*?\*/", RegexOptions.Singleline | RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1)),
        new("//[^\n]*", RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1)),
    ];

    private static string CodeOnly(string source)
    {
        foreach (Regex pattern in NotCode)
        {
            source = pattern.Replace(source, string.Empty);
        }

        return source;
    }

    [Fact]
    public void EveryTestFileThatCreatesRealUia_IsInTheSerializedCollection()
    {
        string directory = RepoPaths.Combine("tests", "UiaTrigger.Core.Tests");
        string[] files = Directory.GetFiles(directory, "*.cs");

        // **0 件で緑にしない。**ディレクトリの場所が変われば、この検査は何も見ないまま通る
        Assert.True(files.Length > 20, $"走査できた .cs が {files.Length} 件しかありません ({directory})。");

        var offenders = new List<string>();
        var covered = new List<string>();
        foreach (string file in files)
        {
            // 属性の在処も同じ「コードだけ」の上で見る。コメントアウトされた属性や
            // 文字列に書いただけの属性を「付いている」と数えないためである
            string code = CodeOnly(File.ReadAllText(file));
            if (!CreatesRealUia.IsMatch(code))
            {
                continue;
            }

            string name = Path.GetFileName(file);
            (code.Contains("[Collection(RealUiaLiteTests.Name)]", StringComparison.Ordinal) ? covered : offenders)
                .Add(name);
        }

        // **陽性対照。**1 つも「実 UIA を起こしうる」と判定できていないなら、
        // この検査は探せていないまま緑を出している
        Assert.True(
            covered.Count >= 3,
            $"実 UIA を起こしうると判定できた T1 が {covered.Count} 件しかありません。" +
            "合図の書き方が変わったか、この検査の探し方が壊れています。");

        Assert.True(
            offenders.Count == 0,
            "TriggerMonitor / UiaSession を作るのに [Collection(RealUiaLiteTests.Name)] が付いていない " +
            $"テストがあります: {string.Join(", ", offenders)}。並列に走ると CUIAutomation8 の生成が " +
            "単発で失敗します (docs/TESTING.md §5)。");
    }
}
