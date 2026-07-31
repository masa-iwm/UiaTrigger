using System.Globalization;
using System.Xml.Linq;
using UiaTrigger.Resources;
using Xunit;

namespace UiaTrigger.Tests;

/// <summary>
/// ローカライズの回帰テスト (docs/LOCALIZATION.md §1 / L1-L8)。
/// 翻訳漏れとサテライトの解決失敗を自動検出する。
/// </summary>
public sealed class LocalizationTests
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
}
