using UiaTrigger.Models;
using UiaTrigger.Resolution;
using Xunit;

namespace UiaTrigger.Tests;

/// <summary>
/// <see cref="ElementSearch"/> — 一意な <c>AutomationId</c> で対象を直接引き当てる方式の回帰
/// (docs/DESIGN.md §3 の Search 方式)。
///
/// <para>
/// <c>FindFirst</c> は要素しか返さないが、B3 の経路購読は
/// 「ウィンドウから対象の親までの各段」を要求する。ここで固定するのはその両方:
/// 直接引き当てられること **と**、経路購読に使える祖先鎖が同じ形で返ること。
/// </para>
///
/// <para>
/// 一意でなくなったときに黙って別の要素を掴まないこと (= 経路方式へ落ちること) が
/// もっとも壊れやすく、かつ壊れても例外にならない性質なので、対で押さえている。
/// </para>
/// </summary>
public sealed class SearchLocatorTests
{
    private const int Button = 50000;
    private const int Pane = 50033;
    private const int WindowType = 50032;
    private const nint Hwnd = 0x1234;

    private static readonly ResolverOptions Options = new();

    private static FakeElement Leaf(string tag, int controlType, string automationId = "", string name = "") =>
        new() { Tag = tag, ControlType = controlType, AutomationId = automationId, Name = name, ProcessId = 42 };

    /// <summary>window → pane → button の木。対象の AutomationId は木の中で一意。</summary>
    private static (FakeElementTree Tree, WindowCandidateCache Windows) Fixture(string duplicateOf = "")
    {
        var target = Leaf("target", Button, automationId: "okButton", name: "OK");
        var pane = Leaf("pane", Pane).WithChildren(
            Leaf("sibling0", Button, automationId: "cancelButton", name: "Cancel"),
            target,
            Leaf("sibling2", Button, automationId: duplicateOf, name: "Help"));
        var window = Leaf("window", WindowType).WithChildren(Leaf("titlebar", 50037), pane);

        var tree = new FakeElementTree(new Dictionary<nint, FakeElement> { [Hwnd] = window });
        var source = new FakeWindowSource(new WindowInfo(Hwnd, 42, "TestClass", "Test Window"))
            .WithProcess(42, @"C:\apps\test.exe");
        return (tree, new WindowCandidateCache(source, Options));
    }

    /// <summary>
    /// Search と経路の両方を持つ定義。既定では Search が使われ、経路は退避路として残る。
    /// <paramref name="brokenPath"/> を立てると経路側の手掛かりを全部別物にする。
    /// </summary>
    private static TriggerDefinition Definition(bool withSearch = true, bool brokenPath = false) => new()
    {
        Id = "test",
        Window = new WindowIdentity { ProcessName = "test.exe", ClassName = "TestClass" },
        Locator = new ElementLocator
        {
            View = TreeViewMode.Control,
            Search = withSearch ? new ElementSearch { AutomationId = "okButton" } : null,
            Steps =
            [
                new ElementPathStep { ControlType = brokenPath ? 50004 : Pane, SiblingIndex = 1 },
                new ElementPathStep
                {
                    ControlType = brokenPath ? 50004 : Button,
                    AutomationId = brokenPath ? "nothingLikeThis" : "okButton",
                    Name = brokenPath ? "nothingLikeThis" : "OK",
                    SiblingIndex = 1,
                },
            ],
        },
    };

    [Fact]
    public void Resolve_WithAUniqueAutomationId_UsesTheSearch()
    {
        (FakeElementTree tree, WindowCandidateCache windows) = Fixture();

        ResolvedTarget? target = ResolverTestHelp.ResolveWith(tree, windows, Definition(), Options);

        Assert.NotNull(target);
        Assert.True(target.FoundBySearch);
        Assert.Equal("target", FakeElementTree.TagOf(target.Element));
        // 子の段階列挙は 1 回も走らない。ここが 0 でないなら経路方式に落ちている
        Assert.Equal(0, tree.GetChildrenCallCount);
        Assert.Equal(1, tree.FindCallCount);
    }

    /// <summary>
    /// 経路購読 (B3) 用の祖先鎖が、経路方式とまったく同じ形で返ること。
    /// これが返らなければ Search 方式は使えない — 購読はウィンドウ全体の Subtree に
    /// 戻すしかなくなり、B3 で得たものを捨てることになる。
    /// </summary>
    [Fact]
    public void Resolve_ViaTheSearch_ReturnsTheSameAncestorChainAsThePath()
    {
        (FakeElementTree searchTree, WindowCandidateCache searchWindows) = Fixture();
        (FakeElementTree pathTree, WindowCandidateCache pathWindows) = Fixture();

        ResolvedTarget? bySearch = ResolverTestHelp.ResolveWith(searchTree, searchWindows, Definition(), Options);
        ResolvedTarget? byPath = ResolverTestHelp.ResolveWith(pathTree, pathWindows, Definition(withSearch: false), Options);

        Assert.NotNull(bySearch);
        Assert.NotNull(byPath);
        Assert.False(byPath.FoundBySearch);
        Assert.Equal(["window", "pane"], bySearch.Ancestors.Select(FakeElementTree.TagOf));
        Assert.Equal(
            byPath.Ancestors.Select(FakeElementTree.TagOf),
            bySearch.Ancestors.Select(FakeElementTree.TagOf));
        Assert.Equal("window", FakeElementTree.TagOf(bySearch.WindowElement));
    }

    /// <summary>
    /// Search で見つけたときも、掴んだままにするのは経路上のノードだけであること (B6)。
    /// 祖先を遡る過程で作ったノードは重複するので、放っておくと解放漏れになる。
    /// </summary>
    [Fact]
    public void Resolve_ViaTheSearch_ReleasesEverythingItDidNotKeep()
    {
        (FakeElementTree tree, WindowCandidateCache windows) = Fixture();

        ResolvedTarget? target = ResolverTestHelp.ResolveWith(tree, windows, Definition(), Options);

        Assert.NotNull(target);
        var retained = tree.Retained.Select(FakeElementTree.TagOf).Order(StringComparer.Ordinal);
        Assert.Equal(["pane", "target", "window"], retained);
    }

    /// <summary>
    /// Search 方式は経路がまるごと壊れていても解決できること。
    /// 「途中の段が変わっても影響を受けない」という Search の唯一の利点そのものである。
    /// </summary>
    [Fact]
    public void Resolve_WithABrokenPath_StillSucceedsThroughTheSearch()
    {
        (FakeElementTree tree, WindowCandidateCache windows) = Fixture();

        ResolvedTarget? target = ResolverTestHelp.ResolveWith(tree, windows, Definition(brokenPath: true), Options);

        Assert.NotNull(target);
        Assert.True(target.FoundBySearch);
        Assert.Equal("target", FakeElementTree.TagOf(target.Element));
    }

    /// <summary>
    /// 上の対: Search を外せば同じ定義で解決できないこと。
    /// これが無いと「壊れた経路でも解決できた」のが Search のおかげなのか、
    /// そもそも経路の壊し方が甘いだけなのかが区別できない。
    /// </summary>
    [Fact]
    public void Resolve_WithABrokenPathAndNoSearch_Fails()
    {
        (FakeElementTree tree, WindowCandidateCache windows) = Fixture();

        ResolvedTarget? target =
            ResolverTestHelp.ResolveWith(tree, windows, Definition(withSearch: false, brokenPath: true), Options);

        Assert.Null(target);
        Assert.Empty(tree.Retained);
    }

    /// <summary>
    /// 記録時は一意だった id が重複するようになったら、Search は使わず経路方式に落ちること。
    ///
    /// **先頭の一致を採ってはならない。**「2 件あるうちの片方」を黙って掴むのは、
    /// 例外にもならず記録とも食い違わない、最も見つけにくい壊れ方である。
    /// </summary>
    [Fact]
    public void Resolve_WhenTheAutomationIdIsNoLongerUnique_FallsBackToThePath()
    {
        (FakeElementTree tree, WindowCandidateCache windows) = Fixture(duplicateOf: "okButton");

        ResolvedTarget? target = ResolverTestHelp.ResolveWith(tree, windows, Definition(), Options);

        Assert.NotNull(target);
        Assert.False(target.FoundBySearch);
        // 経路方式は AutomationId 以外の手掛かり (Name / 兄弟インデックス) も持つので、
        // 重複した 2 件のうち記録した側を選び直せる
        Assert.Equal("target", FakeElementTree.TagOf(target.Element));
        Assert.Equal(Definition().Locator.Steps.Count, tree.GetChildrenCallCount);
    }

    /// <summary>一致が 0 件のとき (要素の id が変わった等) も経路方式に落ちること。</summary>
    [Fact]
    public void Resolve_WhenTheSearchMatchesNothing_FallsBackToThePath()
    {
        (FakeElementTree tree, WindowCandidateCache windows) = Fixture();
        TriggerDefinition definition = Definition();
        definition.Locator.Search!.AutomationId = "noSuchId";

        ResolvedTarget? target = ResolverTestHelp.ResolveWith(tree, windows, definition, Options);

        Assert.NotNull(target);
        Assert.False(target.FoundBySearch);
        Assert.Equal("target", FakeElementTree.TagOf(target.Element));
    }

    /// <summary>
    /// Search の途中で要素が消えても、掴んだものを残さず経路方式に落ちること。
    /// </summary>
    [Fact]
    public void Resolve_WhenTheSearchFails_LeavesNothingBehindAndFallsBackToThePath()
    {
        (FakeElementTree tree, WindowCandidateCache windows) = Fixture();
        tree.ThrowOnFindCall = 1;

        ResolvedTarget? target = ResolverTestHelp.ResolveWith(tree, windows, Definition(), Options);

        Assert.NotNull(target);
        Assert.False(target.FoundBySearch);
        var retained = tree.Retained.Select(FakeElementTree.TagOf).Order(StringComparer.Ordinal);
        Assert.Equal(["pane", "target", "window"], retained);
    }

    /// <summary>
    /// 対象がウィンドウ要素そのものだった場合 (Subtree の検索はスコープ自身を含む)、
    /// 経路は空で、ウィンドウノードが二重に掴まれないこと。
    /// </summary>
    [Fact]
    public void Resolve_WhenTheSearchHitsTheWindowItself_HasNoAncestors()
    {
        (_, WindowCandidateCache windows) = Fixture();
        TriggerDefinition definition = Definition();
        definition.Locator.Search!.AutomationId = "windowId";
        definition.Locator.Steps.Clear();
        // ウィンドウ要素そのものに id を与える
        var window = new FakeElement { Tag = "window", ControlType = WindowType, AutomationId = "windowId", ProcessId = 42 };
        var tree = new FakeElementTree(new Dictionary<nint, FakeElement> { [Hwnd] = window });

        ResolvedTarget? target = ResolverTestHelp.ResolveWith(tree, windows, definition, Options);

        Assert.NotNull(target);
        Assert.Equal("window", FakeElementTree.TagOf(target.Element));
        Assert.Empty(target.Ancestors);
        Assert.Single(tree.Retained);
        Assert.Equal(0, tree.GetParentCallCount);
    }

    /// <summary>
    /// 祖先を遡ってもウィンドウに行き着かない場合 (別ウィンドウの要素が引っかかった等) は
    /// 採らないこと。ウィンドウ照合を素通りする経路を作ってしまうため。
    /// </summary>
    [Fact]
    public void Resolve_WhenTheFoundElementIsNotUnderTheWindow_FallsBackToThePath()
    {
        // 親リンクを張らない孤立した要素を、ウィンドウの部分木に見せかけて置く
        var orphan = new FakeElement { Tag = "orphan", ControlType = Button, AutomationId = "okButton", ProcessId = 42 };
        var window = Leaf("window", WindowType);
        window.Children.Add(orphan); // WithChildren を使わない = Parent を張らない

        var tree = new FakeElementTree(new Dictionary<nint, FakeElement> { [Hwnd] = window });
        var source = new FakeWindowSource(new WindowInfo(Hwnd, 42, "TestClass", "Test Window"))
            .WithProcess(42, @"C:\apps\test.exe");
        var windows = new WindowCandidateCache(source, Options);

        var definition = new TriggerDefinition
        {
            Id = "test",
            Window = new WindowIdentity { ProcessName = "test.exe", ClassName = "TestClass" },
            Locator = new ElementLocator
            {
                Search = new ElementSearch { AutomationId = "okButton" },
                Steps = [new ElementPathStep { ControlType = Button, AutomationId = "okButton", SiblingIndex = 0 }],
            },
        };

        ResolvedTarget? target = ResolverTestHelp.ResolveWith(tree, windows, definition, Options);

        Assert.NotNull(target);
        Assert.False(target.FoundBySearch);
        Assert.Equal("orphan", FakeElementTree.TagOf(target.Element));
    }
}
