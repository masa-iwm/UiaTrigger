using System.CodeDom.Compiler;
using System.Reflection;
using System.Text.Json.Serialization;
using System.Xml.Linq;

namespace UiaTrigger.Tests;

/// <summary>
/// 生成された XML ドキュメントファイルを「公開 API のぶんだけ」に絞って読むヘルパー
/// (docs/DESIGN.md L1)。
///
/// csc が出す .xml には <b>internal / private のメンバーも入る</b> — 日本語で書いてよいと
/// 決めた実装コメント (docs/LOCALIZATION.md §1 の L1) がそのまま同じファイルに並ぶ。ここで公開 API だけを
/// 取り出さないと「英語であること」も「翻訳が揃っていること」も検査できない。
///
/// 判定はドキュメント ID から型名・メンバー名を取り出してリフレクションに問い合わせる方式。
/// 署名までは見ず<b>名前だけ</b>で照合する: 欲しいのは可視性であり、同名の多重定義で
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
            return exported.ContainsKey(body);
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
        if (typeof(JsonSerializerContext).IsAssignableFrom(type))
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

    private static bool IsGenerated(MemberInfo member) =>
        member.IsDefined(typeof(GeneratedCodeAttribute), inherit: false);

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
