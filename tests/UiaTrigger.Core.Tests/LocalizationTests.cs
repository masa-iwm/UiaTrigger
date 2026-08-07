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
    /// <c>throw new …</c> に日本語の文面を直書きしないこと (台帳 L2)。
    /// </summary>
    /// <remarks>
    /// <para>
    /// 例外メッセージは分類 2 (ユーザー向け表示) であり、出すならリソース経由である
    /// (docs/LOCALIZATION.md §3)。利用者に出ないプログラマ向けの契約は
    /// <c>TriggerComposer</c> のように**英語で直書き**する。日本語の直書きはどちらでもない。
    /// </para>
    /// <para>
    /// **行単位で見ないこと。**`throw new X("...");  // 日本語のコメント` は正当であり
    /// (実装内部のコメントは日本語が規律 — L1)、行で照合すると必ず偽陽性になる。
    /// ここが見るのは <c>throw new</c> から最初の <c>;</c> までに現れる**文字列リテラルの中身**
    /// だけである。行コメントは `;` の後ろに来るので入らない。
    /// </para>
    /// <para>
    /// ユーザー向けリテラルの検査 (<c>NoSourceAssignsAUserFacingLiteral</c>) はコントロールへの
    /// 代入を見るので、この形は拾わない。**別の網である。**
    /// </para>
    /// </remarks>
    [Fact]
    public void NoThrowStatementCarriesAJapaneseMessage()
    {
        var offenders = new List<string>();
        foreach (string file in Directory.EnumerateFiles(
            RepoPaths.Combine("src"), "*.cs", SearchOption.AllDirectories))
        {
            if (file.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.Ordinal) ||
                file.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}", StringComparison.Ordinal))
            {
                continue; // 生成物 (XAML の分割クラス等) は対象外
            }
            string text = File.ReadAllText(file);
            foreach (System.Text.RegularExpressions.Match statement in ThrowStatement().Matches(text))
            {
                foreach (System.Text.RegularExpressions.Match literal in StringLiteral().Matches(statement.Value))
                {
                    if (literal.Value.Any(IsJapanese))
                    {
                        offenders.Add(
                            $"{Path.GetRelativePath(RepoPaths.Root.FullName, file)}: {literal.Value}");
                    }
                }
            }
        }

        Assert.True(
            offenders.Count == 0,
            "例外メッセージに日本語が直書きされています (台帳 L2)。" +
            "利用者に出るならリソース経由、出ないなら英語で書くこと:" +
            Environment.NewLine + string.Join(Environment.NewLine, offenders));
    }

    /// <summary>ひらがな・カタカナ・CJK 統合漢字。</summary>
    private static bool IsJapanese(char c) =>
        c is (>= '぀' and <= 'ヿ') or (>= '一' and <= '鿿');

    /// <summary><c>throw new</c> から最初の <c>;</c> まで (行コメントは入らない)。</summary>
    [System.Text.RegularExpressions.GeneratedRegex(@"throw new [^;]*;")]
    private static partial System.Text.RegularExpressions.Regex ThrowStatement();

    /// <summary>単純な文字列リテラル (逐語 <c>@""</c> と生リテラルは使っていない)。</summary>
    [System.Text.RegularExpressions.GeneratedRegex("\"(?:[^\"\\\\]|\\\\.)*\"")]
    private static partial System.Text.RegularExpressions.Regex StringLiteral();
}
