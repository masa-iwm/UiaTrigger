using System.Runtime.InteropServices;
using UiaTrigger.Interop;
using UiaTrigger.Models;
using UiaTrigger.Resolution;
using Xunit;

namespace UiaTrigger.Tests;

/// <summary>
/// COM 無しの擬似要素ツリー。
///
/// ElementResolver が具象 UiaContext に直結していると、探索アルゴリズムを単体テストする
/// 継ぎ目が無い (docs/DESIGN.md D1)。ビーム探索もここに載せる。
/// </summary>
internal sealed class FakeElement : IElementNode
{
    public int ProcessId { get; init; }
    public int ControlType { get; init; }
    public string AutomationId { get; init; } = string.Empty;
    public string Name { get; init; } = string.Empty;
    public string ClassName { get; init; } = string.Empty;

    /// <summary>この要素の子 (指定ビューでの順序)。</summary>
    public List<FakeElement> Children { get; } = [];

    /// <summary>テストの目印。</summary>
    public string Tag { get; init; } = string.Empty;

    /// <summary>解放後に差し替えられる識別 (A8 の「生きているが別物」を作るため)。</summary>
    public ElementIdentity? IdentityOverride { get; set; }

    /// <summary>true にすると識別の読み直しで「消えた」ことにする。</summary>
    public bool Gone { get; set; }

    /// <summary>親要素 (Search 方式が経路購読用の祖先鎖を遡るのに使う)。</summary>
    public FakeElement? Parent { get; private set; }

    public FakeElement WithChildren(params FakeElement[] children)
    {
        ArgumentNullException.ThrowIfNull(children);
        Children.AddRange(children);
        foreach (FakeElement child in children)
        {
            child.Parent = this;
        }
        return this;
    }

    /// <summary>自分を含む部分木を文書順で列挙する (UIA の FindAll(Subtree) と同じ順序)。</summary>
    public IEnumerable<FakeElement> SelfAndDescendants()
    {
        yield return this;
        foreach (FakeElement child in Children)
        {
            foreach (FakeElement descendant in child.SelfAndDescendants())
            {
                yield return descendant;
            }
        }
    }
}

internal sealed class FakeElementTree : IElementTree
{
    private readonly Dictionary<nint, FakeElement> _windows;

    public FakeElementTree(Dictionary<nint, FakeElement> windows) => _windows = windows;

    /// <summary>GetChildren の呼び出し回数 = クロスプロセス往復の回数 (docs/DESIGN.md B1)。</summary>
    public int GetChildrenCallCount { get; private set; }

    public int GetWindowCallCount { get; private set; }

    /// <summary>Release されたノード (解放漏れ・二重解放の検出用)。</summary>
    public List<IElementNode> Released { get; } = [];

    /// <summary>GetChildren / GetWindow が配ったノードの総数。</summary>
    public List<IElementNode> Handed { get; } = [];

    public IElementNode? GetWindow(nint hwnd)
    {
        GetWindowCallCount++;
        if (!_windows.TryGetValue(hwnd, out FakeElement? window))
        {
            return null;
        }
        var handle = new Handle(window);
        Handed.Add(handle);
        return handle;
    }

    /// <summary>この回数目の GetChildren で「要素が消えた」ことにする (0 = 常に成功)。</summary>
    public int ThrowOnGetChildrenCall { get; set; }

    public IReadOnlyList<IElementNode> GetChildren(IElementNode parent, TreeViewMode view, int max)
    {
        GetChildrenCallCount++;
        if (GetChildrenCallCount == ThrowOnGetChildrenCall)
        {
            throw Marshal.GetExceptionForHR(ComErrors.ElementNotAvailable)!;
        }
        var source = ((Handle)parent).Element;
        var children = new List<IElementNode>();
        foreach (FakeElement child in source.Children.Take(max))
        {
            var handle = new Handle(child);
            Handed.Add(handle);
            children.Add(handle);
        }
        return children;
    }

    /// <summary>FindByAutomationId の呼び出し回数 = Search 方式の往復回数。</summary>
    public int FindCallCount { get; private set; }

    /// <summary>GetParent の呼び出し回数 = 祖先鎖を遡るのにかかった往復回数。</summary>
    public int GetParentCallCount { get; private set; }

    /// <summary>この回数目の FindByAutomationId で「要素が消えた」ことにする (0 = 常に成功)。</summary>
    public int ThrowOnFindCall { get; set; }

    public IReadOnlyList<IElementNode> FindByAutomationId(IElementNode scope, TreeViewMode view, string automationId, int max)
    {
        FindCallCount++;
        if (FindCallCount == ThrowOnFindCall)
        {
            throw Marshal.GetExceptionForHR(ComErrors.ElementNotAvailable)!;
        }
        var found = new List<IElementNode>();
        // 実物と同じく Subtree はスコープ自身を含む
        foreach (FakeElement element in ((Handle)scope).Element.SelfAndDescendants())
        {
            if (found.Count >= max)
            {
                break;
            }
            if (string.Equals(element.AutomationId, automationId, StringComparison.Ordinal))
            {
                var handle = new Handle(element);
                Handed.Add(handle);
                found.Add(handle);
            }
        }
        return found;
    }

    public IElementNode? GetParent(IElementNode node, TreeViewMode view)
    {
        GetParentCallCount++;
        if (((Handle)node).Element.Parent is not { } parent)
        {
            return null;
        }
        var handle = new Handle(parent);
        Handed.Add(handle);
        return handle;
    }

    /// <summary>実物と同じく「同じ要素を指す別インスタンス」を同一と判定する (参照比較にしない)。</summary>
    public bool AreSame(IElementNode a, IElementNode b) =>
        ReferenceEquals(((Handle)a).Element, ((Handle)b).Element);

    public ElementIdentity ReadIdentity(IElementNode node)
    {
        var element = ((Handle)node).Element;
        if (element.Gone)
        {
            // 実物と同じ「要素が消えた」HRESULT を投げる
            throw Marshal.GetExceptionForHR(ComErrors.ElementNotAvailable)!;
        }
        return element.IdentityOverride ?? ElementIdentity.Of(node);
    }

    public void Release(IElementNode? node)
    {
        if (node is not null)
        {
            Assert.DoesNotContain(node, Released); // 二重解放はここで落とす
            Released.Add(node);
        }
    }

    /// <summary>まだ解放されていないノード。</summary>
    public IEnumerable<IElementNode> Retained => Handed.Except(Released);

    /// <summary>ノードが指す擬似要素の目印。</summary>
    public static string TagOf(IElementNode node) => ((Handle)node).Element.Tag;

    /// <summary>
    /// 「配られたノード」は毎回別インスタンスにする。実物 (UIA) も呼び出しごとに
    /// 別の COM オブジェクトを返すため、参照同一性に頼るテストにしないための再現。
    /// </summary>
    private sealed class Handle(FakeElement element) : IElementNode
    {
        public FakeElement Element { get; } = element;
        public int ProcessId => Element.ProcessId;
        public int ControlType => Element.ControlType;
        public string AutomationId => Element.AutomationId;
        public string Name => Element.Name;
        public string ClassName => Element.ClassName;
    }
}

/// <summary>擬似ウィンドウ列挙。EnumWindows / OpenProcess の回数を数える。</summary>
internal sealed class FakeWindowSource(params WindowInfo[] windows) : IWindowSource
{
    private readonly Dictionary<uint, string?> _paths = [];

    public int EnumerateCalls { get; private set; }

    public int ImagePathCalls { get; private set; }

    public FakeWindowSource WithProcess(uint processId, string? imagePath)
    {
        _paths[processId] = imagePath;
        return this;
    }

    public IReadOnlyList<WindowInfo> GetVisibleWindows()
    {
        EnumerateCalls++;
        return windows;
    }

    public string? GetProcessImagePath(uint processId)
    {
        ImagePathCalls++;
        return _paths.TryGetValue(processId, out string? path) ? path : null;
    }
}
