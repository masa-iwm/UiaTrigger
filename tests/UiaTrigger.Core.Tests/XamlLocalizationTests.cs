using System.Text.RegularExpressions;
using System.Xml.Linq;
using Xunit;

namespace UiaTrigger.Tests;

/// <summary>
/// ピッカー / ホストの UI 文字列に関する回帰テスト (docs/DESIGN.md L3 / docs/LOCALIZATION.md §4)。
///
/// <para>
/// WinUI3 / x64 のプロジェクトはテストから参照できないので、
/// **ソースとリソースを構造として検査する**。
/// プロジェクトごとの違い (リソースの形式・<c>x:Uid</c> を使うか・
/// XAML があるか) は表にして、テーマのほうを形式非依存にしてある。
/// </para>
/// <para>
/// ここで塞げるのは**腐る側** — 新しい文字列をリソースに通し忘れる / 片方の言語だけ足す /
/// XAML やソースに直書きする — であり、実際に起きるのはそちらである。
/// 「実行すると日本語で出るか」は実物でしか確かめられない — そちらは発行レイアウトの T4
/// (<c>PublishedResourceTests</c> / <c>HostPublishedResourceTests</c>) が見ており、
/// 訳文長のレイアウト破綻だけが docs/MANUAL-CHECKS.md §4 に残る。
/// </para>
/// </summary>
public sealed class XamlLocalizationTests
{
    /// <summary>リソースの供給形式。ファイルの置き方だけが違い、XML 構造は同じである。</summary>
    public enum ResourceLayout
    {
        /// <summary>自分のリソースを持たない (共有のものを使う)。</summary>
        None,

        /// <summary><c>Strings/&lt;culture&gt;/Resources.resw</c> — MRT Core (WinUI)。</summary>
        Resw,

        /// <summary><c>Resources/Strings[.ja].resx</c> — ResourceManager + サテライト。</summary>
        Resx,
    }

    /// <summary>
    /// ローカライズの対象になるプロジェクト 1 つぶん。
    /// </summary>
    /// <param name="Path">リポジトリルートからの相対パス。</param>
    /// <param name="Layout">リソースの供給形式。</param>
    /// <param name="UsesUid"><c>x:Uid</c> で XAML から解決するか。</param>
    /// <param name="HasXaml">XAML を持つか。</param>
    /// <param name="ScanSource">
    /// 表示文字列の直書きを <c>.cs</c> 側で見るか。**ラベルをコードで代入する変種はすべて対象**で、
    /// XAML の有無とは別の話である — WPF は XAML を持つが <c>x:Uid</c> が無いのでラベルは
    /// コードで入れており、Windows Forms とまったく同じ隙がある。
    /// </param>
    public sealed record LocalizedProject(
        string Path,
        ResourceLayout Layout,
        bool UsesUid,
        bool HasXaml,
        bool ScanSource);

    private static readonly LocalizedProject[] Projects =
    [
        // ピッカー: WinUI だけが自分のリソースを持つ (.resw + MRT)。
        // WPF / WinForms は Picker.Core の .resx を共有するので、リソースはそちらの行で見る
        new("src/UiaTrigger.Picker.WinUI", ResourceLayout.Resw, UsesUid: true, HasXaml: true, ScanSource: false),
        new("src/UiaTrigger.Picker.Core", ResourceLayout.Resx, UsesUid: false, HasXaml: false, ScanSource: false),
        new("src/UiaTrigger.Picker.Wpf", ResourceLayout.None, UsesUid: false, HasXaml: true, ScanSource: true),
        new("src/UiaTrigger.Picker.WinForms", ResourceLayout.None, UsesUid: false, HasXaml: false, ScanSource: true),

        // ホスト: 3 つとも自分のリソースを持つ。同じサンプルの同じ画面なので中身も
        // 一致していなければならない — それは HostStringTests が見る
        new("src/UiaTrigger.App.WinUI", ResourceLayout.Resw, UsesUid: true, HasXaml: true, ScanSource: false),
        new("src/UiaTrigger.App.Wpf", ResourceLayout.Resx, UsesUid: false, HasXaml: true, ScanSource: true),
        new("src/UiaTrigger.App.WinForms", ResourceLayout.Resx, UsesUid: false, HasXaml: false, ScanSource: true),

        // 検証用コンソール。コントロールが無いので ScanSource の代入検査は掛かりようがなく、
        // 直書きは LocalizationTests.NoSourceLiteralCarriesJapanese のほうが見る。
        // ここに居る意味は「en/ja のキー集合・書式指定子・プライマリが英語であること」である
        new("src/UiaTrigger.TestHost", ResourceLayout.Resx, UsesUid: false, HasXaml: false, ScanSource: false),
    ];

    /// <summary>
    /// この表に載せないプロジェクトと、その理由。
    /// </summary>
    /// <remarks>
    /// **理由付きで載せないことと、忘れて載っていないことは区別できなければならない。**
    /// 前者は判断であり、後者は穴である — 見た目はどちらも「表に無い」なので、
    /// <see cref="EveryProjectIsEitherLocalizedOrExplicitlyExcluded"/> がここを突き合わせる。
    /// </remarks>
    private static readonly Dictionary<string, string> NotLocalized = new(StringComparer.Ordinal)
    {
        ["src/UiaTrigger.Core"] =
            "ライブラリ本体のリソースは LocalizationTests が en/ja/zh の 3 面で見ている " +
            "(この表より厳しい)。",
        ["src/UiaTrigger.App.Shared"] =
            "ユーザー向け文字列を持たない。ここが書くのは分類 3 (診断ログ) と " +
            "プログラマ契約の例外だけで、どちらも英語固定である — 表に足しても " +
            "resx が無く ScanSource の代入も無いので、検査ゼロの行が 1 つ増えるだけになる。",
    };

    /// <summary>
    /// <c>src/</c> のプロジェクトが、検査対象の表か除外表のどちらかに必ず載っていること。
    /// </summary>
    /// <remarks>
    /// docs/TESTING.md §2 が名指しする「表に載っていないから緑」を塞ぐ。実際に
    /// TestHost がこの形で抜けており、ユーザー向け出力が全部日本語直書きのまま
    /// どのゲートにも掛からなかった。**プロジェクトを足す側にこの判断を強制する。**
    /// </remarks>
    [Fact]
    public void EveryProjectIsEitherLocalizedOrExplicitlyExcluded()
    {
        string[] projects = [.. Directory.GetDirectories(RepoPaths.Combine("src"))
            .Where(d => Directory.GetFiles(d, "*.csproj").Length > 0)
            .Select(d => $"src/{Path.GetFileName(d)}")
            .Order(StringComparer.Ordinal)];

        // **0 件で緑にしない。**src の場所が変われば、この検査は何も見ないまま通る
        Assert.True(projects.Length >= 8, $"見つかったプロジェクトが {projects.Length} 件しかありません。");

        HashSet<string> known = [.. Projects.Select(p => p.Path), .. NotLocalized.Keys];

        string[] unaccounted = [.. projects.Where(p => !known.Contains(p))];
        Assert.True(
            unaccounted.Length == 0,
            $"どちらの表にも載っていないプロジェクトがあります: {string.Join(", ", unaccounted)}。" +
            "ローカライズの対象なら Projects へ、対象外なら NotLocalized へ理由付きで足してください。");

        // 逆向き: 消えたプロジェクトの行が表に残り続けないこと (残ると検査 0 件で緑になる)
        string[] stale = [.. known.Where(k => !projects.Contains(k, StringComparer.Ordinal))];
        Assert.True(stale.Length == 0, $"実在しないプロジェクトが表に残っています: {string.Join(", ", stale)}");
    }

    public static TheoryData<string> WithResources =>
        [.. Projects.Where(p => p.Layout != ResourceLayout.None).Select(p => p.Path)];

    public static TheoryData<string> WithXaml =>
        [.. Projects.Where(p => p.HasXaml).Select(p => p.Path)];

    public static TheoryData<string> WithUid =>
        [.. Projects.Where(p => p.UsesUid).Select(p => p.Path)];

    public static TheoryData<string> WithScannedSource =>
        [.. Projects.Where(p => p.ScanSource).Select(p => p.Path)];

    private static LocalizedProject Lookup(string path)
        => Projects.Single(p => string.Equals(p.Path, path, StringComparison.Ordinal));

    /// <summary>
    /// XAML で文字列を取る属性。ここに書かれたリテラルはそのまま画面に出る。
    /// </summary>
    private static readonly string[] TextAttributes =
        ["Text", "Content", "Header", "OnContent", "OffContent", "PlaceholderText", "Title", "Description"];

    private static readonly Regex Cjk = new(
        @"[\p{IsHiragana}\p{IsKatakana}\p{IsCJKUnifiedIdeographs}]",
        RegexOptions.CultureInvariant | RegexOptions.NonBacktracking,
        TimeSpan.FromSeconds(1));

    /// <summary>プライマリ (en-US) と ja のリソースファイル。形式の違いはここで吸収する。</summary>
    private static (string Primary, string Japanese) ResourcePaths(string project)
    {
        string[] parts = project.Split('/');
        return Lookup(project).Layout switch
        {
            ResourceLayout.Resw => (
                RepoPaths.Combine([.. parts, "Strings", "en-us", "Resources.resw"]),
                RepoPaths.Combine([.. parts, "Strings", "ja-jp", "Resources.resw"])),
            ResourceLayout.Resx => (
                RepoPaths.Combine([.. parts, "Resources", "Strings.resx"]),
                RepoPaths.Combine([.. parts, "Resources", "Strings.ja.resx"])),
            _ => throw new InvalidOperationException($"{project} は自分のリソースを持ちません。"),
        };
    }

    private static Dictionary<string, string> Read(string path)
    {
        Assert.True(File.Exists(path), $"リソースファイルが見つかりません: {path}");
        // .resw と .resx は同じ XML 構造なので、読み手は 1 つで足りる
        return XDocument.Load(path).Root!
            .Elements("data")
            .ToDictionary(e => (string)e.Attribute("name")!, e => (string)e.Element("value")!, StringComparer.Ordinal);
    }

    private static IEnumerable<string> SourceFiles(string project, string pattern)
        => Directory.EnumerateFiles(RepoPaths.Combine(project.Split('/')), pattern, SearchOption.AllDirectories)
            .Where(p => !p.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.Ordinal)
                     && !p.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}", StringComparison.Ordinal));

    [Theory]
    [MemberData(nameof(WithResources))]
    public void EnglishAndJapaneseResourcesHaveTheSameKeys(string project)
    {
        (string primaryPath, string japanesePath) = ResourcePaths(project);
        Dictionary<string, string> english = Read(primaryPath);
        Dictionary<string, string> japanese = Read(japanesePath);

        Assert.NotEmpty(english);

        string[] missing = [.. english.Keys.Except(japanese.Keys, StringComparer.Ordinal).Order(StringComparer.Ordinal)];
        string[] extra = [.. japanese.Keys.Except(english.Keys, StringComparer.Ordinal).Order(StringComparer.Ordinal)];

        Assert.True(missing.Length == 0, $"{project}: ja に無いキー: {string.Join(", ", missing)}");
        Assert.True(extra.Length == 0, $"{project}: ja にのみ在るキー: {string.Join(", ", extra)}");
    }

    /// <summary>
    /// 書式指定子 ({0} 等) の集合が言語間で一致すること。
    ///
    /// 番号の取り違えは**実行するまで分からず、しかも例外にならない**
    /// (足りなければ FormatException だが、余分・順序違いは黙って通る)。
    /// 語順の都合で ja だけ順番が変わるのは正しいので、集合として見る。
    /// </summary>
    [Theory]
    [MemberData(nameof(WithResources))]
    public void EveryTranslationUsesTheSamePlaceholders(string project)
    {
        (string primaryPath, string japanesePath) = ResourcePaths(project);
        Dictionary<string, string> english = Read(primaryPath);
        Dictionary<string, string> japanese = Read(japanesePath);
        var placeholder = new Regex(@"\{(\d+)[^}]*\}", RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1));

        var mismatched = new List<string>();
        foreach ((string key, string en) in english)
        {
            if (!japanese.TryGetValue(key, out string? ja))
            {
                continue; // 上のテストが報告する
            }
            var inEnglish = placeholder.Matches(en).Select(m => m.Groups[1].Value).ToHashSet(StringComparer.Ordinal);
            var inJapanese = placeholder.Matches(ja).Select(m => m.Groups[1].Value).ToHashSet(StringComparer.Ordinal);
            if (!inEnglish.SetEquals(inJapanese))
            {
                mismatched.Add($"{key} (en: {{{string.Join(",", inEnglish.Order(StringComparer.Ordinal))}}} / ja: {{{string.Join(",", inJapanese.Order(StringComparer.Ordinal))}}})");
            }
        }

        Assert.True(
            mismatched.Count == 0,
            $"{project}: 書式指定子が食い違うキー: {string.Join(", ", mismatched)}");
    }

    /// <summary>
    /// XAML に表示文字列のリテラルが残っていないこと。
    ///
    /// 直書きは実際に起きた壊れ方であり、次に足す誰かが同じことをする道でもある。
    /// x:Bind / Binding / ThemeResource / DynamicResource / 空文字は文字列リテラルではないので除く。
    /// </summary>
    [Theory]
    [MemberData(nameof(WithXaml))]
    public void NoXamlAttributeCarriesAUserFacingLiteral(string project)
    {
        var offenders = new List<string>();
        foreach (string path in SourceFiles(project, "*.xaml"))
        {
            foreach (XElement element in XDocument.Load(path).Descendants())
            {
                foreach (XAttribute attribute in element.Attributes())
                {
                    if (!TextAttributes.Contains(attribute.Name.LocalName, StringComparer.Ordinal))
                    {
                        continue;
                    }
                    string value = attribute.Value;
                    if (value.Length == 0 || value.StartsWith('{'))
                    {
                        continue; // マークアップ拡張 (x:Bind / Binding / ThemeResource / DynamicResource)
                    }
                    offenders.Add($"{Path.GetFileName(path)}: {element.Name.LocalName}.{attribute.Name.LocalName}=\"{value}\"");
                }
            }
        }

        Assert.True(
            offenders.Count == 0,
            $"XAML に直書きの表示文字列があります。リソースへ移してください:{Environment.NewLine}  {string.Join($"{Environment.NewLine}  ", offenders)}");
    }

    /// <summary>
    /// ラベルをコードで代入する変種で、表示文字列がソースに直書きされていないこと。
    ///
    /// <para>
    /// Windows Forms はコントロールをコードで組むので、XAML を見る検査が一切効かない。
    /// WPF も <c>x:Uid</c> の実行時解決を持たずラベルをコードで入れるため、同じ隙がある —
    /// XAML があるかどうかではなく**ラベルをどこで入れるか**で対象を決める。
    /// <c>Text = "…"</c> と書けばそのまま画面に出るし、ビルドも通る。
    /// 表示に関わるプロパティへの**文字列リテラルの代入**を探して塞ぐ。
    /// </para>
    /// <para>
    /// この検査があるので、Windows Forms の View はデザイナー (.Designer.cs) を使わない。
    /// デザイナーはまさにこの形のコードを生成するため、使うと検査のほうを外すしかなくなる。
    /// </para>
    /// </summary>
    [Theory]
    [MemberData(nameof(WithScannedSource))]
    public void NoSourceAssignsAUserFacingLiteral(string project)
    {
        // Text = "…" のような代入。空文字は対象外 (表示するものが無い)
        var assignment = new Regex(
            @"\b(Text|Content|Header|ToolTipText|Caption)\s*=\s*""(?<value>[^""]+)""",
            RegexOptions.CultureInvariant,
            TimeSpan.FromSeconds(5));

        var offenders = new List<string>();
        foreach (string path in SourceFiles(project, "*.cs"))
        {
            string[] lines = File.ReadAllLines(path);
            for (int i = 0; i < lines.Length; i++)
            {
                string line = lines[i];
                // コメントは対象外。この規則そのものを説明する文章が引っかかるため
                // (実際にこのテストの 1 回目でそうなった)
                string trimmed = line.TrimStart();
                if (trimmed.StartsWith("//", StringComparison.Ordinal) ||
                    trimmed.StartsWith('*') ||
                    trimmed.StartsWith("/*", StringComparison.Ordinal))
                {
                    continue;
                }
                Match match = assignment.Match(line);
                if (match.Success)
                {
                    offenders.Add($"{Path.GetFileName(path)}:{i + 1}: {match.Value.Trim()}");
                }
            }
        }

        Assert.True(
            offenders.Count == 0,
            $"{project}: 表示文字列がソースに直書きされています。" +
            $"PickerStringKeys / AppStrings 経由にしてください:{Environment.NewLine}  " +
            string.Join($"{Environment.NewLine}  ", offenders));
    }

    /// <summary>
    /// XAML の x:Uid が .resw のキーに実際に対応していること。
    ///
    /// x:Uid の綴り違いは**例外にならず、その要素の文字列が空のまま表示される**。
    /// 「動いているように見えて空欄」という最も気づきにくい壊れ方なので、ここで名前を突き合わせる。
    /// </summary>
    [Theory]
    [MemberData(nameof(WithUid))]
    public void EveryUidResolvesToAtLeastOneResourceKey(string project)
    {
        Dictionary<string, string> english = Read(ResourcePaths(project).Primary);
        XNamespace x = "http://schemas.microsoft.com/winfx/2006/xaml";

        var unresolved = new List<string>();
        var uids = new List<string>();
        foreach (string path in SourceFiles(project, "*.xaml"))
        {
            foreach (XElement element in XDocument.Load(path).Descendants())
            {
                if (element.Attribute(x + "Uid")?.Value is not { Length: > 0 } uid)
                {
                    continue;
                }
                uids.Add(uid);
                if (!english.Keys.Any(k => k.StartsWith(uid + ".", StringComparison.Ordinal)))
                {
                    unresolved.Add($"{Path.GetFileName(path)}: x:Uid=\"{uid}\"");
                }
            }
        }

        Assert.NotEmpty(uids);
        Assert.True(
            unresolved.Count == 0,
            $"対応するリソースキー ('<Uid>.<プロパティ>') が無い x:Uid:{Environment.NewLine}  {string.Join($"{Environment.NewLine}  ", unresolved)}");
    }

    /// <summary>
    /// ".<プロパティ>" 形式のキーには、それを使う x:Uid が実在すること。
    ///
    /// <see cref="EveryUidResolvesToAtLeastOneResourceKey"/> の逆向き。
    /// XAML から要素を消してもリソースは残るので、放っておくと
    /// 「翻訳されているが誰も使っていない文字列」が溜まり、キー集合の一致検査を
    /// 通したまま実際の画面は英語のまま、という状態を作れてしまう。
    /// </summary>
    [Theory]
    [MemberData(nameof(WithUid))]
    public void EveryUidStyleResourceKeyIsUsedByAUid(string project)
    {
        XNamespace x = "http://schemas.microsoft.com/winfx/2006/xaml";
        HashSet<string> uids = [.. SourceFiles(project, "*.xaml")
            .SelectMany(p => XDocument.Load(p).Descendants())
            .Select(e => e.Attribute(x + "Uid")?.Value)
            .Where(v => !string.IsNullOrEmpty(v))
            .Select(v => v!)];

        string[] orphans = [.. Read(ResourcePaths(project).Primary).Keys
            .Where(k => k.Contains('.', StringComparison.Ordinal))
            .Where(k => !uids.Contains(k[..k.IndexOf('.', StringComparison.Ordinal)]))
            .Order(StringComparer.Ordinal)];

        Assert.True(
            orphans.Length == 0,
            $"{project}: どの x:Uid からも参照されていないキー: {string.Join(", ", orphans)}");
    }

    /// <summary>
    /// プライマリ側に日本語が残っていないこと。
    ///
    /// プライマリは en-US である (docs/DESIGN.md §1)。ここが日本語のままだと、翻訳の無いカルチャで
    /// 日本語にフォールバックする — 「ローカライズしたのに英語圏で日本語が出る」形で壊れる。
    /// </summary>
    [Theory]
    [MemberData(nameof(WithResources))]
    public void ThePrimaryResourceIsEnglish(string project)
    {
        string[] japanese = [.. Read(ResourcePaths(project).Primary)
            .Where(kv => Cjk.IsMatch(kv.Value))
            .Select(kv => kv.Key)
            .Order(StringComparer.Ordinal)];

        Assert.True(japanese.Length == 0, $"{project}: プライマリに日本語が残っています: {string.Join(", ", japanese)}");
    }

    /// <summary>
    /// リソースマップ名が、それを書いているアセンブリの名前と一致すること。
    ///
    /// クラスライブラリの <c>.resw</c> はアプリの <c>resources.pri</c> の中で
    /// 「&lt;アセンブリ名&gt;/Resources」というサブツリーに入る。ここを取り違えても
    /// **ビルドは通り、例外も出ない** — 画面の文字列がキー名に化けるだけである。
    ///
    /// アセンブリのリネーム (<c>UiaTrigger.Picker</c> → <c>UiaTrigger.Picker.WinUI</c> など) が
    /// まさにこれを踏みうる変更である。<c>.resx</c> 側にも同じ罠 (ResourceManager のベース名) が
    /// あり、そちらは <c>PickerStringTests</c> が実際に引いて確かめている。
    /// </summary>
    [Theory]
    [InlineData("src/UiaTrigger.Picker.WinUI", "MrtPickerStrings.cs", "UiaTrigger.Picker.WinUI")]
    public void TheResourceMapNamesTheAssemblyItLivesIn(string project, string sourceFile, string assemblyName)
    {
        string path = RepoPaths.Combine([.. project.Split('/'), sourceFile]);
        Assert.True(File.Exists(path), $"リソースマップを書いているファイルがありません: {path}");

        Match match = Regex.Match(
            File.ReadAllText(path),
            @"ResourceMap\s*=\s*""(?<map>[^""]*)""",
            RegexOptions.CultureInvariant,
            TimeSpan.FromSeconds(1));

        Assert.True(match.Success, $"{sourceFile}: ResourceMap の定義が見つかりません。");
        Assert.Equal($"{assemblyName}/Resources", match.Groups["map"].Value);

        // アセンブリ名の側も固定する (csproj のリネームだけでも同じ壊れ方をするため)
        string csproj = RepoPaths.Combine([.. project.Split('/'), $"{assemblyName}.csproj"]);
        Assert.True(File.Exists(csproj), $"アセンブリ名と一致する csproj がありません: {csproj}");
    }

    /// <summary>
    /// ja 側が実際に翻訳されていること (プライマリのコピーではないこと)。
    ///
    /// 「Id」「ON」「OFF」のように訳す必要がないキーがあるので、
    /// **全件**ではなく「大半が違う」ことを見る。コピーしただけのファイルを弾ければ足りる。
    /// </summary>
    [Theory]
    [MemberData(nameof(WithResources))]
    public void TheJapaneseResourceIsTranslated(string project)
    {
        (string primaryPath, string japanesePath) = ResourcePaths(project);
        Dictionary<string, string> english = Read(primaryPath);
        Dictionary<string, string> japanese = Read(japanesePath);

        int translated = japanese.Count(kv =>
            english.TryGetValue(kv.Key, out string? en) && !string.Equals(en, kv.Value, StringComparison.Ordinal));

        Assert.True(
            translated * 2 > english.Count,
            $"{project}: 翻訳されているキーが {translated}/{english.Count} 件しかありません。プライマリをコピーしただけになっていませんか。");
    }
}
