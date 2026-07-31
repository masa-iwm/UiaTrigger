using UiaTrigger.Models;
using UiaTrigger.Resolution;
using Xunit;

namespace UiaTrigger.Tests;

/// <summary>
/// 経路解決の回帰テスト。
///
/// 解決結果に加えて **往復回数** (docs/DESIGN.md B1) と **RCW の解放** (B6) も固定する。
/// 探索アルゴリズムそのものの回帰は <see cref="BeamSearchTests"/> にある。
/// </summary>
public sealed class ElementResolverTests
{
    private const int Button = 50000;
    private const int Pane = 50033;
    private const int WindowType = 50032;
    private const nint Hwnd = 0x1234;

    private static readonly ResolverOptions Options = new();

    private static FakeElement Leaf(string tag, int controlType, string automationId = "", string name = "") =>
        new() { Tag = tag, ControlType = controlType, AutomationId = automationId, Name = name, ProcessId = 42 };

    /// <summary>window → pane → button(3 兄弟中の 1 番目) という木を用意する。</summary>
    private static (FakeElementTree Tree, WindowCandidateCache Windows) Fixture(out FakeWindowSource source)
    {
        var target = Leaf("target", Button, automationId: "okButton", name: "OK");
        var pane = Leaf("pane", Pane).WithChildren(
            Leaf("sibling0", Button, automationId: "cancelButton", name: "Cancel"),
            target,
            Leaf("sibling2", Button, automationId: "helpButton", name: "Help"));
        var window = Leaf("window", WindowType).WithChildren(Leaf("titlebar", 50037), pane);

        var tree = new FakeElementTree(new Dictionary<nint, FakeElement> { [Hwnd] = window });
        source = new FakeWindowSource(new WindowInfo(Hwnd, 42, "TestClass", "Test Window"))
            .WithProcess(42, @"C:\apps\test.exe");
        return (tree, new WindowCandidateCache(source, Options));
    }

    private static TriggerDefinition Definition() => new()
    {
        Id = "test",
        Window = new WindowIdentity { ProcessName = "test.exe", ClassName = "TestClass" },
        Locator = new ElementLocator
        {
            View = TreeViewMode.Control,
            Steps =
            [
                new ElementPathStep { ControlType = Pane, SiblingIndex = 1 },
                new ElementPathStep { ControlType = Button, AutomationId = "okButton", Name = "OK", SiblingIndex = 1 },
            ],
        },
    };

    [Fact]
    public void Resolve_FindsTheRecordedElement()
    {
        (FakeElementTree tree, WindowCandidateCache windows) = Fixture(out _);

        ResolvedTarget? target = ResolverTestHelp.ResolveWith(tree, windows, Definition(), Options);

        Assert.NotNull(target);
        Assert.Equal("target", FakeElementTree.TagOf(target.Element));
        Assert.Equal(Hwnd, target.WindowHandle);
    }

    /// <summary>
    /// B3 のためにウィンドウ〜対象の親までの経路が返ること。
    /// StructureChanged をこの経路だけに絞るのに使う。
    /// </summary>
    [Fact]
    public void Resolve_ReturnsThePathFromTheWindowDownToTheParent()
    {
        (FakeElementTree tree, WindowCandidateCache windows) = Fixture(out _);

        ResolvedTarget? target = ResolverTestHelp.ResolveWith(tree, windows, Definition(), Options);

        Assert.NotNull(target);
        Assert.Equal(["window", "pane"], target.Ancestors.Select(FakeElementTree.TagOf));
        Assert.Equal("window", FakeElementTree.TagOf(target.WindowElement));
    }

    /// <summary>
    /// B1: 子の列挙は「候補 1 件につき 1 往復」であること。
    ///
    /// ビーム探索は段ごとに複数の候補を残しうるので「段あたり 1 往復」ではなくなったが、
    /// 同じ親を 2 度列挙しないことは変わらない。ここでは各段の候補が一意に決まるため、
    /// 往復は段数と一致する。ビームが広がる場合の上限は
    /// <see cref="BeamSearchTests.Resolve_NeverFetchesMoreThanBeamWidthTimesPerLevel"/>。
    /// </summary>
    [Fact]
    public void Resolve_FetchesChildrenOncePerStep()
    {
        (FakeElementTree tree, WindowCandidateCache windows) = Fixture(out _);
        TriggerDefinition definition = Definition();

        ResolverTestHelp.ResolveWith(tree, windows, definition, Options);

        Assert.Equal(definition.Locator.Steps.Count, tree.GetChildrenCallCount);
    }

    /// <summary>B6: 採られなかった候補はその場で解放し、経路上のノードは保持すること。</summary>
    [Fact]
    public void Resolve_ReleasesEveryCandidateItDidNotKeep()
    {
        (FakeElementTree tree, WindowCandidateCache windows) = Fixture(out _);

        ResolvedTarget? target = ResolverTestHelp.ResolveWith(tree, windows, Definition(), Options);

        Assert.NotNull(target);
        var retained = tree.Retained.Select(FakeElementTree.TagOf).Order(StringComparer.Ordinal);
        Assert.Equal(["pane", "target", "window"], retained);
    }

    /// <summary>解決に失敗した経路は、掴んだノードを 1 つも残さないこと。</summary>
    [Fact]
    public void Resolve_WhenAStepFails_ReleasesEverythingItTook()
    {
        (FakeElementTree tree, WindowCandidateCache windows) = Fixture(out _);
        TriggerDefinition definition = Definition();
        // 最終段の手掛かりをすべて別物にする。どの兄弟も証拠が負になり候補が残らない
        definition.Locator.Steps[1].ControlType = 50004;   // Edit
        definition.Locator.Steps[1].AutomationId = "nothingLikeThis";
        definition.Locator.Steps[1].Name = "nothingLikeThis";

        ResolvedTarget? target = ResolverTestHelp.ResolveWith(tree, windows, definition, Options);

        Assert.Null(target);
        Assert.Empty(tree.Retained);
    }

    /// <summary>ウィンドウ候補が 1 つも無ければツリーには一切触れないこと。</summary>
    [Fact]
    public void Resolve_WithNoMatchingWindow_DoesNotTouchTheTree()
    {
        (FakeElementTree tree, WindowCandidateCache windows) = Fixture(out _);
        TriggerDefinition definition = Definition();
        definition.Window.ProcessName = "other.exe";

        Assert.Null(ResolverTestHelp.ResolveWith(tree, windows, definition, Options));
        Assert.Equal(0, tree.GetWindowCallCount);
        Assert.Equal(0, tree.GetChildrenCallCount);
    }

    /// <summary>経路が空なら対象はウィンドウ要素そのものであること。</summary>
    [Fact]
    public void Resolve_WithAnEmptyPath_TargetsTheWindowItself()
    {
        (FakeElementTree tree, WindowCandidateCache windows) = Fixture(out _);
        TriggerDefinition definition = Definition();
        definition.Locator.Steps.Clear();

        ResolvedTarget? target = ResolverTestHelp.ResolveWith(tree, windows, definition, Options);

        Assert.NotNull(target);
        Assert.Equal("window", FakeElementTree.TagOf(target.Element));
        Assert.Empty(target.Ancestors);
        Assert.Equal(0, tree.GetChildrenCallCount);
    }
}
