using UiaTrigger.Models;
using UiaTrigger.Resolution;
using Xunit;

namespace UiaTrigger.Tests;

/// <summary>
/// 要素識別 (ビーム探索) の回帰テスト (docs/DESIGN.md §3 / A3〜A7)。
///
/// 段ごとにスコアを合計して閾値と比べ、1 位を貪欲に選ぶ形にしてはならない (docs/DESIGN.md §3)。
/// ここで固定するのは、それを避ける 4 つの性質である:
/// <list type="number">
/// <item>兄弟が増減しても解決できる (A3) — 兄弟インデックスはタイブレークだけに使う</item>
/// <item>ControlType が変わっても、他に強い証拠があれば追随する (A5)</item>
/// <item>1 位が行き止まりなら 2 位以降を試す (A6)</item>
/// <item>手掛かりが無い段の記録は「不明 (-1)」であり、0 と区別される (A7)</item>
/// </list>
/// COM を一切使わないので、これらはすべて CI で常時回る。
/// </summary>
public sealed class BeamSearchTests
{
    private const int Button = 50000;
    private const int Edit = 50004;
    private const int Text = 50020;
    private const int Pane = 50033;
    private const int WindowType = 50032;
    private const nint Hwnd = 0x1234;

    private static readonly ResolverOptions Options = new();

    private static FakeElement Node(
        string tag, int controlType, string automationId = "", string name = "", string className = "") =>
        new()
        {
            Tag = tag,
            ControlType = controlType,
            AutomationId = automationId,
            Name = name,
            ClassName = className,
            ProcessId = 42,
        };

    private static (FakeElementTree Tree, WindowCandidateCache Windows) Fixture(FakeElement window)
    {
        var tree = new FakeElementTree(new Dictionary<nint, FakeElement> { [Hwnd] = window });
        var source = new FakeWindowSource(new WindowInfo(Hwnd, 42, "TestClass", "Test Window"))
            .WithProcess(42, @"C:\apps\test.exe");
        return (tree, new WindowCandidateCache(source, Options));
    }

    private static TriggerDefinition Definition(params ElementPathStep[] steps) => new()
    {
        Id = "test",
        Window = new WindowIdentity { ProcessName = "test.exe", ClassName = "TestClass" },
        Locator = new ElementLocator { View = TreeViewMode.Control, Steps = [.. steps] },
    };

    private static string? Resolve(FakeElement window, TriggerDefinition definition, ResolverOptions? options = null)
    {
        (FakeElementTree tree, WindowCandidateCache windows) = Fixture(window);
        ResolvedTarget? target = ResolverTestHelp.ResolveWith(tree, windows, definition, options ?? Options);
        if (target is null)
        {
            // 失敗経路では 1 つも掴んだままにしない (docs/DESIGN.md B6)
            Assert.Empty(tree.Retained);
            return null;
        }
        return FakeElementTree.TagOf(target.Element);
    }

    // ---------- A3: 兄弟の増減 ----------

    /// <summary>
    /// A3 そのもの。AutomationId も Name も無い汎用 Pane だけの経路で、
    /// 兄弟が 1 個挿入されても解決できること。
    ///
    /// スコア合計 + 閾値の形では、手掛かりの薄い Pane は index が 1 ずれただけで閾値を割り、
    /// **経路全体が失敗**する。
    /// </summary>
    [Fact]
    public void Resolve_FollowsAGenericPaneWhenASiblingIsInsertedBefore()
    {
        var window = Node("window", WindowType).WithChildren(
            Node("inserted", Pane),
            Node("target", Pane).WithChildren(Node("leaf", Text)));

        // 記録時は index 0 だった
        string? tag = Resolve(window, Definition(
            new ElementPathStep { ControlType = Pane, SiblingIndex = 0 },
            new ElementPathStep { ControlType = Text, SiblingIndex = 0 }));

        Assert.Equal("leaf", tag);
    }

    /// <summary>
    /// 兄弟インデックスは **タイブレークにのみ** 効くこと。
    /// 他の手掛かりが同点の 2 つの Pane があるなら、記録された index に近いほうを採る。
    /// </summary>
    [Fact]
    public void Resolve_UsesTheSiblingIndexOnlyToBreakTies()
    {
        var window = Node("window", WindowType).WithChildren(
            Node("pane0", Pane), Node("pane1", Pane), Node("pane2", Pane));

        Assert.Equal("pane2", Resolve(window, Definition(new ElementPathStep { ControlType = Pane, SiblingIndex = 2 })));
        Assert.Equal("pane0", Resolve(window, Definition(new ElementPathStep { ControlType = Pane, SiblingIndex = 0 })));
    }

    /// <summary>
    /// 一方でインデックスは **スコアではない**。
    /// 記録された index の要素より、記録された Name に一致する要素のほうが優先されること。
    /// </summary>
    [Fact]
    public void Resolve_PrefersMatchingAttributesOverTheRecordedIndex()
    {
        var window = Node("window", WindowType).WithChildren(
            Node("wrongName", Pane, name: "Other"),
            Node("rightName", Pane, name: "Content"));

        string? tag = Resolve(window, Definition(
            new ElementPathStep { ControlType = Pane, Name = "Content", SiblingIndex = 0 }));

        Assert.Equal("rightName", tag);
    }

    // ---------- A5: ControlType の変化 ----------

    /// <summary>
    /// A5。ControlType が Text → Edit に変わっても、AutomationId が一致していれば追随すること。
    /// ControlType 不一致を即除外にすると、この要素は永久に見失われる。
    /// </summary>
    [Fact]
    public void Resolve_FollowsAnElementWhoseControlTypeChanged()
    {
        var window = Node("window", WindowType).WithChildren(
            Node("other", Text, automationId: "somethingElse"),
            Node("target", Edit, automationId: "editableLabel"));

        string? tag = Resolve(window, Definition(
            new ElementPathStep { ControlType = Text, AutomationId = "editableLabel", SiblingIndex = 1 }));

        Assert.Equal("target", tag);
    }

    /// <summary>
    /// 一方で「何でも解決してしまう」わけではないこと (ネガティブコントロール)。
    /// ControlType しか記録が無い段でその ControlType が一致しなければ、
    /// その要素を指す証拠は 1 つも残らないので解決は失敗する。
    /// </summary>
    [Fact]
    public void Resolve_FailsWhenTheOnlyRecordedAttributeIsAControlTypeThatChanged()
    {
        var window = Node("window", WindowType).WithChildren(Node("pane", Pane).WithChildren(Node("leaf", Text)));

        Assert.Null(Resolve(window, Definition(
            new ElementPathStep { ControlType = Pane, SiblingIndex = 0 },
            new ElementPathStep { ControlType = Button, SiblingIndex = 0 })));
    }

    /// <summary>AutomationId が別物なら、他が全部合っていても採らないこと。</summary>
    [Fact]
    public void Resolve_RejectsACandidateWithADifferentAutomationId()
    {
        var window = Node("window", WindowType).WithChildren(Node("impostor", Button, automationId: "cancelButton", name: "OK"));

        Assert.Null(Resolve(window, Definition(
            new ElementPathStep { ControlType = Button, AutomationId = "okButton", Name = "OK", SiblingIndex = 0 })));
    }

    // ---------- A6: バックトラック ----------

    /// <summary>
    /// A6 そのもの。第 1 段のスコア最良の候補が行き止まりでも、2 位を辿って解決すること。
    ///
    /// 木は「Name が完全一致する Pane (だが子を持たない)」と
    /// 「Name が部分一致するだけの Pane (こちらに目的の子がある)」。
    /// 貪欲に 1 位だけを選ぶと前者に入って失敗する。
    /// </summary>
    [Fact]
    public void Resolve_BacktracksOutOfADeadEnd()
    {
        var window = Node("window", WindowType).WithChildren(
            Node("deadEnd", Pane, name: "Document"),
            Node("realParent", Pane, name: "Document view").WithChildren(
                Node("target", Button, automationId: "okButton")));

        string? tag = Resolve(window, Definition(
            new ElementPathStep { ControlType = Pane, Name = "Document", SiblingIndex = 0 },
            new ElementPathStep { ControlType = Button, AutomationId = "okButton", SiblingIndex = 0 }));

        Assert.Equal("target", tag);
    }

    /// <summary>
    /// ビーム幅を 1 に絞ると、上のケースは失敗すること。
    /// バックトラックしているのがビーム幅であることの確認 (幅 1 = 貪欲選択)。
    /// </summary>
    [Fact]
    public void Resolve_WithBeamWidthOne_CannotBacktrack()
    {
        var window = Node("window", WindowType).WithChildren(
            Node("deadEnd", Pane, name: "Document"),
            Node("realParent", Pane, name: "Document view").WithChildren(
                Node("target", Button, automationId: "okButton")));

        string? tag = Resolve(
            window,
            Definition(
                new ElementPathStep { ControlType = Pane, Name = "Document", SiblingIndex = 0 },
                new ElementPathStep { ControlType = Button, AutomationId = "okButton", SiblingIndex = 0 }),
            new ResolverOptions { BeamWidth = 1 });

        Assert.Null(tag);
    }

    /// <summary>往復回数の上限は「段あたりビーム幅」であること (バックトラックの対価)。</summary>
    [Fact]
    public void Resolve_NeverFetchesMoreThanBeamWidthTimesPerLevel()
    {
        var children = new List<FakeElement>();
        for (int i = 0; i < 8; i++)
        {
            // 全部同点にしてビームを最大まで広げる
            children.Add(Node($"pane{i}", Pane).WithChildren(Node($"leaf{i}", Text)));
        }
        var window = Node("window", WindowType).WithChildren([.. children]);

        (FakeElementTree tree, WindowCandidateCache windows) = Fixture(window);
        var options = new ResolverOptions { BeamWidth = 3 };
        TriggerDefinition definition = Definition(
            new ElementPathStep { ControlType = Pane, SiblingIndex = 0 },
            new ElementPathStep { ControlType = Text, SiblingIndex = 0 });

        ResolverTestHelp.ResolveWith(tree, windows, definition, options);

        // 1 段目: 親 1 個 → 1 往復 / 2 段目: 生き残り 3 個 → 3 往復
        Assert.Equal(4, tree.GetChildrenCallCount);
    }

    /// <summary>
    /// 探索の途中で要素が消えても、そこまでに掴んだノードを 1 つも残さないこと。
    ///
    /// ビームが複数あると、1 つ目の親の子から候補を採った**あと**に 2 つ目の親で落ちうる。
    /// その時点の候補はまだ「生き残りの記録」に入っていないので、素直に書くと取りこぼす。
    /// </summary>
    [Fact]
    public void Resolve_WhenTheTreeDiesMidSearch_ReleasesEverythingItTook()
    {
        var window = Node("window", WindowType).WithChildren(
            Node("pane0", Pane).WithChildren(Node("leaf0", Text)),
            Node("pane1", Pane).WithChildren(Node("leaf1", Text)));

        (FakeElementTree tree, WindowCandidateCache windows) = Fixture(window);
        // 1 回目 = 1 段目 / 2 回目 = 2 段目の pane0 (leaf0 を候補に採る) / 3 回目 = pane1 でここで消える
        tree.ThrowOnGetChildrenCall = 3;

        ResolvedTarget? target = ResolverTestHelp.ResolveWith(tree, windows, Definition(
            new ElementPathStep { ControlType = Pane, SiblingIndex = 0 },
            new ElementPathStep { ControlType = Text, SiblingIndex = 0 }), Options);

        Assert.Null(target);
        Assert.Empty(tree.Retained);
    }

    // ---------- A7: 兄弟インデックス「不明」 ----------

    /// <summary>
    /// A7。記録時に兄弟が見つからなかった段は -1 (不明) であり、
    /// 「0 番目にあった」という誤った手掛かりとして働かないこと。
    /// </summary>
    [Fact]
    public void Resolve_WithAnUnknownSiblingIndex_DoesNotFavourTheFirstChild()
    {
        var window = Node("window", WindowType).WithChildren(
            Node("pane0", Pane), Node("pane1", Pane), Node("pane2", Pane));

        // 不明 (-1): 全候補が等距離になるので、同点なら発見順 = 先頭
        Assert.Equal("pane0", Resolve(window, Definition(
            new ElementPathStep { ControlType = Pane, SiblingIndex = ElementPathStep.UnknownSiblingIndex })));

        // 明示された 2: タイブレークが効いて pane2
        Assert.Equal("pane2", Resolve(window, Definition(
            new ElementPathStep { ControlType = Pane, SiblingIndex = 2 })));
    }

    /// <summary>既定値が「不明」であること (0 を既定にすると誤った手掛かりが自動的に入る)。</summary>
    [Fact]
    public void ElementPathStep_DefaultsToAnUnknownSiblingIndex()
    {
        Assert.Equal(ElementPathStep.UnknownSiblingIndex, new ElementPathStep().SiblingIndex);
        Assert.Equal(-1, ElementPathStep.UnknownSiblingIndex);
    }

    // ---------- ClassName ----------

    /// <summary>
    /// ClassName が記録されていれば識別に効くこと。
    /// Win32 では極めて安定しており、AutomationId も Name も無い段では唯一の手掛かりになる。
    /// </summary>
    [Fact]
    public void Resolve_UsesTheRecordedClassName()
    {
        var window = Node("window", WindowType).WithChildren(
            Node("toolbar", Pane, className: "ToolbarWindow32"),
            Node("status", Pane, className: "msctls_statusbar32"));

        string? tag = Resolve(window, Definition(
            new ElementPathStep { ControlType = Pane, ClassName = "msctls_statusbar32", SiblingIndex = 0 }));

        Assert.Equal("status", tag);
    }

    // ---------- スコアの意味 ----------

    /// <summary>
    /// スコアは「記録された属性がその候補を肯定する度合い」であること。
    /// 記録されていない属性は加点も減点もしない (無記録の段はどの候補も 0 点で並ぶ)。
    /// </summary>
    [Fact]
    public void ScoreStep_IgnoresAttributesThatWereNotRecorded()
    {
        var candidate = new FakeElement { ControlType = Button, AutomationId = "x", Name = "y", ClassName = "z" };

        Assert.Equal(0, ElementResolver.ScoreStep(new ElementPathStep(), candidate, Options));
    }

    /// <summary>
    /// AutomationId 一致は ControlType 不一致を上回ること (A5 が成り立つための重み関係)。
    /// この不等式が崩れると「型が変わったら追えない」に逆戻りする。
    /// </summary>
    [Fact]
    public void ScoreStep_AutomationIdMatchOutweighsAControlTypeMismatch()
    {
        Assert.True(Options.StepAutomationIdScore + Options.StepControlTypeMismatchPenalty >= Options.StepAcceptScore);
    }

    /// <summary>
    /// **ControlType 一致は ClassName 不一致を上回ること** (A4 を段レベルでも成立させる重み関係)。
    ///
    /// <para>
    /// WinForms のクラス名は起動ごとに変わる token を含む (A4 の実測)。AutomationId も Name も
    /// 無い段 (実行時生成の Pane / Group) で ClassName の減点が ControlType の加点を上回ると、
    /// 再起動後に**その段の全候補が足切りされ経路が恒久的に解決不能**になる — A4 が
    /// ウィンドウレベルで取り除いたのと同じ失敗である。例外もログも出ない。
    /// </para>
    /// </summary>
    [Fact]
    public void ScoreStep_AControlTypeMatchOutweighsAClassNameMismatch()
    {
        Assert.True(Options.StepControlTypeScore + Options.StepClassNameMismatchPenalty >= Options.StepAcceptScore);
    }

    /// <summary>
    /// 上の不等式を、実際の解決で確かめる対 (重みを直接見るテストの検出力の裏付け)。
    /// クラス名が起動ごとに変わった段でも、型と経路の形で追えること。
    /// </summary>
    [Fact]
    public void Resolve_AfterAClassNameTokenChanged_StillFindsTheStep()
    {
        // 記録時のクラス名は WindowsForms10.Window.8.app.0.34f5582_r6_ad1 のような形。
        // 再起動でこの token が変わる
        var window = Node("window", WindowType).WithChildren(
            Node("pane", Pane, className: "WindowsForms10.Window.8.app.0.NEWTOKEN"));

        string? tag = Resolve(window, Definition(
            new ElementPathStep
            {
                ControlType = Pane,
                ClassName = "WindowsForms10.Window.8.app.0.OLDTOKEN",
                SiblingIndex = 0,
            }));

        Assert.Equal("pane", tag);
    }

    /// <summary>
    /// **記録側の走査上限は解決側の候補上限を超えないこと** (docs/DESIGN.md A30)。
    /// 超えると「記録できたのに解決側の候補集合に入らない」段ができ、その定義は
    /// エラーを出さずに別の要素へ解決される (兄弟インデックスの手掛かりが効かないため)。
    /// </summary>
    [Fact]
    public void RecordingScansNoFurtherThanResolutionLooks()
    {
        Assert.True(Options.RecordingSiblingScan <= Options.MaxChildrenPerLevel);
    }
}
