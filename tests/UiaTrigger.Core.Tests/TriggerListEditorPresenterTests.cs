using System.Text.Json;
using UiaTrigger.Models;
using UiaTrigger.Picker;
using UiaTrigger.Serialization;
using Xunit;

namespace UiaTrigger.Tests;

/// <summary>
/// トリガ一覧エディタの振る舞い (docs/DESIGN.md §4)。
///
/// <para>
/// 一覧・削除・まとめるの規則がサンプルホスト 3 つに**三重に**書かれている形だと、
/// そのホストは NuGet では配れない (docs/RELEASING.md §3) ため、組み込む側から見れば参照できる
/// コードとして存在しない。ここはその規則を 1 か所に集めたものである。
/// </para>
/// <para>
/// いちばん重いのは**値渡し・値返し**の検査である (docs/DESIGN.md §4 の継ぎ目の絶対条件)。
/// エディタが渡されたリストを直に触ると、取り消したはずの編集がホスト側に残る —
/// 例外は出ず、保存されたファイルを見るまで分からない。
/// </para>
/// </summary>
public sealed class TriggerListEditorPresenterTests
{
    // ---- 一覧の描画 ----

    [Fact]
    public void TheRowsAreFormattedFromTheKeyTable()
    {
        var h = new Harness();
        h.Strings.Values[EditorStringKeys.TriggerRow] = "{0}|{1}|{2}|{3}|{4}";
        h.Strings.Values[EditorStringKeys.NoClauses] = "(none)";
        h.Open([Simple("a"), Appear("b")]);

        Assert.Equal(
            ["a|A|a.exe|PropertyChanged|Name Equals", "b|B|b.exe|ElementAppeared|(none)"],
            h.View.Rows);
    }

    [Fact]
    public void ACompositeGetsItsOwnRowFormat()
    {
        var h = new Harness();
        h.Strings.Values[EditorStringKeys.CompositeRow] = "{0}|{1}|{2}|{3}";
        h.Open([Composite()]);

        Assert.Equal(["composite-1|2|WhileMatching|a && b"], h.View.Rows);
    }

    // ---- 値渡し・値返し (継ぎ目の絶対条件) ----

    [Fact]
    public void TheListHandedInIsNeverTouched()
    {
        var h = new Harness();
        TriggerDefinition original = Simple("a");
        var host = new List<TriggerDefinition> { original };
        string before = Json(original);
        h.Open(host);

        h.Presenter.NotifyDeleteRequested(); // 選択なし = 何もしない
        h.View.SelectedIndices = [0];
        h.Presenter.NotifyDeleteRequested();

        // ホストのリストも、その中の定義も動かないこと
        Assert.Single(host);
        Assert.Same(original, host[0]);
        Assert.Equal(before, Json(original));
        Assert.Empty(h.Presenter.Snapshot());
    }

    [Fact]
    public void TheSnapshotIsACopy_SoLaterEditsDoNotReachIt()
    {
        var h = new Harness();
        h.Open([Simple("a")]);
        TriggerDefinition taken = h.Presenter.Snapshot()[0];

        // 返した後に作業用リストを変えても、返したものは動かない
        h.View.SelectedIndices = [0];
        h.Presenter.NotifyDeleteRequested();

        Assert.Equal("a", taken.Id);
        // 逆向きも: 返したものを変えても作業用リストは動かない
        h.Open([Simple("a")]);
        h.Presenter.Snapshot()[0].Id = "changed";
        Assert.Equal("a", h.Presenter.Snapshot()[0].Id);
    }

    [Fact]
    public void AHostThatMutatesItsOwnDefinitionsAfterwardsDoesNotAffectTheEditor()
    {
        var h = new Harness();
        TriggerDefinition original = Simple("a");
        h.Open([original]);

        original.Id = "changed-behind-our-back";
        original.Clauses.Clear();

        Assert.Equal("a", h.Presenter.Snapshot()[0].Id);
        Assert.Single(h.Presenter.Snapshot()[0].Clauses);
    }

    // ---- 削除 ----

    [Fact]
    public void DeleteRemovesEveryySelectedRow_TakingThemFromTheBack()
    {
        var h = new Harness();
        h.Open([Simple("a"), Simple("b"), Simple("c"), Simple("d")]);
        h.View.SelectedIndices = [0, 2];

        h.Presenter.NotifyDeleteRequested();

        // 前から消すと 2 番目を消す時点で添字がずれ、'd' が消える
        Assert.Equal(["b", "d"], h.Presenter.Snapshot().Select(t => t.Id));
    }

    [Fact]
    public void DeleteWithoutASelection_SaysSoAndChangesNothing()
    {
        var h = new Harness();
        h.Strings.Values[EditorStringKeys.SelectSomething] = "select something";
        h.Open([Simple("a")]);

        h.Presenter.NotifyDeleteRequested();

        Assert.Equal("select something", h.View.LastStatus);
        Assert.Single(h.Presenter.Snapshot());
    }

    // ---- 追加 / 編集の口 ----

    [Fact]
    public void AddOpensThePickerWithNothingLoaded()
    {
        var h = new Harness();
        h.Open([]);

        h.Presenter.NotifyAddRequested();

        Assert.Equal([null], h.View.PickerRequests);
    }

    [Fact]
    public void EditOpensThePickerOnACopyOfTheSelectedTrigger()
    {
        var h = new Harness();
        h.Open([Simple("a")]);
        h.View.SelectedIndices = [0];

        h.Presenter.NotifyEditRequested();

        TriggerDefinition? handed = Assert.Single(h.View.PickerRequests);
        Assert.NotNull(handed);
        Assert.Equal("a", handed.Id);
        // **写しであること。**ピッカーは渡された実体へ書き戻すので、直に渡すと
        // 取り消しても作業用リストが変わってしまう
        handed.Id = "scribbled";
        Assert.Equal("a", h.Presenter.Snapshot()[0].Id);
    }

    [Fact]
    public void EditWithoutExactlyOneRow_SaysSoAndOpensNothing()
    {
        var h = new Harness();
        h.Strings.Values[EditorStringKeys.SelectOneToEdit] = "select one";
        h.Open([Simple("a"), Simple("b")]);

        h.View.SelectedIndices = [];
        h.Presenter.NotifyEditRequested();
        Assert.Equal("select one", h.View.LastStatus);

        h.View.SelectedIndices = [0, 1];
        h.Presenter.NotifyEditRequested();
        Assert.Equal("select one", h.View.LastStatus);

        Assert.Empty(h.View.PickerRequests);
    }

    [Fact]
    public void EditingWhatThePickerCannotEdit_SaysSoAndOpensNothing()
    {
        var h = new Harness();
        h.Strings.Values[EditorStringKeys.CannotEditWithThePicker] = "take it apart first";
        h.Open([Composite()]);
        h.View.SelectedIndices = [0];

        h.Presenter.NotifyEditRequested();

        Assert.Equal("take it apart first", h.View.LastStatus);
        Assert.Empty(h.View.PickerRequests);
    }

    // ---- コミットの差し替え規則 ----

    [Fact]
    public void ACommitWhileAdding_ReplacesTheRowWithTheSameId_OrAppends()
    {
        var h = new Harness();
        h.Open([Simple("a"), Simple("b")]);
        h.Presenter.NotifyAddRequested();

        h.Presenter.NotifyPickerCommitted(Simple("c"));
        Assert.Equal(["a", "b", "c"], h.Presenter.Snapshot().Select(t => t.Id));

        // 同じ id は差し替え = 録り直しで 2 件目が増えない
        TriggerDefinition again = Simple("b");
        again.DisplayName = "recorded again";
        h.Presenter.NotifyPickerCommitted(again);
        Assert.Equal(["a", "c", "b"], h.Presenter.Snapshot().Select(t => t.Id));
        Assert.Equal("recorded again", h.Presenter.Snapshot()[^1].DisplayName);
    }

    [Fact]
    public void ACommitWhileEditing_ReplacesTheEditedRowWhereItIs()
    {
        var h = new Harness();
        h.Open([Simple("a"), Simple("b"), Simple("c")]);
        h.View.SelectedIndices = [1];
        h.Presenter.NotifyEditRequested();

        TriggerDefinition edited = Simple("b");
        edited.DisplayName = "edited";
        h.Presenter.NotifyPickerCommitted(edited);

        // 末尾へ移さない。ユーザーが並べた順序を編集で崩さないこと
        Assert.Equal(["a", "b", "c"], h.Presenter.Snapshot().Select(t => t.Id));
        Assert.Equal("edited", h.Presenter.Snapshot()[1].DisplayName);
    }

    [Fact]
    public void ChangingTheIdWhileEditing_KeepsThePositionAndDropsTheCollidingRow()
    {
        var h = new Harness();
        h.Open([Simple("a"), Simple("b"), Simple("c")]);
        h.View.SelectedIndices = [1];
        h.Presenter.NotifyEditRequested();

        // 'b' を編集して 'c' に改名した。id が重複したままだと TriggerMonitor.AddAsync が投げる
        h.Presenter.NotifyPickerCommitted(Simple("c"));

        Assert.Equal(["a", "c"], h.Presenter.Snapshot().Select(t => t.Id));
        // 続けてコミットしても同じ行を差し替える (新しい id で追う)
        TriggerDefinition more = Simple("c");
        more.DisplayName = "again";
        h.Presenter.NotifyPickerCommitted(more);
        Assert.Equal(["a", "c"], h.Presenter.Snapshot().Select(t => t.Id));
        Assert.Equal("again", h.Presenter.Snapshot()[1].DisplayName);
    }

    [Fact]
    public void AfterThePickerCloses_TheNextCommitIsAnAddition()
    {
        var h = new Harness();
        h.Open([Simple("a"), Simple("b")]);
        h.View.SelectedIndices = [0];
        h.Presenter.NotifyEditRequested();
        h.Presenter.NotifyPickerClosed();

        h.Presenter.NotifyPickerCommitted(Simple("c"));

        Assert.Equal(["a", "b", "c"], h.Presenter.Snapshot().Select(t => t.Id));
    }

    [Fact]
    public void ACommittedDefinitionIsCopied_SoThePickerCanKeepWritingToIt()
    {
        var h = new Harness();
        h.Open([]);
        h.Presenter.NotifyAddRequested();
        TriggerDefinition committed = Simple("a");

        h.Presenter.NotifyPickerCommitted(committed);
        // ピッカーは同じ実体へ書き戻し続ける (LoadDefinition の契約)
        committed.Id = "changed-after-commit";

        Assert.Equal("a", h.Presenter.Snapshot()[0].Id);
    }

    // ---- まとめる / ほどく ----

    [Fact]
    public void CombineAddsTheCompositeAndKeepsTheSources()
    {
        var h = new Harness();
        h.Strings.Values[EditorStringKeys.CombineDone] = "{0} from {1}";
        h.Open([Simple("a"), Simple("b")]);
        h.View.SelectedIndices = [0, 1];

        h.Presenter.NotifyCombineRequested();

        Assert.Equal(["a", "b", "composite-1"], h.Presenter.Snapshot().Select(t => t.Id));
        Assert.Equal("composite-1 from 2", h.View.LastStatus);
    }

    [Fact]
    public void CombinePassesTheExpressionAndTheUnwatchedIdsThrough()
    {
        var h = new Harness();
        h.Open([Simple("a"), Simple("b")]);
        h.View.SelectedIndices = [0, 1];
        h.View.ExpressionText = "a && !b";
        h.View.UnwatchedText = " b , ";

        h.Presenter.NotifyCombineRequested();

        TriggerDefinition composite = h.Presenter.Snapshot()[^1];
        Assert.Equal("a && !b", composite.Expression);
        Assert.True(composite.Clauses[0].Watch);
        Assert.False(composite.Clauses[1].Watch);
    }

    /// <summary>
    /// 欄の値が Compose まで届き、Snapshot (JSON クローン経由) でも欠けないこと。
    /// 複合の PollInterval の UI からの唯一の入力口である。
    /// </summary>
    [Fact]
    public void CombinePassesThePollIntervalThrough()
    {
        var h = new Harness();
        h.Open([Simple("a"), Simple("b")]);
        h.View.SelectedIndices = [0, 1];
        h.View.CombinePollIntervalSeconds = 1.5;

        h.Presenter.NotifyCombineRequested();

        Assert.Equal(TimeSpan.FromSeconds(1.5), h.Presenter.Snapshot()[^1].PollInterval);
    }

    /// <summary>欄が空なら未設定 (イベント駆動) のまま。</summary>
    [Fact]
    public void CombineWithoutAPollInterval_LeavesTheCompositeEventDriven()
    {
        var h = new Harness();
        h.Open([Simple("a"), Simple("b")]);
        h.View.SelectedIndices = [0, 1];

        h.Presenter.NotifyCombineRequested();

        Assert.Null(h.Presenter.Snapshot()[^1].PollInterval);
    }

    /// <summary>負値は Compose の理由が Status に出て、何も足されないこと。</summary>
    [Fact]
    public void CombineWithANegativePollInterval_ReportsTheReasonAndAddsNothing()
    {
        var h = new Harness();
        h.Strings.Values[EditorStringKeys.CombineFailed] = "no: {0}";
        h.Open([Simple("a"), Simple("b")]);
        h.View.SelectedIndices = [0, 1];
        h.View.CombinePollIntervalSeconds = -1;

        h.Presenter.NotifyCombineRequested();

        Assert.StartsWith("no: ", h.View.LastStatus, StringComparison.Ordinal);
        Assert.Equal(2, h.Presenter.Snapshot().Count);
    }

    [Fact]
    public void CombineReportsTheReasonAndAddsNothing()
    {
        var h = new Harness();
        h.Strings.Values[EditorStringKeys.CombineFailed] = "no: {0}";
        h.Open([Simple("a"), Simple("b")]);
        h.View.SelectedIndices = [0];

        h.Presenter.NotifyCombineRequested();

        Assert.StartsWith("no: ", h.View.LastStatus, StringComparison.Ordinal);
        Assert.Equal(2, h.Presenter.Snapshot().Count);
    }

    [Fact]
    public void DecomposeAddsThePartsAndKeepsTheComposite()
    {
        var h = new Harness();
        h.Strings.Values[EditorStringKeys.DecomposeDone] = "{1} from {0}";
        h.Open([Composite()]);
        h.View.SelectedIndices = [0];

        h.Presenter.NotifyDecomposeRequested();

        Assert.Equal(["composite-1", "a", "b"], h.Presenter.Snapshot().Select(t => t.Id));
        Assert.Equal("2 from composite-1", h.View.LastStatus);
    }

    [Fact]
    public void DecomposeNeedsExactlyOneComposite()
    {
        var h = new Harness();
        h.Strings.Values[EditorStringKeys.SelectACompositeToDecompose] = "pick a composite";
        h.Open([Simple("a"), Composite()]);

        h.View.SelectedIndices = [0]; // 複合ではない
        h.Presenter.NotifyDecomposeRequested();
        Assert.Equal("pick a composite", h.View.LastStatus);

        h.View.SelectedIndices = [0, 1]; // 2 件
        h.Presenter.NotifyDecomposeRequested();
        Assert.Equal("pick a composite", h.View.LastStatus);

        Assert.Equal(2, h.Presenter.Snapshot().Count);
    }

    /// <summary>
    /// ほどいて編集し、もう一度まとめられること。
    ///
    /// **これが「ほどく」を足した理由そのものである**。
    /// まとめる前のトリガーを削除していると、これが無い限り複合はどこからも編集できなくなる。
    /// </summary>
    [Fact]
    public void DecomposeThenEditThenCombine_IsTheWayBack()
    {
        var h = new Harness();
        h.Open([Composite()]);

        // ほどく → 元の 2 件が戻る
        h.View.SelectedIndices = [0];
        h.Presenter.NotifyDecomposeRequested();

        // その 1 件はピッカーで編集できる (複合はできない)
        h.View.SelectedIndices = [1];
        h.Presenter.NotifyEditRequested();
        Assert.Single(h.View.PickerRequests);
        h.Presenter.NotifyPickerClosed();

        // まとめ直すと、同じ名前なので元の式がそのまま通る
        h.View.SelectedIndices = [1, 2];
        h.View.ExpressionText = "a && b";
        h.Presenter.NotifyCombineRequested();

        TriggerDefinition again = h.Presenter.Snapshot()[^1];
        Assert.Equal("a && b", again.Expression);
        Assert.Equal(["a", "b"], again.Clauses.Select(c => c.Name));
    }

    // ---- 選択の扱い ----

    [Fact]
    public void OutOfRangeAndRepeatedSelections_AreIgnored()
    {
        var h = new Harness();
        h.Open([Simple("a"), Simple("b")]);
        h.View.SelectedIndices = [-1, 0, 0, 5];

        h.Presenter.NotifyDeleteRequested();

        Assert.Equal(["b"], h.Presenter.Snapshot().Select(t => t.Id));
    }

    // ---- 素材 ----

    private static TriggerDefinition Simple(string id) => new()
    {
        Id = id,
        DisplayName = id.ToUpperInvariant(),
        Window = new WindowIdentity { ProcessName = id + ".exe" },
        On = TriggerOn.PropertyChanged,
        Clauses = [new PropertyClause { Property = TriggerProperty.Name, Op = ComparisonOp.Equals, Text = id }],
    };

    private static TriggerDefinition Appear(string id) => new()
    {
        Id = id,
        DisplayName = id.ToUpperInvariant(),
        Window = new WindowIdentity { ProcessName = id + ".exe" },
        On = TriggerOn.ElementAppeared,
    };

    private static TriggerDefinition Composite()
    {
        TriggerCompositionResult result = TriggerComposer.Compose(
            [Simple("a"), Simple("b")], "a && b", [], []);
        Assert.True(result.IsValid);
        return result.Definition!;
    }

    private static string Json(TriggerDefinition definition) => JsonSerializer.Serialize(
        definition, TriggerJsonContext.Default.TriggerDefinition);

    private sealed class Harness
    {
        public FakeEditorView View { get; } = new();

        public FakeStrings Strings { get; } = new();

        public TriggerListEditorPresenter Presenter { get; private set; } = null!;

        /// <summary>文字列を仕込んだ後に開く (presenter は構築時に行を描くため)。</summary>
        public void Open(IReadOnlyList<TriggerDefinition> triggers)
            => Presenter = new TriggerListEditorPresenter(View, Strings, triggers);
    }
}
