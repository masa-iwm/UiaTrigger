// 文書とコメントが引用するテスト名が実在すること。
//
// **散文は実装と一緒に動かない。**テストの名前を変えても、それを引用している文書は
// 何も言わずに古いままになる — そして手動チェックリストは「自動の網はこれだ」と
// 名前で指しているので、名前が腐ると**「自動で見ているつもり」の項目が誰も見ていない
// 項目に化ける**。実例: `docs/MANUAL-CHECKS.md` が枠側のクラスを見るテストを引用していたが、
// 実在するのはアイコン側を見るテストのほうで、枠側では登録が落ちても緑になる。
//
// **引用の形を限定して拾う。**`Foo.Bar` (クラス.メソッド) と `FooTests` (クラス) だけを
// 見る。散文に出るあらゆる識別子を相手にすると、雑音のほうが多くなって誰も直さなくなる。
using System.Text.RegularExpressions;
using Xunit;

namespace UiaTrigger.Tests;

public sealed class DocumentationReferenceTests
{
    /// <summary>引用を探す先。散文と、テストのソースコメントの両方を見る。</summary>
    private static IEnumerable<string> SourcesToScan()
    {
        foreach (string path in Directory.EnumerateFiles(RepoPaths.Combine("docs"), "*.md"))
        {
            yield return path;
        }
        foreach (string path in Directory.EnumerateFiles(RepoPaths.Combine("tests"), "*.cs", SearchOption.AllDirectories))
        {
            if (path.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.Ordinal)
                || path.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}", StringComparison.Ordinal)
                // この検査自身は例として名前を書くので対象外である
                || string.Equals(Path.GetFileName(path), "DocumentationReferenceTests.cs", StringComparison.Ordinal))
            {
                continue;
            }
            yield return path;
        }
    }

    /// <summary>
    /// 引用の形。Markdown の <c>`X`</c> と XML doc の <c>&lt;c&gt;X&lt;/c&gt;</c> の両方を受ける。
    /// 末尾の三点リーダーは**意図的な省略**なので、前方一致で照合する。
    /// </summary>
    private static readonly Regex Citation = new(
        @"(?:`|<c>)(?<class>[A-Za-z][A-Za-z0-9_]*Tests)(?:\.(?<method>[A-Za-z][A-Za-z0-9_]*))?(?<cut>…)?(?:`|</c>)",
        RegexOptions.CultureInvariant,
        TimeSpan.FromSeconds(5));

    /// <summary>テストのソースに実在するクラス名 → そのファイルのメソッド名。</summary>
    private static readonly Regex ClassDeclaration = new(
        @"\bclass\s+(?<name>[A-Za-z][A-Za-z0-9_]*Tests)\b",
        RegexOptions.CultureInvariant,
        TimeSpan.FromSeconds(5));

    // **戻り値の型で絞らない。**ヘルパーは void でも Task でもないことがあり
    // (<c>ArrowKeyTests.WaitForFrame</c> は Rect を返す)、絞ると「実在するのに無いと言う」
    // 偽陽性になる — 検査が信用されなくなる壊れ方はこちらである。
    private static readonly Regex MethodDeclaration = new(
        @"\b(?:public|private|internal|protected)\s+(?:static\s+|async\s+|override\s+|virtual\s+|sealed\s+|partial\s+|readonly\s+)*[\w<>,\.\[\]\?]+\s+(?<name>[A-Za-z][A-Za-z0-9_]*)\s*\(",
        RegexOptions.CultureInvariant,
        TimeSpan.FromSeconds(5));

    private static Dictionary<string, HashSet<string>> ReadTestSurface()
    {
        var surface = new Dictionary<string, HashSet<string>>(StringComparer.Ordinal);
        foreach (string path in Directory.EnumerateFiles(RepoPaths.Combine("tests"), "*.cs", SearchOption.AllDirectories))
        {
            if (path.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.Ordinal)
                || path.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}", StringComparison.Ordinal))
            {
                continue;
            }
            string text = File.ReadAllText(path);
            var methods = new HashSet<string>(
                MethodDeclaration.Matches(text).Select(m => m.Groups["name"].Value),
                StringComparer.Ordinal);
            foreach (Match declaration in ClassDeclaration.Matches(text))
            {
                string name = declaration.Groups["name"].Value;
                if (!surface.TryGetValue(name, out HashSet<string>? existing))
                {
                    surface[name] = [.. methods];
                }
                else
                {
                    existing.UnionWith(methods);
                }
            }
        }
        return surface;
    }

    /// <summary>
    /// 文書とテストのコメントが引用するテスト名が実在すること。
    /// </summary>
    /// <remarks>
    /// **落ちたら、直すのは名前を書いたほうである。**引用のほうが正しくてテストを消したのなら、
    /// その項目は自動では見られなくなっているので、文書の側もそう書き直すことになる —
    /// 引用を消して終わりにすると「網が在る」という嘘だけが残る。
    /// </remarks>
    [Fact]
    public void EveryTestNameCitedInProseExists()
    {
        Dictionary<string, HashSet<string>> surface = ReadTestSurface();
        var bad = new List<string>();
        int cited = 0;

        foreach (string path in SourcesToScan())
        {
            string[] lines = File.ReadAllLines(path);
            for (int i = 0; i < lines.Length; i++)
            {
                foreach (Match citation in Citation.Matches(lines[i]))
                {
                    string className = citation.Groups["class"].Value;
                    string method = citation.Groups["method"].Value;
                    bool truncated = citation.Groups["cut"].Success;
                    cited++;

                    if (!surface.TryGetValue(className, out HashSet<string>? methods))
                    {
                        bad.Add($"{Path.GetFileName(path)}:{i + 1} — テストクラス {className} が実在しません");
                        continue;
                    }
                    if (method.Length == 0)
                    {
                        continue;
                    }
                    bool found = truncated
                        ? methods.Any(m => m.StartsWith(method, StringComparison.Ordinal))
                        : methods.Contains(method);
                    if (!found)
                    {
                        bad.Add($"{Path.GetFileName(path)}:{i + 1} — {className} に {method} がありません");
                    }
                }
            }
        }

        // **0 件で緑にしない。**引用の書式が変われば、この検査は何も見ないまま通る
        Assert.True(cited > 20, $"拾えた引用が {cited} 件しかありません。Citation の書式を確認してください。");
        Assert.True(bad.Count == 0, $"実在しないテスト名を引用しています:{Environment.NewLine}{string.Join(Environment.NewLine, bad)}");
    }
}
