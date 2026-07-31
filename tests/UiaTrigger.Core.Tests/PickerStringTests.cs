using System.Globalization;
using System.Xml.Linq;
using UiaTrigger.Picker;
using Xunit;

namespace UiaTrigger.Tests;

/// <summary>
/// ピッカーの文字列が「1 つのキー表・2 つの供給経路」で保たれていることの回帰
/// (docs/DESIGN.md L3 / docs/LOCALIZATION.md §4)。
///
/// <para>
/// キーの出所は <see cref="PickerStringKeys"/> と <see cref="EditorStringKeys"/> の 2 表で、
/// 値の供給だけが 2 経路ある: WinUI は <c>.resw</c> + MRT Core、
/// WPF / WinForms は <c>.resx</c> + <c>ResourceManager</c>。
/// WPF と WinForms は resx を<b>共有する</b>
/// (仕組みが同一なので、分けると全キー × 2 言語を手で二重管理することになる)。
/// </para>
/// <para>
/// <b>表を 2 つに割ったのは窓が 2 つあるからである</b> (docs/LOCALIZATION.md §4)。供給経路は
/// 2 表を合わせたものを持つが、「View がキー表のラベルをすべて要求すること」を見ている検査は
/// 窓ごとに別々でなければならない — 混ぜると、ピッカーに存在しないエディタのコントロールを
/// ピッカーの View が要求するはめになる。
/// </para>
/// <para>
/// ここで塞ぐのは<b>片方にだけ足す</b>という壊れ方である。文字列が引けないことは
/// 例外にならず、その View だけ画面にキー名が出る形で現れる。
/// </para>
/// </summary>
public sealed class PickerStringTests
{
    private const string ReswProject = "src/UiaTrigger.Picker.WinUI";
    private const string ResxProject = "src/UiaTrigger.Picker.Core";

    /// <summary>2 つのキー表を合わせたもの。供給経路が持つべきキーの全体である。</summary>
    private static IReadOnlyList<string> AllKeys => [.. PickerStringKeys.All, .. EditorStringKeys.All];

    /// <summary>キー表 1 つぶん (型と、その <c>All</c>)。</summary>
    public static TheoryData<string> KeyTables =>
    [
        nameof(PickerStringKeys),
        nameof(EditorStringKeys),
    ];

    private static (Type Type, IReadOnlyList<string> All) TableFor(string name) => name switch
    {
        nameof(PickerStringKeys) => (typeof(PickerStringKeys), PickerStringKeys.All),
        nameof(EditorStringKeys) => (typeof(EditorStringKeys), EditorStringKeys.All),
        _ => throw new ArgumentOutOfRangeException(nameof(name), name, "知らないキー表です。"),
    };

    /// <summary>供給経路 1 つぶんのリソースファイル。</summary>
    /// <param name="Route">経路の名前 (失敗メッセージ用)。</param>
    /// <param name="Path">リポジトリルートからの相対パス。</param>
    /// <param name="IsPrimary">プライマリ (en-US) なら true。</param>
    public static TheoryData<string, string, bool> ResourceFiles =>
    [
        ("WinUI (.resw, en-us)", $"{ReswProject}/Strings/en-us/Resources.resw", true),
        ("WinUI (.resw, ja-jp)", $"{ReswProject}/Strings/ja-jp/Resources.resw", false),
        ("WPF+WinForms (.resx, neutral)", $"{ResxProject}/Resources/Strings.resx", true),
        ("WPF+WinForms (.resx, ja)", $"{ResxProject}/Resources/Strings.ja.resx", false),
    ];

    private static Dictionary<string, string> Read(string relativePath)
    {
        string path = RepoPaths.Combine(relativePath.Split('/'));
        Assert.True(File.Exists(path), $"リソースファイルがありません: {path}");
        // .resw と .resx は同じ XML 構造である (レイアウトだけが違う) ので読み手は 1 つで足りる
        return XDocument.Load(path).Root!
            .Elements("data")
            .ToDictionary(e => (string)e.Attribute("name")!, e => (string)e.Element("value")!, StringComparer.Ordinal);
    }

    /// <summary>
    /// どの供給経路も<b>2 つのキー表を合わせたもの</b>と過不足なく一致すること。
    ///
    /// 「片方の経路にだけ新しいキーを足す」は、その View だけ画面にキー名が出る形で現れ、
    /// <b>ビルドも実行も通る</b>。キー表を起点に全経路を回すのがここの目的である。
    /// </summary>
    [Theory]
    [MemberData(nameof(ResourceFiles))]
    public void EverySupplyRouteCarriesExactlyTheKeyTable(string route, string path, bool isPrimary)
    {
        _ = isPrimary;
        HashSet<string> declared = [.. AllKeys];
        HashSet<string> actual = [.. Read(path).Keys];

        string[] missing = [.. declared.Except(actual, StringComparer.Ordinal).Order(StringComparer.Ordinal)];
        string[] extra = [.. actual.Except(declared, StringComparer.Ordinal).Order(StringComparer.Ordinal)];

        Assert.True(missing.Length == 0, $"{route}: キー表にあって供給されないキー: {string.Join(", ", missing)}");
        Assert.True(extra.Length == 0, $"{route}: 供給されているがキー表に無いキー: {string.Join(", ", extra)}");
    }

    /// <summary>
    /// キー表の <c>All</c> に重複が無く、公開されている定数を漏れなく含むこと。
    ///
    /// All は手で並べた配列なので、定数を足して All に入れ忘れると
    /// 「そのキーだけ検査対象から外れる」— 検査の網に穴が空いたことに気づけない。
    /// </summary>
    [Theory]
    [MemberData(nameof(KeyTables))]
    public void TheKeyTableListsEveryConstantExactlyOnce(string table)
    {
        (Type type, IReadOnlyList<string> all) = TableFor(table);
        string[] constants = [.. type
            .GetFields(System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static)
            .Where(f => f is { IsLiteral: true, IsInitOnly: false } && f.FieldType == typeof(string))
            .Select(f => (string)f.GetRawConstantValue()!)];

        Assert.Equal(constants.Length, constants.Distinct(StringComparer.Ordinal).Count());
        Assert.Equal(all.Count, all.Distinct(StringComparer.Ordinal).Count());
        Assert.Equal(
            [.. constants.Order(StringComparer.Ordinal)],
            [.. all.Order(StringComparer.Ordinal)]);
    }

    /// <summary>
    /// 2 つのキー表が同じキーを名乗っていないこと。
    ///
    /// 供給経路は 1 つの辞書なので、重なると「どちらの窓のものか」が決まらず、
    /// 一方の文言を直したときに他方が黙って変わる。
    /// </summary>
    [Fact]
    public void TheTwoKeyTablesDoNotOverlap()
    {
        string[] shared = [.. PickerStringKeys.All
            .Intersect(EditorStringKeys.All, StringComparer.Ordinal)
            .Order(StringComparer.Ordinal)];

        Assert.True(shared.Length == 0, $"2 つのキー表が共有しているキー: {string.Join(", ", shared)}");
    }

    /// <summary>
    /// 同じキーの<b>値</b>も経路をまたいで一致すること (言語ごとに)。
    ///
    /// キー集合だけを見ていると、WinUI 側の文言を直したときに WPF / WinForms 側が
    /// 古い文言のまま残る。ユーザーから見れば「同じ画面なのに版ごとに文言が違う」であり、
    /// どのテストも落ちないまま起きる。値の出所を 1 つに保つのが resx を共有した理由でもある。
    /// </summary>
    [Theory]
    [InlineData("en-US", $"{ReswProject}/Strings/en-us/Resources.resw", $"{ResxProject}/Resources/Strings.resx")]
    [InlineData("ja", $"{ReswProject}/Strings/ja-jp/Resources.resw", $"{ResxProject}/Resources/Strings.ja.resx")]
    public void TheTwoSupplyRoutesCarryTheSameWording(string language, string reswPath, string resxPath)
    {
        Dictionary<string, string> resw = Read(reswPath);
        Dictionary<string, string> resx = Read(resxPath);

        string[] different = [.. resw
            .Where(kv => resx.TryGetValue(kv.Key, out string? other) && !string.Equals(kv.Value, other, StringComparison.Ordinal))
            .Select(kv => kv.Key)
            .Order(StringComparer.Ordinal)];

        Assert.True(
            different.Length == 0,
            $"{language}: .resw と .resx で文言が食い違うキー: {string.Join(", ", different)}");
    }

    /// <summary>
    /// <see cref="ResxPickerStrings"/> が実際に全キーを解決できること (en と ja の両方で)。
    ///
    /// <para>
    /// これは配線の検査である。<c>ResourceManager</c> のベース名
    /// (<c>UiaTrigger.Picker.Resources.Strings</c>) はアセンブリの既定名前空間 +
    /// フォルダ名から決まるので、<b>ファイルを移動するだけで黙って引けなくなる</b>。
    /// WinUI 側のリソースマップ名の罠 (<see cref="XamlLocalizationTests"/>) と同じ壊れ方で、
    /// 例外は出ず画面がキー名に化けるだけである。
    /// </para>
    /// <para>
    /// ソース検査では届かない — ここだけは実際に引いてみる必要がある。
    /// </para>
    /// </summary>
    [Theory]
    [InlineData("en-US")]
    [InlineData("ja-JP")]
    public void TheResxRouteResolvesEveryKeyAtRunTime(string culture)
    {
        CultureInfo original = CultureInfo.CurrentUICulture;
        try
        {
            CultureInfo.CurrentUICulture = new CultureInfo(culture);
            var strings = new ResxPickerStrings();

            // GetString は引けなければキー名をそのまま返す (無言で空にしない) 契約なので、
            // 「キー名が返ってきた = 引けていない」で判定できる
            string[] unresolved = [.. AllKeys
                .Where(key => string.Equals(strings.GetString(key), key, StringComparison.Ordinal))
                .Order(StringComparer.Ordinal)];

            Assert.True(
                unresolved.Length == 0,
                $"{culture}: ResxPickerStrings が解決できないキー: {string.Join(", ", unresolved)}。" +
                "ResourceManager のベース名か .resx の埋め込みが外れています。");
        }
        finally
        {
            CultureInfo.CurrentUICulture = original;
        }
    }

    /// <summary>
    /// ja のサテライトが実際に日本語を返すこと (neutral へ落ちていないこと)。
    ///
    /// サテライトが出力に含まれないと、例外にはならず<b>英語のまま表示される</b>。
    /// 上のテストは「引けること」しか見ないので素通りする。
    /// </summary>
    [Fact]
    public void TheJapaneseSatelliteActuallyLoads()
    {
        CultureInfo original = CultureInfo.CurrentUICulture;
        try
        {
            var strings = new ResxPickerStrings();
            CultureInfo.CurrentUICulture = new CultureInfo("en-US");
            string english = strings.GetString(PickerStringKeys.CommitButtonContent);
            CultureInfo.CurrentUICulture = new CultureInfo("ja-JP");
            string japanese = strings.GetString(PickerStringKeys.CommitButtonContent);

            Assert.NotEqual(english, japanese);
            Assert.Equal("トリガーを追加", japanese);
        }
        finally
        {
            CultureInfo.CurrentUICulture = original;
        }
    }
}
