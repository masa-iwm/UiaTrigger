// 判断の台帳 (docs/DESIGN.md §13) の「網」列が実態と合っていること。
//
// **台帳は不変条件の一覧であって、守られている証拠ではない。**どの行に網があり、
// どの行が人の目だけで保たれているかは、書いておかないと区別がつかない —
// そして書いただけの列は、次の変更で静かにずれる。ここで両方向に縛る。
using System.Text.RegularExpressions;
using Xunit;

namespace UiaTrigger.Tests;

public sealed class LedgerNetTests
{
    /// <summary>台帳の 1 行 (ID と網の欄)。</summary>
    private sealed record Row(string Id, string Net);

    private static readonly Regex LedgerRow = new(
        @"^\| ([ABCDLS]\d+) \|(?<rest>.*)\|\s*$",
        RegexOptions.CultureInvariant,
        TimeSpan.FromSeconds(1));

    private static List<Row> ReadLedger()
    {
        string[] lines = File.ReadAllLines(RepoPaths.Combine("docs", "DESIGN.md"));
        var rows = new List<Row>();
        foreach (string line in lines)
        {
            Match match = LedgerRow.Match(line);
            if (!match.Success)
            {
                continue;
            }
            // 最後の欄が網。不変条件の本文にも `|` が入りうるので後ろから採る
            string rest = match.Groups["rest"].Value;
            int last = rest.LastIndexOf('|');
            rows.Add(new Row(match.Groups[1].Value, last < 0 ? string.Empty : rest[(last + 1)..].Trim()));
        }
        return rows;
    }

    /// <summary>その ID を doc コメントで引用しているテストのファイル名。</summary>
    private static IReadOnlyList<string> CitedBy(string id)
    {
        var regex = new Regex(@"\b" + id + @"\b(?!\d)", RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1));
        return [.. Directory.EnumerateFiles(RepoPaths.Combine("tests"), "*.cs", SearchOption.AllDirectories)
            .Where(f => !f.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.Ordinal)
                     && !f.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}", StringComparison.Ordinal))
            // この検査自身は台帳を読むだけで、どの不変条件も縛らない
            .Where(f => !string.Equals(Path.GetFileName(f), "LedgerNetTests.cs", StringComparison.Ordinal))
            .Where(f => regex.IsMatch(File.ReadAllText(f)))
            .Select(Path.GetFileName)
            .Select(n => n!)];
    }

    private const string HasNet = "テストが ID で参照";

    /// <summary>
    /// 台帳のすべての行が網の欄を持ち、その値が 3 種のどれかであること。
    /// </summary>
    [Fact]
    public void EveryLedgerRowDeclaresWhetherItHasANet()
    {
        IReadOnlyList<Row> rows = ReadLedger();

        // **0 件で緑にしない。**表の書式が変われば、この検査は何も見ないまま通る
        Assert.True(rows.Count > 50, $"読み取れた台帳の行が {rows.Count} 件しかありません。");

        string[] bad = [.. rows
            .Where(r => !string.Equals(r.Net, HasNet, StringComparison.Ordinal)
                     && !r.Net.StartsWith("網なし", StringComparison.Ordinal)
                     && !string.Equals(r.Net, "欠番", StringComparison.Ordinal))
            .Select(r => $"{r.Id} ('{r.Net}')")];

        Assert.True(
            bad.Length == 0,
            $"網の欄が「{HasNet}」「網なし…」「欠番」のどれでもない行があります: {string.Join(", ", bad)}");
    }

    /// <summary>
    /// 「テストが ID で参照」の行は、実際に <c>tests/</c> のどれかがその ID を引用していること。
    /// </summary>
    /// <remarks>
    /// テスト名ではなく ID の引用で結ぶのは、名前が変わるたびに台帳が腐るからである。
    /// 引用はテストを直す人の目に入る場所に在り、消せばここが落ちる。
    /// </remarks>
    [Fact]
    public void EveryRowClaimingANetIsCitedByATest()
    {
        string[] uncited = [.. ReadLedger()
            .Where(r => string.Equals(r.Net, HasNet, StringComparison.Ordinal))
            .Where(r => CitedBy(r.Id).Count == 0)
            .Select(r => r.Id)];

        Assert.True(
            uncited.Length == 0,
            $"網が在ると書いてあるのに、どのテストも ID を引用していません: {string.Join(", ", uncited)}。" +
            "縛っているテストの doc に ID を書くか、台帳を「網なし」へ直してください。");
    }

    /// <summary>
    /// 逆向き: 「網なし」「欠番」の行は、どのテストからも引用されていないこと。
    /// </summary>
    /// <remarks>
    /// **これが無いと片側だけの検査になる。**網を建てた人が台帳を直さなければ、
    /// 台帳は「人の目だけで保たれている」と言い続ける — 次に読む人が、
    /// 在る網を無いものとして扱うことになる。
    /// </remarks>
    [Fact]
    public void EveryRowClaimingNoNetIsReallyUncited()
    {
        var unexpected = new List<string>();
        foreach (Row row in ReadLedger().Where(r => !string.Equals(r.Net, HasNet, StringComparison.Ordinal)))
        {
            IReadOnlyList<string> cited = CitedBy(row.Id);
            if (cited.Count > 0)
            {
                unexpected.Add($"{row.Id} ({string.Join(", ", cited)})");
            }
        }

        Assert.True(
            unexpected.Count == 0,
            $"「網なし」と書いてあるのにテストが ID を引用しています: {string.Join(" / ", unexpected)}。" +
            "網が建ったなら台帳の欄も直してください。");
    }
}
