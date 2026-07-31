using System.Reflection;
using System.Xml.Linq;

namespace UiaTrigger.Tests;

/// <summary>
/// 生成された XML ドキュメントファイルを「公開 API のぶんだけ」に絞って読むヘルパー
/// (docs/DESIGN.md L1)。
///
/// csc が出す .xml には **internal / private のメンバーも入る** — 日本語で書いてよいと
/// 決めた実装コメント (docs/LOCALIZATION.md §1 の L1) がそのまま同じファイルに並ぶ。ここで公開 API だけを
/// 取り出さないと「英語であること」も「翻訳が揃っていること」も検査できない。
///
/// 判定はドキュメント ID から型名・メンバー名を取り出してリフレクションに問い合わせる方式。
/// 署名までは見ず**名前だけ**で照合する: 欲しいのは可視性であり、同名の多重定義で
/// 可視性が割れることは実際上ない。割れていれば internal 側にも翻訳を要求する形になり、
/// 見落としではなく過剰検出として現れる。
/// </summary>
internal static class PublicApiDoc
{
    /// <summary>1 件のドキュメント項目。<paramref name="Text"/> は要素の中身をそのまま持つ。</summary>
    public sealed record Entry(string Id, string Text);

    /// <summary>XML ドキュメントファイルを読み、公開 API のメンバーだけを ID 順で返す。</summary>
    public static IReadOnlyList<Entry> ReadPublicEntries(string xmlPath, Assembly assembly)
    {
        Dictionary<string, Type> exported = assembly.GetExportedTypes()
            .ToDictionary(t => t.FullName!.Replace('+', '.'), t => t, StringComparer.Ordinal);

        return [.. ReadAllEntries(xmlPath)
            .Where(e => IsPublicApi(e.Id, exported))
            .OrderBy(e => e.Id, StringComparer.Ordinal)];
    }

    /// <summary>XML ドキュメントファイルの全項目 (可視性を問わない)。</summary>
    public static IReadOnlyList<Entry> ReadAllEntries(string xmlPath)
        => [.. XDocument.Load(xmlPath)
            .Root!
            .Element("members")!
            .Elements("member")
            .Select(m => new Entry((string)m.Attribute("name")!, Normalize(m)))];

    /// <summary>要素の中身を、空白の差だけで不一致にならない形に均す。</summary>
    private static string Normalize(XElement member)
    {
        string inner = string.Concat(member.Nodes().Select(n => n.ToString()));
        return string.Join(' ', inner.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
    }

    private static bool IsPublicApi(string id, Dictionary<string, Type> exported)
    {
        if (id.Length < 2 || id[1] != ':')
        {
            return false;
        }
        char kind = id[0];
        string body = StripSignature(id[2..]);

        if (kind == 'T')
        {
            // 型ごと生成される道具の出力は翻訳の対象にしない。XAML マークアップコンパイラは
            // XamlMetaDataProvider を、CsWinRT は起動用の型を**公開で**出すので、落とさないと
            // 「ソースに存在しない型に日本語 doc を書け」と要求することになる。
            //
            // **[GeneratedCode] の有無だけでは割れない。**TriggerJsonContext のように
            // 手で書いた partial に生成された partial が付く型にも属性は付いてしまい、
            // 有無で判定すると本物の公開 API まで落ちる (実測: ja にのみ存在する残骸として現れた)。
            // そこで**道具の名前**で挙げる。新しい道具が公開型を出したら、この一覧に無いので
            // 「ja に無い公開 API」として名指しで落ちる — 黙って通ることはない。
            return exported.TryGetValue(body, out Type? declared) && !IsWholeTypeGenerated(declared);
        }

        int split = body.LastIndexOf('.');
        if (split <= 0)
        {
            return false;
        }
        string typeName = body[..split];
        string memberName = body[(split + 1)..];

        // 演算子や明示的実装で使われる '#' 記法 (#ctor / #cctor) を反射側の名前へ戻す
        if (memberName is "#ctor" or "#cctor")
        {
            memberName = memberName.Replace('#', '.');
        }

        if (!exported.TryGetValue(typeName, out Type? type))
        {
            return false;
        }

        // JsonSerializerContext のメンバー (型ごとの JsonTypeInfo プロパティ・GetTypeInfo・
        // コンストラクタ) は STJ のソースジェネレーターが doc ごと生成する。型宣言そのものは
        // 手書きなので残すが、メンバーを翻訳対象にすると
        // 「[JsonSerializable] を 1 行足すたびに ja の XML doc を書き足す」ことになる
        if (DerivesFromJsonSerializerContext(type))
        {
            return false;
        }

        const BindingFlags Flags =
            BindingFlags.Public | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly;
        MemberInfo[] found = memberName == ".ctor"
            ? type.GetConstructors(Flags)
            : type.GetMember(memberName, Flags);

        // ソース生成されたメンバー (STJ の JsonSerializerContext が足す型情報プロパティなど) は
        // 手で書いた API ではない。英語の doc も生成器が出しているので、翻訳の対象にしない
        return found.Length > 0 && !found.All(IsGenerated);
    }

    /// <summary>
    /// 生成されたメンバー / 型かどうか。
    ///
    /// **判定は属性の名前で行う。**`IsDefined(typeof(...))` は実行中のランタイムに読み込んだ
    /// 型としか照合できず、`MetadataLoadContext` から読んだアセンブリでは
    /// 例外になるか、黙って false になる。参照できるアセンブリと参照できないアセンブリで
    /// 検査の意味が変わってはいけないので、両方で同じに効く形にしてある。
    /// </summary>
    private static bool IsGenerated(MemberInfo member) =>
        GeneratorOf(member) is not null;

    /// <summary>
    /// 型そのものがソースに存在しない (道具が型ごと出した) かどうか。
    /// 一覧の根拠は <see cref="IsPublicApi"/> のコメントにある。
    /// </summary>
    private static readonly string[] ToolsThatGenerateWholeTypes =
    [
        "Microsoft.UI.Xaml.Markup.Compiler", // XamlTypeInfo.g.cs の XamlMetaDataProvider
        "CsWinRT",                           // WinRT の起動用の型
    ];

    private static bool IsWholeTypeGenerated(Type type)
        => GeneratorOf(type) is string tool
        && ToolsThatGenerateWholeTypes.Contains(tool, StringComparer.Ordinal);

    /// <summary>
    /// <c>[GeneratedCode]</c> を出した道具の名前。付いていなければ null。
    ///
    /// **属性の型は名前で照合する。**<c>IsDefined(typeof(...))</c> は実行中のランタイムに
    /// 読み込んだ型としか照合できず、<c>MetadataLoadContext</c> から読んだアセンブリでは
    /// 成立しない。参照できるアセンブリと参照できないアセンブリで検査の意味が変わっては
    /// いけないので、両方で同じに効く形にしてある。
    /// </summary>
    private static string? GeneratorOf(MemberInfo member)
    {
        foreach (CustomAttributeData attribute in member.GetCustomAttributesData())
        {
            if (attribute.AttributeType.FullName != "System.CodeDom.Compiler.GeneratedCodeAttribute")
            {
                continue;
            }
            return attribute.ConstructorArguments.Count > 0
                ? attribute.ConstructorArguments[0].Value as string ?? string.Empty
                : string.Empty;
        }
        return null;
    }

    /// <summary>
    /// <c>JsonSerializerContext</c> の派生かどうかを、基底の名前を辿って調べる。
    /// 理由は <see cref="IsGenerated"/> と同じ — `typeof` との比較は
    /// <c>MetadataLoadContext</c> をまたぐと成立しない。
    /// </summary>
    private static bool DerivesFromJsonSerializerContext(Type type)
    {
        for (Type? t = type; t is not null; t = t.BaseType)
        {
            if (t.FullName == "System.Text.Json.Serialization.JsonSerializerContext")
            {
                return true;
            }
        }
        return false;
    }

    /// <summary>ID 末尾の引数リストとジェネリック引数の個数表記を落とす。</summary>
    private static string StripSignature(string body)
    {
        int paren = body.IndexOf('(', StringComparison.Ordinal);
        if (paren >= 0)
        {
            body = body[..paren];
        }
        int tick = body.IndexOf('`', StringComparison.Ordinal);
        return tick >= 0 ? body[..tick] : body;
    }
}
