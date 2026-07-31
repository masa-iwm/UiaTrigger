// ピッカーの文字列を**リポジトリのリソースファイルから**読む (docs/TESTING.md §1)。
//
// 期待文字列をテストへ書き写さないためである。書き写すと、翻訳を直したときに
// テストだけが古い正解を守り続ける。
using System.Xml.Linq;
using UiaTrigger.Tests;
using Xunit;

namespace UiaTrigger.Picker.UiTests;

internal static class PickerResources
{
    /// <summary>
    /// そのカルチャで期待される文字列。
    /// </summary>
    /// <remarks>
    /// 供給元が変種で違う: WinUI は自分の <c>.resw</c> (MRT)、WPF / WinForms は
    /// <c>Picker.Core</c> の <c>.resx</c> (<c>ResxPickerStrings</c>)。
    /// **キーの集合は同じ** (<c>PickerStringKeys</c>) だが、値の出所と解決経路が違う —
    /// S4 が見ているのはまさにそこである。
    /// </remarks>
    public static Dictionary<string, string> For(PickerHostProfile profile, string culture)
    {
        bool japanese = culture.StartsWith("ja", StringComparison.OrdinalIgnoreCase);
        string path = profile == PickerHostProfile.WinUI
            ? RepoPaths.Combine(
                "src", "UiaTrigger.Picker.WinUI", "Strings", japanese ? "ja-jp" : "en-us", "Resources.resw")
            : RepoPaths.Combine(
                "src", "UiaTrigger.Picker.Core", "Resources", japanese ? "Strings.ja.resx" : "Strings.resx");

        Assert.True(File.Exists(path), $"リソースファイルが見つかりません: {path}");

        // .resw と .resx は同じ XML 構造なので読み手は 1 つで足りる
        // (T1 の XamlLocalizationTests.Read と同じ形。プロジェクトが別なので写している)
        return XDocument.Load(path).Root!
            .Elements("data")
            .ToDictionary(
                e => (string)e.Attribute("name")!,
                e => (string)e.Element("value")!,
                StringComparer.Ordinal);
    }
}
