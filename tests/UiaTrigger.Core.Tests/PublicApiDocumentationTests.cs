using System.Reflection;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;
using Xunit;

namespace UiaTrigger.Tests;

/// <summary>
/// 公開 API のドキュメントに関する回帰テスト (docs/DESIGN.md L1)。
///
/// 押さえるのは 2 つ:
///   1. 生成される XML ドキュメントの**公開 API 部分**が英語であること
///      (NuGet 配布すると全利用者の IntelliSense に出る)
///   2. 日本語版 IntelliSense ファイル (<c>ja/UiaTrigger.Core.xml</c>) が
///      公開 API を**過不足なく**覆っていること
///
/// 2 が「過不足なく」でなければならないのは、ロールアップの単位が**ファイル**だからである。
/// ja のファイルが存在すればそちらが丸ごと使われ、そこに無いメンバーは
/// **英語に落ちるのではなく、説明が消える**。resx のキー集合一致 (docs/LOCALIZATION.md §3) と
/// 同じ理由で、ここも差分をテストで塞ぐ。
///
/// 対象は NuGet で配るアセンブリすべてである (UiaTrigger.Picker.Core を含む)。
/// </summary>
public sealed class PublicApiDocumentationTests
{
    /// <summary>日本語 IntelliSense を用意するアセンブリ。</summary>
    private static readonly string[] DocumentedAssemblyNames =
    [
        "UiaTrigger.Core",
        "UiaTrigger.Picker.Core",
        "UiaTrigger.Picker.Wpf",
        "UiaTrigger.Picker.WinForms",
        WinUiAssemblyName,
    ];

    /// <summary>
    /// **このアセンブリだけは参照できない。**`Picker.WinUI` は slnx で `Platform=x64` に
    /// 固定されており、参照すると AnyCPU のこのプロジェクトごと x64 になって
    /// Windows App SDK を引き込む。T1 は「UIA にも GUI にも依らない」層なので、
    /// そこを崩さずに検査だけ同じにするため、**ビルド出力をメタデータとして読む**
    /// (<see cref="AssemblyFor"/>)。
    /// </summary>
    private const string WinUiAssemblyName = "UiaTrigger.Picker.WinUI";

    public static TheoryData<string> DocumentedAssemblies => [.. DocumentedAssemblyNames];

    /// <summary>
    /// 「項目が少なすぎる」の下限。ID の解釈や出力先が壊れると 0 件になるので、それを弾く。
    /// アセンブリごとに公開面の大きさが違うため、一律の数では意味を持たない
    /// (View は公開面がウィンドウ 1 つぶんしかない)。
    /// </summary>
    private static int MinimumEntries(string assembly) => assembly switch
    {
        "UiaTrigger.Core" => 100,
        "UiaTrigger.Picker.Core" => 20,
        _ => 3,
    };

    private static Assembly AssemblyFor(string name) => name switch
    {
        "UiaTrigger.Core" => typeof(UiaSession).Assembly,
        "UiaTrigger.Picker.Core" => typeof(UiaTrigger.Picker.TriggerPickerPresenter).Assembly,
        "UiaTrigger.Picker.Wpf" => typeof(UiaTrigger.Picker.Wpf.TriggerPickerWindow).Assembly,
        "UiaTrigger.Picker.WinForms" => typeof(UiaTrigger.Picker.WinForms.TriggerPickerForm).Assembly,
        WinUiAssemblyName => WinUiMetadata.Value,
        _ => throw new ArgumentOutOfRangeException(nameof(name)),
    };

    /// <summary>
    /// <see cref="WinUiAssemblyName"/> のビルド出力フォルダ。
    ///
    /// **「いちばん新しいものを採る」形にしない。**単体プロジェクトのビルドが作る
    /// <c>bin\Debug</c> をソリューションビルドの <c>bin\x64\...</c> と取り違える罠が
    /// このリポジトリには実在する (.claude/rules/build.md)。テスト自身と同じ構成の、
    /// ソリューションビルドが出す場所だけを見て、無ければ場所を名指しして落とす。
    /// </summary>
    private static readonly Lazy<string> WinUiOutputDirectory = new(() =>
    {
        string configuration = new DirectoryInfo(AppContext.BaseDirectory).Parent!.Name;
        string root = RepoPaths.Combine("src", WinUiAssemblyName, "bin", "x64", configuration);

        string[] found = Directory.Exists(root)
            ? Directory.GetFiles(root, $"{WinUiAssemblyName}.dll", SearchOption.AllDirectories)
            : [];

        if (found.Length != 1)
        {
            throw new InvalidOperationException(
                $"{WinUiAssemblyName}.dll が {root} の下に {found.Length} 個ありました (1 個であるべきです)。" +
                "ソリューションビルド (dotnet build UiaTrigger.slnx) を先に通してください。");
        }
        return Path.GetDirectoryName(found[0])!;
    });

    /// <summary>
    /// Windows App SDK のアセンブリが並ぶフォルダ。
    ///
    /// <c>App.WinUI</c> は <c>WindowsAppSDKSelfContained</c> なので、その出力にだけ
    /// <c>Microsoft.WinUI.dll</c> / <c>WinRT.Runtime.dll</c> が揃う。
    /// <see cref="WinUiAssemblyName"/> 自身の <c>bin</c> には並ばない (実測で 5 ファイルのみ)。
    /// </summary>
    private static readonly Lazy<string> WindowsAppSdkDirectory = new(() =>
    {
        string configuration = new DirectoryInfo(AppContext.BaseDirectory).Parent!.Name;
        string root = RepoPaths.Combine("src", "UiaTrigger.App.WinUI", "bin", "x64", configuration);

        string[] found = Directory.Exists(root)
            ? Directory.GetFiles(root, "Microsoft.WinUI.dll", SearchOption.AllDirectories)
            : [];

        if (found.Length != 1)
        {
            throw new InvalidOperationException(
                $"Microsoft.WinUI.dll が {root} の下に {found.Length} 個ありました (1 個であるべきです)。" +
                "ソリューションビルド (dotnet build UiaTrigger.slnx) を先に通してください。");
        }
        return Path.GetDirectoryName(found[0])!;
    });

    /// <summary>
    /// <see cref="WinUiAssemblyName"/> のビルド出力を <c>MetadataLoadContext</c> で読む。
    /// コードは動かさないので x64 でも Windows App SDK でも構わない — 見るのは可視性と
    /// 属性だけである。<see cref="PublicApiDoc"/> 側は <c>typeof</c> との比較を使わない形に
    /// してあるので、通常の <see cref="Assembly"/> と同じに扱える。
    /// </summary>
    private static readonly Lazy<Assembly> WinUiMetadata = new(() =>
    {
        string directory = WinUiOutputDirectory.Value;

        // 同じ単純名が複数の場所に在ると解決が曖昧になる。**発行元のフォルダを優先する** —
        // ここで見たいのは「あのビルド出力の中身」であって、テストが抱えている写しではない。
        //
        // Windows App SDK 本体 (Microsoft.WinUI / WinRT.Runtime) は **Picker.WinUI の bin には
        // 並ばない** (あそこに出るのは 5 つだけ — 実測)。属性の型を解決するのに要るので、
        // ソリューションビルドがそれらを置く唯一の場所である App.WinUI の出力から補う。
        Dictionary<string, string> byName = new(StringComparer.OrdinalIgnoreCase);
        foreach (string dll in Directory.GetFiles(directory, "*.dll")
            .Concat(Directory.GetFiles(WindowsAppSdkDirectory.Value, "*.dll"))
            .Concat(Directory.GetFiles(AppContext.BaseDirectory, "*.dll"))
            .Concat(Directory.GetFiles(RuntimeEnvironment.GetRuntimeDirectory(), "*.dll")))
        {
            byName.TryAdd(Path.GetFileName(dll), dll);
        }

        MetadataLoadContext context = new(new PathAssemblyResolver(byName.Values));
        return context.LoadFromAssemblyPath(Path.Combine(directory, $"{WinUiAssemblyName}.dll"));
    });

    /// <summary>
    /// XML ドキュメントを出すアセンブリが、**1 つ残らず**上の表に載っていること。
    ///
    /// <para>
    /// <see cref="DocumentedAssemblies"/> は手で並べた配列なので、
    /// <c>GenerateDocumentationFile</c> を有効にしたプロジェクトを足して表への追加を忘れると、
    /// このクラスの検査は全部「対象外」として素通りする。英語の doc も日本語版の有無も
    /// 誰も見ないまま、NuGet で配られる .xml が 1 つ増えることになる。
    /// </para>
    /// <para>
    /// <c>DpiManifestTests.EveryHostDeclaresPerMonitorV2SomeWay</c> と同じ形の検査である —
    /// 「どの表にも載っていないから緑」を塞ぐためだけに在る。
    /// </para>
    /// </summary>
    [Fact]
    public void EveryAssemblyThatShipsXmlDocumentationIsCovered()
    {
        string[] generating = [.. Directory
            .EnumerateFiles(Path.Combine(RepoPaths.Root.FullName, "src"), "*.csproj", SearchOption.AllDirectories)
            .Where(p => File.ReadAllText(p).Contains("<GenerateDocumentationFile>true", StringComparison.Ordinal))
            .Select(Path.GetFileNameWithoutExtension)
            .Select(n => n!)
            .Order(StringComparer.Ordinal)];

        string[] missing = [.. generating.Except(DocumentedAssemblyNames, StringComparer.Ordinal)];

        Assert.NotEmpty(generating);
        Assert.True(
            missing.Length == 0,
            $"XML ドキュメントを出すのに日本語版の検査対象になっていないアセンブリ: {string.Join(", ", missing)}。" +
            "DocumentedAssemblies に足すか、GenerateDocumentationFile を外してください。");
    }

    private static readonly Regex Cjk = new(
        @"[\p{IsHiragana}\p{IsKatakana}\p{IsCJKUnifiedIdeographs}]",
        RegexOptions.CultureInvariant | RegexOptions.NonBacktracking,
        TimeSpan.FromSeconds(1));

    /// <summary>
    /// XML ドキュメントを探すフォルダ。参照している 4 つはテストの出力に並ぶが、
    /// <see cref="WinUiAssemblyName"/> は参照できないのでそのプロジェクトの出力を見る。
    /// </summary>
    private static string OutputDirectoryFor(string assembly)
        => assembly == WinUiAssemblyName ? WinUiOutputDirectory.Value : AppContext.BaseDirectory;

    /// <summary>ビルド出力に並ぶ英語 (neutral) の XML ドキュメント。</summary>
    private static string NeutralXmlPath(string assembly)
        => Path.Combine(OutputDirectoryFor(assembly), $"{assembly}.xml");

    /// <summary>同じ出力の <c>ja/</c> サブフォルダ。IDE はここを先に見る。</summary>
    private static string JapaneseXmlPath(string assembly)
        => Path.Combine(OutputDirectoryFor(assembly), "ja", $"{assembly}.xml");

    /// <summary>
    /// XML ドキュメントがそもそも生成・配置されていること。
    ///
    /// <c>GenerateDocumentationFile</c> が外れると以降の検査が「対象ゼロ」で
    /// 全部通ってしまうため、最初に土台を確かめる。
    /// </summary>
    [Theory]
    [MemberData(nameof(DocumentedAssemblies))]
    public void TheDocumentationFilesAreBuiltAndCopied(string assembly)
    {
        Assert.True(File.Exists(NeutralXmlPath(assembly)), $"英語の XML ドキュメントがありません: {NeutralXmlPath(assembly)}");
        Assert.True(File.Exists(JapaneseXmlPath(assembly)), $"日本語の XML ドキュメントがありません: {JapaneseXmlPath(assembly)}");

        // 公開 API を 1 件も拾えていないなら、ID の組み立てか出力先が壊れている
        Assert.True(
            PublicApiDoc.ReadPublicEntries(NeutralXmlPath(assembly), AssemblyFor(assembly)).Count >= MinimumEntries(assembly),
            $"{assembly}: 公開 API のドキュメント項目が少なすぎます。ID の解釈が壊れている可能性があります。");
    }

    /// <summary>
    /// L1: 公開 API の XML doc に日本語が残っていないこと。
    ///
    /// 実装内部のコメントは日本語のままでよい (docs/LOCALIZATION.md §1 の L1) ので、
    /// **公開 API のぶんだけ**を見る。同じファイルに両方が入るため、
    /// ファイル全体を見る検査にすると内部コメントで必ず落ちて成立しない。
    /// </summary>
    [Theory]
    [MemberData(nameof(DocumentedAssemblies))]
    public void ThePublicApiDocumentationIsEnglish(string assembly)
    {
        string[] japanese = [.. PublicApiDoc.ReadPublicEntries(NeutralXmlPath(assembly), AssemblyFor(assembly))
            .Where(e => Cjk.IsMatch(e.Text))
            .Select(e => e.Id)];

        Assert.True(
            japanese.Length == 0,
            $"公開 API の XML doc に日本語が残っています:{Environment.NewLine}  {string.Join($"{Environment.NewLine}  ", japanese)}");
    }

    /// <summary>
    /// 配布する XML doc に非公開メンバーの項目が残っていないこと (docs/LOCALIZATION.md §5)。
    ///
    /// <para>
    /// **csc は可視性で絞らない。**<c>///</c> を書いた private / internal のメンバーも
    /// そのまま <c>.xml</c> に入るので、docs/LOCALIZATION.md §1 (L1) で「実装内部のコメントは日本語のままでよい」と
    /// 決めた以上、放っておくと日本語の内部コメントが NuGet パッケージと Release の zip に
    /// 同梱される。
    /// </para>
    /// <para>
    /// <see cref="ThePublicApiDocumentationIsEnglish"/> は**公開面だけ**を見るので、
    /// この検査が無いと誰も見ない
    /// (実測で <c>UiaTrigger.Core</c> は 618 項目のうち 301 が非公開だった)。
    /// 絞り込みは <c>Directory.Build.targets</c> の
    /// <c>KeepDistributedDocumentationPublic</c> が行う。
    /// </para>
    /// </summary>
    [Theory]
    [MemberData(nameof(DocumentedAssemblies))]
    public void TheDistributedDocumentationContainsNoNonPublicMembers(string assembly)
    {
        string[] all = [.. PublicApiDoc.ReadAllEntries(NeutralXmlPath(assembly)).Select(e => e.Id)];
        string[] publicApi = [.. PublicApiDoc.ReadPublicEntries(NeutralXmlPath(assembly), AssemblyFor(assembly)).Select(e => e.Id)];

        string[] nonPublic = [.. all.Except(publicApi, StringComparer.Ordinal).Order(StringComparer.Ordinal)];

        Assert.True(
            nonPublic.Length == 0,
            $"配布する {assembly}.xml に非公開メンバーの項目が {nonPublic.Length} 件残っています。" +
            $"Directory.Build.targets の KeepDistributedDocumentationPublic が効いていません (先頭 20 件):" +
            $"{Environment.NewLine}  {string.Join($"{Environment.NewLine}  ", nonPublic.Take(20))}");
    }

    /// <summary>
    /// 日本語版が公開 API を過不足なく覆っていること。
    /// </summary>
    [Theory]
    [MemberData(nameof(DocumentedAssemblies))]
    public void TheJapaneseDocumentationCoversExactlyThePublicApi(string assembly)
    {
        string[] english = [.. PublicApiDoc.ReadPublicEntries(NeutralXmlPath(assembly), AssemblyFor(assembly)).Select(e => e.Id)];
        string[] japanese = [.. PublicApiDoc.ReadAllEntries(JapaneseXmlPath(assembly)).Select(e => e.Id).Order(StringComparer.Ordinal)];

        string[] missing = [.. english.Except(japanese, StringComparer.Ordinal).Order(StringComparer.Ordinal)];
        string[] extra = [.. japanese.Except(english, StringComparer.Ordinal).Order(StringComparer.Ordinal)];

        Assert.True(
            missing.Length == 0,
            $"ja の XML doc に無い公開 API ({missing.Length} 件) — IDE では説明が消えます:{Environment.NewLine}  {string.Join($"{Environment.NewLine}  ", missing)}");
        Assert.True(
            extra.Length == 0,
            $"ja の XML doc にのみ存在する項目 ({extra.Length} 件) — 公開 API から消えた残骸です:{Environment.NewLine}  {string.Join($"{Environment.NewLine}  ", extra)}");
    }

    /// <summary>
    /// 日本語版が実際に翻訳されていること。
    ///
    /// これが無いと「英語をそのままコピーした ja ファイル」でもキー集合一致の検査は通り、
    /// **翻訳したつもりで英語が出る**状態に静かに戻れる。
    /// </summary>
    [Theory]
    [MemberData(nameof(DocumentedAssemblies))]
    public void TheJapaneseDocumentationIsActuallyTranslated(string assembly)
    {
        Dictionary<string, string> english = PublicApiDoc.ReadPublicEntries(NeutralXmlPath(assembly), AssemblyFor(assembly))
            .ToDictionary(e => e.Id, e => e.Text, StringComparer.Ordinal);

        string[] untranslated = [.. PublicApiDoc.ReadAllEntries(JapaneseXmlPath(assembly))
            .Where(e => english.TryGetValue(e.Id, out string? en) && string.Equals(en, e.Text, StringComparison.Ordinal))
            .Select(e => e.Id)
            .Order(StringComparer.Ordinal)];

        Assert.True(
            untranslated.Length == 0,
            $"ja の XML doc が英語と同一のままです ({untranslated.Length} 件):{Environment.NewLine}  {string.Join($"{Environment.NewLine}  ", untranslated)}");
    }

    /// <summary>
    /// 日本語版が日本語で書かれていること。
    ///
    /// <see cref="TheJapaneseDocumentationIsActuallyTranslated"/> は「英語と違うこと」しか見ない —
    /// 空要素や体裁だけの差し替えでも通ってしまう。ここで中身を見る。
    /// </summary>
    [Theory]
    [MemberData(nameof(DocumentedAssemblies))]
    public void EveryJapaneseSummaryContainsJapanese(string assembly)
    {
        string[] notJapanese = [.. PublicApiDoc.ReadAllEntries(JapaneseXmlPath(assembly))
            .Where(e => !Cjk.IsMatch(e.Text))
            .Select(e => e.Id)
            .Order(StringComparer.Ordinal)];

        Assert.True(
            notJapanese.Length == 0,
            $"ja の XML doc に日本語が 1 文字も無い項目があります:{Environment.NewLine}  {string.Join($"{Environment.NewLine}  ", notJapanese)}");
    }
}
