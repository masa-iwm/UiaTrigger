// トリガー定義ファイルの読み書き。
//
// 旧形式の検出・移行・退避は持たない。版数 1 より前の形式は公開版に存在せず、
// 読み替えるべき「過去のファイル」が無いためである (docs/DESIGN.md §1)。
// ただしフォーマット版数は最初から書き込んでおく — 版数を持たないファイルを後から
// 見分ける手段は無く、それを増やさないことだけが将来の移行を可能にする。
using System.Globalization;
using System.Text.Json;
using UiaTrigger.Models;
using UiaTrigger.Serialization;

namespace UiaTrigger.Persistence;

/// <summary>Reads and writes a file of <see cref="TriggerDefinition"/> values.</summary>
/// <remarks>
/// A convenience for hosts that keep triggers in their own file. Hosts that store triggers inside
/// a larger settings document should serialize <see cref="TriggerDefinition"/> themselves and
/// chain <see cref="TriggerJsonContext.Default"/> into their own options.
/// </remarks>
public static class TriggerStore
{
    /// <summary>Reads a trigger file. A missing file yields an empty list.</summary>
    /// <exception cref="JsonException">The file is malformed.</exception>
    /// <exception cref="NotSupportedException">
    /// The file was written by a newer version of the format than this library understands.
    /// </exception>
    public static IReadOnlyList<TriggerDefinition> Load(string path)
    {
        ArgumentException.ThrowIfNullOrEmpty(path);
        if (!File.Exists(path))
        {
            return [];
        }

        using FileStream stream = OpenShared(path);

        // **版数だけ先に読む。**全体をデシリアライズしてから版数を見る形だと、
        // 新版ファイルの典型 (列挙メンバーの追加) では版数の判定に**到達しない** —
        // 未知の列挙名が JsonException になり、「新しい形式なので読めない」ではなく
        // 「壊れている」と報告される。呼び出し元は前者なら更新を促せるが、後者では
        // ファイルを捨てる判断に倒れかねない
        int version = ReadVersion(stream);
        if (version > TriggerJson.FormatVersion)
        {
            throw new NotSupportedException(string.Create(
                CultureInfo.CurrentCulture,
                $"'{path}' is version {version}; this build understands up to {TriggerJson.FormatVersion}."));
        }
        stream.Position = 0;

        var file = JsonSerializer.Deserialize(stream, TriggerJsonContext.Default.TriggerFile);
        if (file is null)
        {
            return [];
        }
        // 形の検査 (docs/DESIGN.md C20)。列挙は文字列コンバーターでも**裸の整数を既定で
        // 受理する**ため、"Op": 99 の手編集がデシリアライズを素通りし、例外を出さずに
        // 「鳴らないトリガー / Any 扱い」へ化ける。域外の値は形式の誤りとしてここで弾く。
        // 意味の検証 (窓の要求・オペランドの有無) は**しない** — 1 件の意味エラーで
        // ファイル全体が読めなくなると、一覧表示だけしたいホストに対して過剰であり、
        // 意味の合否は従来どおり AddAsync が言う
        if (file.Triggers is null)
        {
            throw new JsonException(string.Create(CultureInfo.CurrentCulture, $"'{path}': Triggers is null."));
        }
        for (int i = 0; i < file.Triggers.Count; i++)
        {
            TriggerDefinition? definition = file.Triggers[i];
            if (definition is null)
            {
                throw new JsonException(string.Create(
                    CultureInfo.CurrentCulture, $"'{path}': trigger [{i}] is null."));
            }
            if (TriggerDefinitionRules.CheckShape(definition) is { } reason)
            {
                throw new JsonException(reason);
            }
        }
        return file.Triggers;
    }

    /// <summary>
    /// 形式版数だけを読む (本体はまだ解釈しない)。
    /// </summary>
    /// <remarks>
    /// 未知の列挙名などで本体の解釈が落ちても、版数の判定はその**前**に済ませたい。
    /// 版数が無い / 数値でない場合は 0 を返す — 「版数を持たないファイル」は
    /// このライブラリが書いたものではないので、下流の解釈に任せて JsonException にする。
    /// </remarks>
    private static int ReadVersion(Stream stream)
    {
        try
        {
            using JsonDocument document = JsonDocument.Parse(stream);
            return document.RootElement.ValueKind == JsonValueKind.Object &&
                   document.RootElement.TryGetProperty("Version", out JsonElement version) &&
                   version.ValueKind == JsonValueKind.Number
                ? version.GetInt32()
                : 0;
        }
        catch (JsonException)
        {
            // 版数すら読めない = 形式として壊れている。理由は本体の解釈に言わせる
            // (ここで投げると「新しすぎる」と「壊れている」を取り違える)
            return 0;
        }
    }

    /// <summary>
    /// 読み取り用にファイルを開く。**共有モードに <see cref="FileShare.Delete"/> を含めること。**
    /// </summary>
    /// <remarks>
    /// <see cref="Save"/> の原子的な置き換え (<see cref="File.Replace(string, string, string?)"/>)
    /// は宛先の**名前の差し替え**なので、読み手が Delete を共有しないと、
    /// **誰かが読んでいる一瞬に重なった保存が共有違反 (IOException) で失敗する**。
    /// 実際に T4 で起きた — ピッカーは「Added: ...」と言うのに、保存だけが黙って落ちて
    /// 定義はメモリにしか残らなかった (ホストは SaveFailed をステータスに出すだけで再試行しない)。
    /// 監視プロセスがトリガーファイルを読むのは普通の使い方なので、これはテストの都合ではない。
    /// Write は共有しない — 書き手はこのライブラリの置き換えだけを想定し、
    /// truncate で書く他者の半端な内容を読まないためである。
    /// 開いている読み手は置き換え後も**古い中身**を最後まで読む (名前が差し替わるだけで
    /// 実体は残る) ので、半端な JSON を読む瞬間は存在しない。
    /// </remarks>
    internal static FileStream OpenShared(string path)
        => new(path, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete);

    /// <summary>Writes a trigger file, replacing any existing one atomically.</summary>
    public static void Save(string path, IEnumerable<TriggerDefinition> triggers)
    {
        ArgumentException.ThrowIfNullOrEmpty(path);
        ArgumentNullException.ThrowIfNull(triggers);

        var file = new TriggerFile { Triggers = [.. triggers] };
        // truncate-in-place で書くと、書き込み中のクラッシュで全トリガー定義が失われる
        // (docs/DESIGN.md A17)
        AtomicFile.Write(path, stream => JsonSerializer.Serialize(stream, file, TriggerJsonContext.Default.TriggerFile));
    }
}
