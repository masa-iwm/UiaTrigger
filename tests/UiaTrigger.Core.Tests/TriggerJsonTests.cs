using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;
using UiaTrigger.Models;
using UiaTrigger.Persistence;
using UiaTrigger.Serialization;
using Xunit;

namespace UiaTrigger.Tests;

// ホスト側の context を模したもの。実際の App / TestHost はこれと同じ形で
// 自分の設定型を持ち、ライブラリの context を鎖に足す。
[JsonSourceGenerationOptions(WriteIndented = true)]
[JsonSerializable(typeof(HostSettings))]
internal sealed partial class HostJsonContext : JsonSerializerContext;

internal sealed class HostSettings
{
    public string Profile { get; set; } = string.Empty;
    public List<TriggerDefinition> Triggers { get; set; } = [];
}

/// <summary>
/// ライブラリが JsonSerializerContext を提供し、ホストが TypeInfoResolverChain で
/// 合成できること (docs/DESIGN.md C8)。
///
/// これが成立しないと、ホストごとに同じ context ファイルを複製することになる
/// (実際、修正前は App と TestHost に同一ファイルが 2 重にあった)。
/// </summary>
public sealed class TriggerJsonTests
{
    /// <summary>ホストが自分の context にライブラリの context を鎖で足した状態。</summary>
    private static readonly JsonSerializerOptions ComposedOptions = new()
    {
        TypeInfoResolverChain = { HostJsonContext.Default, TriggerJsonContext.Default },
    };

    private static TriggerDefinition Sample() => new()
    {
        Id = "notepad-title",
        DisplayName = "Window: Untitled",
        Window = new WindowIdentity
        {
            ProcessName = "notepad.exe",
            ClassName = "Notepad",
            ClassNameMatch = MatchStrength.Required,
            WindowName = "Untitled - Notepad",
        },
        Locator = new ElementLocator
        {
            View = TreeViewMode.Control,
            Search = new ElementSearch { AutomationId = "editor" },
            Steps =
            [
                new ElementPathStep { ControlType = 50033, ClassName = "Edit", SiblingIndex = 1 },
                new ElementPathStep { ControlType = 50004, AutomationId = "editor" },
            ],
        },
        On = TriggerOn.WhileMatching,
        Combine = ClauseCombinator.Any,
        Clauses =
        [
            new PropertyClause { Property = TriggerProperty.Name, Op = ComparisonOp.RegexMatch, Text = @"\d+%" },
            new PropertyClause { Property = TriggerProperty.RangeValue, Op = ComparisonOp.Equals, Text = "42", Tolerance = 0.5 },
        ],
        MinInterval = TimeSpan.FromMilliseconds(500),
        PollInterval = TimeSpan.FromSeconds(2),
    };

    private static void AssertSameAsSample(TriggerDefinition actual)
    {
        TriggerDefinition expected = Sample();
        Assert.Equal(expected.Id, actual.Id);
        Assert.Equal(expected.On, actual.On);
        Assert.Equal(expected.Combine, actual.Combine);
        Assert.Equal(expected.MinInterval, actual.MinInterval);
        // ポーリング間隔はトリガーと一緒に永続化される。鳴らないのは実行ごとの事情ではなく
        // 対象アプリの性質なので、保存されないと利用者が毎回入れ直すことになる
        Assert.Equal(expected.PollInterval, actual.PollInterval);
        Assert.Equal(expected.Window.ClassNameMatch, actual.Window.ClassNameMatch);
        Assert.Equal(expected.Window.WindowNameMatch, actual.Window.WindowNameMatch);
        Assert.Equal(expected.Locator.Steps.Count, actual.Locator.Steps.Count);
        // Search 方式は「経路より先に試される」= これが落ちると解決の速い側が黙って消える
        Assert.Equal("editor", actual.Locator.Search?.AutomationId);
        Assert.Equal("Edit", actual.Locator.Steps[0].ClassName);
        // 記録できなかった兄弟インデックスが -1 のまま往復すること (0 に化けない)
        Assert.Equal(ElementPathStep.UnknownSiblingIndex, actual.Locator.Steps[1].SiblingIndex);
        Assert.Equal(2, actual.Clauses.Count);
        Assert.Equal(0.5, actual.Clauses[1].Tolerance);
    }

    [Fact]
    public void Definition_RoundTripsThroughTheLibraryContext()
    {
        string json = JsonSerializer.Serialize(Sample(), TriggerJsonContext.Default.TriggerDefinition);

        AssertSameAsSample(JsonSerializer.Deserialize(json, TriggerJsonContext.Default.TriggerDefinition)!);
    }

    /// <summary>
    /// 列挙は名前で書かれること。数値だと、後から列挙メンバーを挿入した瞬間に
    /// 保存済みファイルの意味が静かに変わる。
    /// </summary>
    [Fact]
    public void Enums_AreWrittenAsNames()
    {
        string json = JsonSerializer.Serialize(Sample(), TriggerJsonContext.Default.TriggerDefinition);

        Assert.Contains("\"WhileMatching\"", json, StringComparison.Ordinal);
        Assert.Contains("\"Required\"", json, StringComparison.Ordinal);
        Assert.Contains("\"RegexMatch\"", json, StringComparison.Ordinal);
    }

    /// <summary>
    /// C8 の本題。ホストが自分の context にライブラリの context を鎖で足せば、
    /// ホスト側の型の中に埋め込んだトリガー定義まで一度に往復できること。
    /// </summary>
    [Fact]
    public void HostCanComposeTheLibraryContextIntoItsOwnOptions()
    {
        var settings = new HostSettings { Profile = "default", Triggers = [Sample()] };

        string json = JsonSerializer.Serialize(settings, ComposedOptions);
        HostSettings restored = JsonSerializer.Deserialize<HostSettings>(json, ComposedOptions)!;

        Assert.Equal("default", restored.Profile);
        AssertSameAsSample(Assert.Single(restored.Triggers));
    }

    /// <summary>合成した経路でも列挙が名前のままであること (鎖に足すと既定に戻る、を防ぐ)。</summary>
    [Fact]
    public void ComposedOptions_StillWriteEnumsAsNames()
    {
        string json = JsonSerializer.Serialize(new HostSettings { Triggers = [Sample()] }, ComposedOptions);

        Assert.Contains("\"WhileMatching\"", json, StringComparison.Ordinal);
    }

    // ---------- 複合条件 (docs/DESIGN.md §4) ----------

    /// <summary>
    /// 式と句ごとの要素が往復すること。入れ子を 1 本の文字列で持つ狙いは、
    /// 判別子もコンバータも足さずに source-gen の経路へそのまま乗ることである。
    /// </summary>
    [Fact]
    public void Definition_RoundTripsAnExpressionAndPerClauseElements()
    {
        var definition = new TriggerDefinition
        {
            Id = "two-apps",
            Window = new WindowIdentity { ProcessName = "a.exe" },
            On = TriggerOn.PropertyChanged,
            Expression = "(ready || idle) && !busy",
            Clauses =
            [
                new PropertyClause { Name = "ready", Property = TriggerProperty.Name, Op = ComparisonOp.Equals, Text = "Ready" },
                new PropertyClause
                {
                    Name = "idle",
                    DisplayName = "the other app is idle",
                    Window = new WindowIdentity { ProcessName = "b.exe" },
                    Locator = new ElementLocator { Search = new ElementSearch { AutomationId = "status" } },
                    Watch = false,
                    Property = TriggerProperty.Value,
                    Op = ComparisonOp.Equals,
                    Text = "Idle",
                },
                new PropertyClause { Name = "busy", Property = TriggerProperty.IsEnabled, Op = ComparisonOp.Equals, Text = "false" },
            ],
        };

        string json = JsonSerializer.Serialize(definition, TriggerJsonContext.Default.TriggerDefinition);
        TriggerDefinition restored = JsonSerializer.Deserialize(json, TriggerJsonContext.Default.TriggerDefinition)!;

        Assert.Equal("(ready || idle) && !busy", restored.Expression);
        Assert.Equal(["ready", "idle", "busy"], restored.Clauses.Select(c => c.Name));
        Assert.Equal("the other app is idle", restored.Clauses[1].DisplayName);
        Assert.Equal("b.exe", restored.Clauses[1].Window?.ProcessName);
        Assert.Equal("status", restored.Clauses[1].Locator?.Search?.AutomationId);
        // 句ごとに Watch が保たれること。絞るだけのつもりの句が発火源に変わると、
        // 「A が存在していて B が変化したとき」が黙って「どちらが変化しても」になる
        Assert.False(restored.Clauses[1].Watch);
        Assert.True(restored.Clauses[0].Watch);
        // 上書きしていない句は null のまま = 「ほかの句と同じ要素」。
        // ここが空の WindowIdentity に化けると、同じ意味を 2 通りで持つことになる
        Assert.Null(restored.Clauses[0].Window);
        Assert.Null(restored.Clauses[0].Locator);
    }

    /// <summary>
    /// <see cref="PropertyClause.Watch"/> を書いていない JSON は true として読めること。
    /// 既定が往復で消えると、条件を絞るだけのつもりの句が黙って発火源に変わる — 逆も同じ。
    /// </summary>
    [Fact]
    public void Clause_WatchDefaultsToTrueWhenAbsent()
    {
        const string json = """
            { "Id": "x", "Clauses": [ { "Property": "Name", "Op": "Equals", "Text": "a" } ] }
            """;

        TriggerDefinition restored = JsonSerializer.Deserialize(json, TriggerJsonContext.Default.TriggerDefinition)!;

        Assert.True(Assert.Single(restored.Clauses).Watch);
        Assert.Null(restored.Expression);
    }

    /// <summary>
    /// <see cref="TriggerDefinition.NotifyOnStoppedMatching"/> を書いていない JSON
    /// (preview.3 以前が保存したもの) は false として読めること。
    /// 既定が変わると、古いファイルのトリガーが黙って立ち下がりも鳴らし始める。
    /// </summary>
    [Fact]
    public void Definition_NotifyOnStoppedMatchingDefaultsToFalseWhenAbsent()
    {
        const string json = """
            { "Id": "x", "On": "WhileMatching",
              "Clauses": [ { "Property": "Name", "Op": "Equals", "Text": "a" } ] }
            """;

        TriggerDefinition restored = JsonSerializer.Deserialize(json, TriggerJsonContext.Default.TriggerDefinition)!;

        Assert.False(restored.NotifyOnStoppedMatching);
    }

    [Fact]
    public void Definition_NotifyOnStoppedMatchingSurvivesBeingTrue()
    {
        var definition = new TriggerDefinition
        {
            Id = "x",
            On = TriggerOn.WhileMatching,
            NotifyOnStoppedMatching = true,
            Clauses = [new PropertyClause { Property = TriggerProperty.Name, Op = ComparisonOp.Always }],
        };

        string json = JsonSerializer.Serialize(definition, TriggerJsonContext.Default.TriggerDefinition);
        TriggerDefinition restored = JsonSerializer.Deserialize(json, TriggerJsonContext.Default.TriggerDefinition)!;

        Assert.True(restored.NotifyOnStoppedMatching);
    }

    [Fact]
    public void Clause_WatchSurvivesBeingFalse()
    {
        var definition = new TriggerDefinition
        {
            Id = "x",
            Clauses = [new PropertyClause { Property = TriggerProperty.Name, Op = ComparisonOp.Always, Watch = false }],
        };

        string json = JsonSerializer.Serialize(definition, TriggerJsonContext.Default.TriggerDefinition);
        TriggerDefinition restored = JsonSerializer.Deserialize(json, TriggerJsonContext.Default.TriggerDefinition)!;

        Assert.False(Assert.Single(restored.Clauses).Watch);
    }

    /// <summary>ライブラリの型情報が実際に鎖から引けること。</summary>
    [Fact]
    public void TheLibraryResolverProvidesTheModelTypes()
    {
        IJsonTypeInfoResolver resolver = TriggerJsonContext.Default;

        Assert.NotNull(resolver.GetTypeInfo(typeof(TriggerDefinition), ComposedOptions));
        Assert.NotNull(resolver.GetTypeInfo(typeof(List<TriggerDefinition>), ComposedOptions));
    }

    // ---------- ファイル入出力 ----------

    [Fact]
    public void Store_RoundTripsAFile()
    {
        string path = Path.Combine(Path.GetTempPath(), $"uiatrigger-{Guid.NewGuid():N}", "triggers.json");
        try
        {
            TriggerStore.Save(path, [Sample()]);

            AssertSameAsSample(Assert.Single(TriggerStore.Load(path)));
        }
        finally
        {
            Cleanup(path);
        }
    }

    /// <summary>
    /// 版数を書き込んでおくこと。版数の無いファイルは後から見分けようがなく、
    /// 「将来の移行を可能にする」ためだけに最初から入れてある。
    /// </summary>
    [Fact]
    public void Store_StampsTheFormatVersion()
    {
        string path = Path.Combine(Path.GetTempPath(), $"uiatrigger-{Guid.NewGuid():N}", "triggers.json");
        try
        {
            TriggerStore.Save(path, [Sample()]);

            using JsonDocument document = JsonDocument.Parse(File.ReadAllText(path));
            Assert.Equal(TriggerJson.FormatVersion, document.RootElement.GetProperty("Version").GetInt32());
        }
        finally
        {
            Cleanup(path);
        }
    }

    /// <summary>
    /// 域外の列挙 (裸の整数 — 手編集で入る) を黙って読まないこと (docs/DESIGN.md C20)。
    /// JsonStringEnumConverter は未知の**名前**は拒むが**整数**は既定で受理するため、
    /// "Op": 99 はデシリアライズを素通りし、例外を出さずに「永久不成立の句」へ化ける。
    /// </summary>
    [Fact]
    public void Store_RefusesAnUndefinedEnumValue()
    {
        string path = Path.Combine(Path.GetTempPath(), $"uiatrigger-{Guid.NewGuid():N}", "triggers.json");
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, """
                {"Version": 1, "Triggers": [ { "Id": "x", "Clauses": [ { "Property": "Name", "Op": 99 } ] } ]}
                """);

            JsonException error = Assert.Throws<JsonException>(() => TriggerStore.Load(path));
            Assert.Contains("99", error.Message, StringComparison.Ordinal);
        }
        finally
        {
            Cleanup(path);
        }
    }

    /// <summary>知らない版数を黙って空として読まないこと (次の保存で元ファイルを失う)。</summary>
    [Fact]
    public void Store_RefusesAFileFromANewerFormat()
    {
        string path = Path.Combine(Path.GetTempPath(), $"uiatrigger-{Guid.NewGuid():N}", "triggers.json");
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, $$"""{"Version": {{TriggerJson.FormatVersion + 1}}, "Triggers": []}""");

            Assert.Throws<NotSupportedException>(() => TriggerStore.Load(path));
        }
        finally
        {
            Cleanup(path);
        }
    }

    /// <summary>
    /// **読み手がファイルを掴んだままでも、保存が成功すること。**
    /// </summary>
    /// <remarks>
    /// <see cref="TriggerStore.Save"/> は <c>File.Replace</c> (宛先の名前の差し替え) なので、
    /// 読み手が <see cref="FileShare.Delete"/> を共有しないと、読んでいる一瞬に重なった保存が
    /// **共有違反で失敗する**。T4 で実際に起きた形: ピッカーのコミットは
    /// 成功と表示されるのに、ホストの保存だけが黙って落ちて定義がファイルに入らない。
    /// 読み手を <see cref="TriggerStore.OpenShared"/> (Load と同じ開き方) で立てるのは、
    /// テストが独自のフラグで開くと <c>Load</c> の実装が変わっても検査が緩まないためである。
    /// </remarks>
    [Fact]
    public void Store_SavesWhileAReaderHoldsTheFileOpen()
    {
        string path = Path.Combine(Path.GetTempPath(), $"uiatrigger-{Guid.NewGuid():N}", "triggers.json");
        try
        {
            TriggerStore.Save(path, [Sample()]);

            TriggerDefinition second = Sample();
            second.Id = "second";
            using (FileStream reader = TriggerStore.OpenShared(path))
            {
                TriggerStore.Save(path, [Sample(), second]);
            }

            Assert.Equal(2, TriggerStore.Load(path).Count);
        }
        finally
        {
            Cleanup(path);
        }
    }

    [Fact]
    public void Store_ReturnsNothingForAMissingFile()
    {
        Assert.Empty(TriggerStore.Load(Path.Combine(Path.GetTempPath(), $"uiatrigger-missing-{Guid.NewGuid():N}.json")));
    }

    private static void Cleanup(string path)
    {
        string? directory = Path.GetDirectoryName(path);
        if (directory is not null && Directory.Exists(directory))
        {
            Directory.Delete(directory, recursive: true);
        }
    }
}
