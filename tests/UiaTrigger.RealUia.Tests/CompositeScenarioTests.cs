using System.Text.Json;
using UiaTrigger.Models;
using UiaTrigger.Monitoring;
using UiaTrigger.Serialization;
using Xunit;

namespace UiaTrigger.RealUia.Tests;

/// <summary>
/// 1 つのトリガーが複数の要素にまたがること (docs/DESIGN.md §4) を実 UIA で確認する。
///
/// <para>
/// <b>対象は 2 プロセスにする。</b>同一ウィンドウ内の 2 要素だと、スロットが割れていようが
/// まとまっていようが素通りしてしまい何も証明しない。プロセスをまたぐと
/// 「ウィンドウごとに解決し、ウィンドウごとに購読する」が本当に効いていないと通らない。
/// あわせて WinForms (MSAA ブリッジ) と WPF (ネイティブ UIA プロバイダー) を 1 つずつ使い、
/// docs/DESIGN.md §6 が言うプロバイダー差の両側をまたぐ。
/// </para>
///
/// <para>
/// <b>片方だけ満たしたときに鳴らないこと (ネガティブコントロール) がこの一群の主眼である。</b>
/// 「両方満たしたら鳴った」だけなら、条件を無視して鳴っているだけかもしれない。
/// </para>
/// </summary>
public sealed class CompositeScenarioTests
{
    /// <summary>変化を 1 件ずつ検出させるための間隔。詰めて送ると UIA 側でまとまってしまう。</summary>
    private static readonly TimeSpan BetweenChanges = TimeSpan.FromMilliseconds(250);

    /// <summary>発火しないことを見るための観測窓。</summary>
    private static readonly TimeSpan NoFireWindow = TimeSpan.FromSeconds(3);

    /// <summary>
    /// 2 つの対象アプリを、<b>重ならない位置に</b>並べて起動する。
    ///
    /// <para>
    /// どちらも既定位置 (40,40) に 520x1000 で出るので、そのままだと完全に重なる。
    /// 記録は座標 (<c>ElementFromPoint</c>) を通るため、重なっていると
    /// 一方の記録がもう一方のタイトルバーを掴む。
    /// </para>
    ///
    /// <para>
    /// 退かせるのは WinForms 側である。<c>place</c> を持っているのが WinForms の対象アプリ
    /// だけで、WPF 側には無い。位置は決め打ちにせず WPF の窓の右端から計算する —
    /// 表示スケールによって物理座標が変わるためである。
    /// </para>
    /// </summary>
    private static (TestTargetProcess WinForms, TestTargetProcess Wpf) StartSideBySide(
        string winFormsControl, string wpfControl)
    {
        TestTargetProcess wpf = TestTargetProcess.Start(TargetProfile.Wpf);
        TestTargetProcess winForms = TestTargetProcess.Start(TargetProfile.WinForms);
        try
        {
            wpf.Send("add-button " + wpfControl);
            winForms.Send("add-button " + winFormsControl);

            (_, _, int wpfRight, _) = wpf.RectOf(wpfControl);
            winForms.Send(FormattableString.Invariant($"place {wpfRight + 40} 40 520 1000"));

            (int left, _, _, _) = winForms.RectOf(winFormsControl);
            Assert.True(
                left > wpfRight,
                $"2 つの対象アプリのコントロールが重なっています " +
                $"(WPF 側ボタンの右端 {wpfRight}, WinForms 側ボタンの左端 {left})。" +
                "この状態では座標からの記録が別のウィンドウを掴むため、テストに検出力がありません。");
            return (winForms, wpf);
        }
        catch
        {
            winForms.Dispose();
            wpf.Dispose();
            throw;
        }
    }

    /// <summary>
    /// 記録済みの定義を、別のトリガーの句として取り込む。
    ///
    /// 記録はユーザーと同じ経路 (座標から) で行い、そこから (Window, Locator) だけを取り出す。
    /// 手で組むと「解決側は正しいが記録側が壊れている」組み合わせを永久に見逃す。
    /// </summary>
    private static PropertyClause AsClause(TriggerDefinition recorded, string name, string expected, bool watch = true) =>
        new()
        {
            Name = name,
            Window = recorded.Window,
            Locator = recorded.Locator,
            Watch = watch,
            Property = TriggerProperty.Name,
            Op = ComparisonOp.Equals,
            Text = expected,
        };

    /// <summary>
    /// 「A のラベルが ready、<b>かつ</b> B のラベルが done」。
    ///
    /// 片方だけ満たした時点では鳴らず、もう片方を満たした瞬間に 1 回だけ鳴ること。
    /// <c>TriggerDefinition</c> が <c>Window</c> と <c>Locator</c> を 1 組しか
    /// 持たない形では、この条件は原理的に書けない。
    /// </summary>
    [Fact]
    public async Task ClausesOnTwoProcesses_FireOnlyWhenBothHold()
    {
        (TestTargetProcess a, TestTargetProcess b) = StartSideBySide("btnA", "btnB");
        using TestTargetProcess winForms = a;
        using TestTargetProcess wpf = b;

        TriggerDefinition recordedA = (await Recording.RecordAsync(a, "btnA")).PinToWindow(a);
        TriggerDefinition recordedB = (await Recording.RecordAsync(b, "btnB")).PinToWindow(b);

        var definition = new TriggerDefinition
        {
            Id = "both",
            // 既定の要素は A に合わせておく。句が両方とも上書きしているので解決には使われない
            Window = recordedA.Window,
            Locator = recordedA.Locator,
            On = TriggerOn.WhileMatching,
            Expression = "ready && done",
            Clauses = [AsClause(recordedA, "ready", "ready"), AsClause(recordedB, "done", "done")],
        };

        await using var harness = new MonitorHarness();
        await harness.StartAsync(definition);
        harness.WaitForResolution(resolved: true);

        // 片方だけ満たす — ここで鳴ってはならない
        a.Send("set-text btnA ready");
        await Task.Delay(BetweenChanges, TestContext.Current.CancellationToken);
        harness.AssertNoFire(NoFireWindow);

        // もう片方を満たすと鳴る
        b.Send("set-text btnB done");
        TriggerFiredEventArgs fired = harness.WaitForFire("both");

        // ---- イベントに載る値は「先頭の句の要素」のもので、3 つとも整合していること ----
        //
        // <b>鳴らしたのは B の変化だが、載るのは A の値である</b> (docs/DESIGN.md §4)。
        // 何がトリガーを鳴らしたかと、イベントが何を報告するかは別の問いだと doc が
        // 述べているのはこの形のことで、ここが唯一それを実物で押さえる場所である。
        //
        // 退行の形: Properties を「変化を報告したスロットのもの」に戻すと、
        // NewValue が A・Properties が B という<b>1 つのイベントが 2 つの要素を指す</b>状態になり、
        // 最後の 2 行が落ちる。例外は出ないので、これが無いと誰も気づけない
        Assert.Equal("ready", fired.NewValue.Value);
        Assert.NotNull(fired.Properties);
        Assert.Equal("ready", fired.Properties.GetComparisonValue(TriggerProperty.Name).Value);
        // WhileMatching は「条件全体が成立した」ので、1 つの値の変化前は存在しない。
        // <b>HasValue で見ること</b> — 「読めなかった」と「空文字だった」は別である
        // (ComparisonString.Value は前者でも空文字を返す)
        Assert.False(fired.OldValue.HasValue);

        // ---- 句ごとの値は<b>それぞれ自分の要素</b>から来ること (docs/DESIGN.md §4) ----
        //
        // 上の 3 行が「1 つの要素しか語れない」ことの裏返しである。2 プロセスにまたがる
        // トリガーで B 側がどうだったかを言えるのは、ここだけである。
        Assert.Equal(["ready", "done"], fired.Clauses.Select(c => c.Name));
        Assert.Equal(["ready", "done"], fired.Clauses.Select(c => c.Value.Value));
        Assert.All(fired.Clauses, c => Assert.Equal(ClauseOutcome.Matched, c.Outcome));
    }

    /// <summary>
    /// 「B が done <b>のまま</b>、A が ready になったとき」を
    /// <see cref="PropertyClause.Watch"/> = false で書けること。
    ///
    /// <para>
    /// 購読は <c>Property</c> から作られ <c>Op</c> を見ないので、B の句をそのまま置くと
    /// <b>B が変わるだけでも鳴る</b>。Watch = false はそれを絞るだけの句にする。
    /// </para>
    ///
    /// <para>
    /// <b>B をいくら変えても鳴らないこと</b>がこの検査の主眼である。
    /// </para>
    /// </summary>
    [Fact]
    public async Task AnUnwatchedClause_NarrowsWithoutFiring()
    {
        (TestTargetProcess a, TestTargetProcess b) = StartSideBySide("btnA", "btnB");
        using TestTargetProcess winForms = a;
        using TestTargetProcess wpf = b;

        TriggerDefinition recordedA = (await Recording.RecordAsync(a, "btnA")).PinToWindow(a);
        TriggerDefinition recordedB = (await Recording.RecordAsync(b, "btnB")).PinToWindow(b);

        var definition = new TriggerDefinition
        {
            Id = "guarded",
            Window = recordedA.Window,
            Locator = recordedA.Locator,
            On = TriggerOn.PropertyChanged,
            Expression = "ready && done",
            Clauses =
            [
                AsClause(recordedA, "ready", "ready"),
                AsClause(recordedB, "done", "done", watch: false),
            ],
        };

        // 先に B を満たしておく (絞る側は先に成立していてよい)
        b.Send("set-text btnB done");
        await Task.Delay(BetweenChanges, TestContext.Current.CancellationToken);

        await using var harness = new MonitorHarness();
        await harness.StartAsync(definition);
        harness.WaitForResolution(resolved: true);

        // 絞るだけの側をいくら動かしても鳴らない。購読していないのだから当然だが、
        // Watch の判定が抜けるとここが鳴りはじめる
        b.Send("set-text btnB done-2");
        await Task.Delay(BetweenChanges, TestContext.Current.CancellationToken);
        b.Send("set-text btnB done");
        await Task.Delay(BetweenChanges, TestContext.Current.CancellationToken);
        harness.AssertNoFire(NoFireWindow);

        // 発火源の側が動いたときだけ鳴る
        a.Send("set-text btnA ready");
        harness.WaitForFire("guarded");
    }

    /// <summary>
    /// <b>式が短絡して読まなかった句は、そう報告されること</b> (docs/DESIGN.md §4)。
    ///
    /// <para>
    /// <c>Expression</c> の doc は「短絡するので、演算子の評価されない側の句は読まれない」と
    /// 公開済みの約束にしている。<see cref="ClauseOutcome.NotEvaluated"/> はそれを
    /// <b>ホストから観測できる形にしたもの</b>である —
    /// 「読んだが成立しなかった」と「読んでいない」を混ぜないために別の状態にしてある。
    /// </para>
    /// <para>
    /// <c>a || b</c> で a が成立すれば b は読まれない。<b>この形でしか観測できない</b> —
    /// 値は「最後に読めた値」ではなく「その周に読んだかどうか」で決まるので、
    /// スナップショットを見ても区別できない。
    /// </para>
    /// <para>
    /// 退行の形: <c>Read</c> の記録を「評価の前に全句へ入れる」ように変えると、
    /// b が <see cref="ClauseOutcome.NotMatched"/> として報告され、ここが落ちる。
    /// </para>
    /// </summary>
    [Fact]
    public async Task AShortCircuitedClause_IsReportedAsNotEvaluated()
    {
        (TestTargetProcess a, TestTargetProcess b) = StartSideBySide("btnA", "btnB");
        using TestTargetProcess winForms = a;
        using TestTargetProcess wpf = b;

        TriggerDefinition recordedA = (await Recording.RecordAsync(a, "btnA")).PinToWindow(a);
        TriggerDefinition recordedB = (await Recording.RecordAsync(b, "btnB")).PinToWindow(b);

        var definition = new TriggerDefinition
        {
            Id = "either",
            Window = recordedA.Window,
            Locator = recordedA.Locator,
            On = TriggerOn.PropertyChanged,
            // a が成立すれば b は読まれない
            Expression = "ready || done",
            Clauses = [AsClause(recordedA, "ready", "ready"), AsClause(recordedB, "done", "done")],
        };

        await using var harness = new MonitorHarness();
        await harness.StartAsync(definition);
        harness.WaitForResolution(resolved: true);

        a.Send("set-text btnA ready");
        TriggerFiredEventArgs fired = harness.WaitForFire("either");

        Assert.Equal(ClauseOutcome.Matched, fired.Clauses[0].Outcome);
        Assert.Equal("ready", fired.Clauses[0].Value.Value);
        // 読んでいないのだから値も無い。<b>「空文字だった」ではない</b>
        Assert.Equal(ClauseOutcome.NotEvaluated, fired.Clauses[1].Outcome);
        Assert.False(fired.Clauses[1].Value.HasValue);
    }

    /// <summary>
    /// <b>ファイルから読み直した</b>定義で、同じ要素を指す 2 つの句の発火が 1 回であること。
    ///
    /// <para>
    /// <see cref="WindowIdentity"/> と <see cref="ElementLocator"/> は <c>Equals</c> を持たない
    /// 可変クラスなので、JSON から読むと「同じ要素を指す 2 つの句」は<b>別オブジェクト</b>になる。
    /// 参照でまとめようとすると 1 要素にプロパティハンドラが 2 本立ち、
    /// <b>1 回の変化で 2 回鳴る</b>。
    /// </para>
    ///
    /// <para>
    /// <b>句を手で組み立てて同じインスタンスを使い回すと、この誤りは絶対に落ちない。</b>
    /// だからここは往復させてから使う — 利用者がやるのはまさにそれである。
    /// </para>
    ///
    /// <para>
    /// 直接見るのは<b>解決する要素の数</b>である。二重発火はその帰結だが、
    /// 同じ要素に 2 本立てたハンドラを UIA がどう配送するかはこちらの管轄ではなく、
    /// 「鳴らなかったこと」だけを根拠にすると検出力が UIA の都合に左右される。
    /// </para>
    /// </summary>
    [Fact]
    public async Task TwoClausesOnTheSameElement_LoadedFromJson_FireOnce()
    {
        using var target = TestTargetProcess.Start(TargetProfile.WinForms);
        target.Send("add-button btnTarget");

        TriggerDefinition recorded = (await Recording.RecordAsync(target, "btnTarget")).PinToWindow(target);

        var authored = new TriggerDefinition
        {
            Id = "same",
            Window = recorded.Window,
            Locator = recorded.Locator,
            On = TriggerOn.PropertyChanged,
            // 同じ要素・同じプロパティを 2 つの句で見る。まとまっていれば購読は 1 本
            Expression = "one && two",
            Clauses =
            [
                new PropertyClause
                {
                    Name = "one",
                    Window = recorded.Window,
                    Locator = recorded.Locator,
                    Property = TriggerProperty.Name,
                    Op = ComparisonOp.Equals,
                    Text = "ready",
                },
                new PropertyClause
                {
                    Name = "two",
                    Window = recorded.Window,
                    Locator = recorded.Locator,
                    Property = TriggerProperty.Name,
                    Op = ComparisonOp.RegexMatch,
                    Text = "^r",
                },
            ],
        };

        // ここが要点。往復させると 2 つの句の Window / Locator は別オブジェクトになる
        string json = JsonSerializer.Serialize(authored, TriggerJsonContext.Default.TriggerDefinition);
        TriggerDefinition definition = JsonSerializer.Deserialize(json, TriggerJsonContext.Default.TriggerDefinition)!;
        Assert.NotSame(definition.Clauses[0].Window, definition.Clauses[1].Window);
        Assert.NotSame(definition.Clauses[0].Locator, definition.Clauses[1].Locator);

        await using var harness = new MonitorHarness();
        await harness.StartAsync(definition);
        harness.WaitForResolution(resolved: true);

        // 別オブジェクトでも同じ要素は 1 つに畳まれていること
        TriggerMonitorDiagnostics diagnostics = harness.Monitor.GetDiagnostics();
        Assert.True(
            diagnostics.ElementSlotCount == 1,
            $"同じ要素を指す 2 句が {diagnostics.ElementSlotCount} つの要素に割れています。" +
            $"1 要素にプロパティ購読が 2 本立ちます。{Environment.NewLine}{harness.History()}");
        Assert.Equal(1, diagnostics.WatchedPropertyCount);

        target.Send("set-text btnTarget ready");
        harness.WaitForFire("same");

        // 帰結の側も見ておく。2 本目のハンドラがあれば、同じ 1 回の変化にもう 1 件届きうる
        harness.AssertNoFire(NoFireWindow);
    }
}
