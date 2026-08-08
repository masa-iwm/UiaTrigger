using System.Globalization;
using System.Xml.Linq;
using UiaTrigger.Resources;
using Xunit;

namespace UiaTrigger.Tests;

/// <summary>
/// ローカライズの回帰テスト (docs/LOCALIZATION.md §1 / L1-L8)。
/// 翻訳漏れとサテライトの解決失敗を自動検出する。
/// </summary>
public sealed partial class LocalizationTests
{
    private const string NeutralResx = "src/UiaTrigger.Core/Resources/Strings.resx";
    private const string JapaneseResx = "src/UiaTrigger.Core/Resources/Strings.ja.resx";

    private static HashSet<string> ReadKeys(string relativePath)
    {
        string path = RepoPaths.Combine(relativePath.Split('/'));
        Assert.True(File.Exists(path), $"リソースファイルが見つかりません: {path}");

        return [.. XDocument.Load(path)
            .Root!
            .Elements("data")
            .Select(e => (string)e.Attribute("name")!)];
    }

    [Fact]
    public void JapaneseResx_HasExactlyTheSameKeys_AsNeutral()
    {
        HashSet<string> neutral = ReadKeys(NeutralResx);
        HashSet<string> japanese = ReadKeys(JapaneseResx);

        Assert.NotEmpty(neutral);

        string[] missing = [.. neutral.Except(japanese).Order()];
        string[] extra = [.. japanese.Except(neutral).Order()];

        Assert.True(
            missing.Length == 0,
            $"ja-JP に翻訳が無いキー: {string.Join(", ", missing)}");
        Assert.True(
            extra.Length == 0,
            $"ja-JP にのみ存在する余分なキー: {string.Join(", ", extra)}");
    }

    [Fact]
    public void NeutralResx_HasNoEmptyValues()
    {
        foreach (string relativePath in new[] { NeutralResx, JapaneseResx })
        {
            string path = RepoPaths.Combine(relativePath.Split('/'));
            var empty = XDocument.Load(path).Root!
                .Elements("data")
                .Where(e => string.IsNullOrWhiteSpace((string?)e.Element("value")))
                .Select(e => (string)e.Attribute("name")!)
                .ToArray();

            Assert.True(empty.Length == 0, $"{relativePath} に空の値があります: {string.Join(", ", empty)}");
        }
    }

    /// <summary>
    /// サテライトアセンブリが実際に解決されることを確認する。
    /// AOT 発行後の挙動は CI の aot ジョブで別途確認する (docs/LOCALIZATION.md §2)。
    /// </summary>
    [Fact]
    public void ResourceManager_ResolvesJapaneseSatellite()
    {
        var english = new CultureInfo("en-US");
        var japanese = new CultureInfo("ja-JP");

        string? en = Strings.ResourceManager.GetString(nameof(Strings.Error_AlreadyStarted), english);
        string? ja = Strings.ResourceManager.GetString(nameof(Strings.Error_AlreadyStarted), japanese);

        Assert.False(string.IsNullOrEmpty(en), "en-US のリソースが取得できません。");
        Assert.False(string.IsNullOrEmpty(ja), "ja-JP のリソースが取得できません (サテライトが配置されていない可能性)。");
        Assert.NotEqual(en, ja);
        Assert.Contains("監視", ja, StringComparison.Ordinal);
    }

    /// <summary>
    /// 未対応カルチャでは neutral (en-US) にフォールバックすること。
    /// </summary>
    [Fact]
    public void ResourceManager_FallsBackToEnglish_ForUnsupportedCulture()
    {
        string? en = Strings.ResourceManager.GetString(nameof(Strings.Error_AlreadyStarted), new CultureInfo("en-US"));
        string? zh = Strings.ResourceManager.GetString(nameof(Strings.Error_AlreadyStarted), new CultureInfo("zh-CN"));

        Assert.Equal(en, zh);
    }

    /// <summary>
    /// <c>src/**/*.cs</c> の**文字列リテラルに**日本語が入っていないこと (台帳 L2)。
    /// </summary>
    /// <remarks>
    /// <para>
    /// 日本語が正当に居られるのは 3 箇所しかない — 実装内部のコメント (L1)、
    /// <c>Resources/*.ja.xml</c>、<c>*.ja.resx</c>。どれも <c>.cs</c> のコードではない。
    /// したがって「src の <c>.cs</c> のリテラルに日本語が無い」が真の不変条件であり、
    /// この 1 本で分類 2 (リソース経由) と分類 3 (英語固定) の**両方**を縛れる。
    /// </para>
    /// <para>
    /// **形を <c>throw new …;</c> 限定に戻さないこと。**その形は 2 度、別々の理由で漏らした:
    /// 例外を <c>throw</c> せず**返す**ファクトリ (<c>HostOptions.NotInitialized</c>) を
    /// 一切見ないこと、そして文字列リテラルの中の <c>;</c> で文が切れること。
    /// 正規表現を足すのではなく、見る対象をリテラルそのものへ移してある。
    /// </para>
    /// <para>
    /// コメントは対象外だが、**剥がすのは行頭がコメントの行だけ**である。
    /// 行末に付けた <c>// 日本語の説明</c> はリテラルではないので自然に外れ、
    /// <c>"https://例.jp"</c> のような**リテラル内の <c>//</c>** を誤って落とさない。
    /// </para>
    /// </remarks>
    [Fact]
    public void NoSourceLiteralCarriesJapanese()
    {
        string[] files = [.. Directory.EnumerateFiles(
                RepoPaths.Combine("src"), "*.cs", SearchOption.AllDirectories)
            .Where(f => !f.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.Ordinal)
                     && !f.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}", StringComparison.Ordinal))];

        // **0 件で緑にしない。**src の場所が変われば、この検査は何も見ないまま通る
        Assert.True(files.Length > 50, $"走査できた .cs が {files.Length} 件しかありません。");

        var offenders = new List<string>();
        int scanned = 0;
        foreach (string file in files)
        {
            foreach (string literal in StringLiterals(File.ReadAllText(file)))
            {
                scanned++;
                if (literal.Any(IsJapanese))
                {
                    string shown = literal.Length > 80 ? literal[..80] + "…" : literal;
                    offenders.Add(
                        $"{Path.GetRelativePath(RepoPaths.Root.FullName, file)}: {shown.ReplaceLineEndings(" ")}");
                }
            }
        }

        // **陽性対照。**抽出が壊れると offenders が空になって静かに緑になる。
        // 実測はおよそ 490 件なので、下限は「桁が落ちたら気づく」位置に置く
        Assert.True(scanned > 300, $"取り出せた文字列リテラルが {scanned} 件しかありません。");

        Assert.True(
            offenders.Count == 0,
            "ソースの文字列リテラルに日本語が直書きされています (台帳 L2)。" +
            "利用者に出るならリソース経由、出ないなら英語で書くこと (docs/LOCALIZATION.md §3):" +
            Environment.NewLine + string.Join(Environment.NewLine, offenders));
    }

    /// <summary>
    /// C# ソースから文字列リテラルを取り出す。生リテラル (<c>"""…"""</c>)・逐語
    /// (<c>@"…"</c>)・通常の 3 種。行頭がコメントの行は見ない。
    /// </summary>
    /// <remarks>
    /// 完全な字句解析ではない。**取りこぼす方向ではなく余分に拾う方向へ倒してある** —
    /// 誤検出は落ちて直せるが、見落としは静かに通るからである。
    /// </remarks>
    private static IEnumerable<string> StringLiterals(string source)
    {
        // 生リテラルは行をまたぐので先に取り出し、残りを行単位で見る
        foreach (System.Text.RegularExpressions.Match raw in RawStringLiteral().Matches(source))
        {
            yield return raw.Value;
        }

        foreach (string line in RawStringLiteral().Replace(source, "\"\"").Split('\n'))
        {
            string trimmed = line.TrimStart();
            if (trimmed.StartsWith("//", StringComparison.Ordinal) ||
                trimmed.StartsWith('*') ||
                trimmed.StartsWith("/*", StringComparison.Ordinal))
            {
                continue;
            }

            string rest = line;
            foreach (System.Text.RegularExpressions.Match verbatim in VerbatimStringLiteral().Matches(line))
            {
                yield return verbatim.Value;
                rest = rest.Replace(verbatim.Value, "\"\"", StringComparison.Ordinal);
            }
            foreach (System.Text.RegularExpressions.Match plain in StringLiteral().Matches(rest))
            {
                yield return plain.Value;
            }
        }
    }

    /// <summary>ひらがな・カタカナ・CJK 統合漢字。</summary>
    private static bool IsJapanese(char c) =>
        c is (>= '぀' and <= 'ヿ') or (>= '一' and <= '鿿');

    /// <summary>生リテラル <c>"""…"""</c> (行をまたぐ)。</summary>
    [System.Text.RegularExpressions.GeneratedRegex("\"\"\".*?\"\"\"", System.Text.RegularExpressions.RegexOptions.Singleline)]
    private static partial System.Text.RegularExpressions.Regex RawStringLiteral();

    /// <summary>逐語リテラル <c>@"…"</c> (内側の <c>""</c> は 1 つの引用符)。</summary>
    [System.Text.RegularExpressions.GeneratedRegex("@\"(?:[^\"]|\"\")*\"")]
    private static partial System.Text.RegularExpressions.Regex VerbatimStringLiteral();

    /// <summary>通常の文字列リテラル (補間 <c>$"…"</c> も本体は同じ形)。</summary>
    [System.Text.RegularExpressions.GeneratedRegex("\"(?:[^\"\\\\\\n]|\\\\.)*\"")]
    private static partial System.Text.RegularExpressions.Regex StringLiteral();
}
