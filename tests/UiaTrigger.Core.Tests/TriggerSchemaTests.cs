// トリガーファイルの JSON Schema (docs/DESIGN.md C22)。
//
// **schema はモデルから生成する。**手で書いた写しは、モデルが育った日に黙って古くなり、
// エディタが「正しい定義を赤線で拒む」という一番たちの悪い形で出る。生成にしてあるので
// ずれようが無い — この検査が縛るのは、**リポジトリに置いてある写しのほう**である。
using System.Text.Json;
using UiaTrigger.Models;
using UiaTrigger.Persistence;
using UiaTrigger.Serialization;
using Xunit;

namespace UiaTrigger.Tests;

public sealed class TriggerSchemaTests
{
    private static TriggerDefinition Sample() => new()
    {
        Id = "sample",
        Window = new WindowIdentity { ProcessName = "notepad" },
        On = TriggerOn.PropertyChanged,
        Clauses = [new PropertyClause { Property = TriggerProperty.Name, Op = ComparisonOp.Always }],
    };

    /// <summary>
    /// リポジトリに置いてある schema が、いまのモデルから生成されるものと一致すること。
    /// </summary>
    /// <remarks>
    /// <c>schema/triggers.schema.json</c> は**公開する写し**である ($id が指す先であり、
    /// エディタがネット越しに引く先でもある)。モデルを変えたらこの写しも作り直す必要があり、
    /// 忘れると「配ってある schema が実装と違う」状態になる。
    ///
    /// <para>
    /// 直し方は手で書き足すことではない。<c>TriggerJson.Schema</c> の中身をこのファイルへ
    /// 上書きする — **生成物が正である。**
    /// </para>
    /// </remarks>
    [Fact]
    public void TheSchemaInTheRepositoryMatchesTheOneGeneratedFromTheModel()
    {
        string published = File.ReadAllText(RepoPaths.Combine("schema", "triggers.schema.json"));

        Assert.Equal(
            TriggerJson.Schema.ReplaceLineEndings("\n").TrimEnd(),
            published.ReplaceLineEndings("\n").TrimEnd(),
            StringComparer.Ordinal);
    }

    /// <summary>
    /// 生成した schema が、モデルの列挙をすべて名前で載せていること。
    /// </summary>
    /// <remarks>
    /// **0 件で緑にしない。**生成が空を返しても上の一致検査は通ってしまう (写しも空になるため)。
    /// 列挙は補完のいちばん実用的な部分なので、名前が載っていることを別に見る。
    /// </remarks>
    [Fact]
    public void TheSchemaListsEveryEnumMemberByName()
    {
        string schema = TriggerJson.Schema;

        foreach (string name in Enum.GetNames<TriggerOn>()
            .Concat(Enum.GetNames<ComparisonOp>())
            .Concat(Enum.GetNames<TriggerProperty>())
            .Concat(Enum.GetNames<MatchStrength>())
            .Concat(Enum.GetNames<TreeViewMode>())
            .Concat(Enum.GetNames<ClauseCombinator>()))
        {
            Assert.Contains($"\"{name}\"", schema, StringComparison.Ordinal);
        }

        // 数値で書かれていたら補完の役に立たない (名前で書くのはモデル側の宣言である)
        Assert.DoesNotContain("\"enum\": [\n        0", schema.ReplaceLineEndings("\n"), StringComparison.Ordinal);
    }

    /// <summary>
    /// 保存すると schema が隣に置かれ、ファイルがそれを指すこと。
    /// </summary>
    /// <remarks>
    /// <c>$schema</c> は**相対名**なので、隣に置かれないと壊れた参照が入ったファイルを
    /// 配ることになる。エディタは黙って何もしないので、書いた側は気づかない。
    /// </remarks>
    [Fact]
    public void SavingWritesTheSchemaBesideTheFileAndPointsAtIt()
    {
        string folder = Path.Combine(Path.GetTempPath(), "uiatrigger-schema-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        try
        {
            string path = Path.Combine(folder, "triggers.json");
            TriggerStore.Save(path, [Sample()]);

            string schemaPath = Path.Combine(folder, TriggerJson.SchemaFileName);
            Assert.True(File.Exists(schemaPath), $"schema が隣に置かれていません: {schemaPath}");
            Assert.Equal(TriggerJson.Schema, File.ReadAllText(schemaPath), StringComparer.Ordinal);

            using JsonDocument document = JsonDocument.Parse(File.ReadAllText(path));
            Assert.Equal(
                TriggerJson.SchemaFileName,
                document.RootElement.GetProperty("$schema").GetString());
        }
        finally
        {
            Directory.Delete(folder, recursive: true);
        }
    }

    /// <summary>
    /// <c>$schema</c> が入ったファイルがそのまま読めること。
    /// </summary>
    /// <remarks>
    /// 書けるのに読めない形にすると、**自分が保存したファイルを自分で拒む**。
    /// 逆に <c>$schema</c> を持たない (別の道具が書いた) ファイルも読めること。
    /// </remarks>
    [Fact]
    public void ReadingToleratesTheSchemaMember()
    {
        string folder = Path.Combine(Path.GetTempPath(), "uiatrigger-schema-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        try
        {
            string path = Path.Combine(folder, "triggers.json");
            TriggerStore.Save(path, [Sample()]);
            Assert.Equal("sample", Assert.Single(TriggerStore.Load(path)).Id);

            // ネガティブコントロール: $schema が無いファイルも読める
            string bare = Path.Combine(folder, "bare.json");
            File.WriteAllText(bare, $$"""{"Version": {{TriggerJson.FormatVersion}}, "Triggers": []}""");
            Assert.Empty(TriggerStore.Load(bare));
        }
        finally
        {
            Directory.Delete(folder, recursive: true);
        }
    }
}
