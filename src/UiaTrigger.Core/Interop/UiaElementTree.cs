// IElementTree の UIA 実装。
//
// ここが docs/DESIGN.md B1 の本体: 子要素の列挙を FindAllBuildCache + get_Cached* に置き換え、
// 「候補 1 件あたり最大 3 往復 × 512 件 × 深さ」を「段あたり 1 往復」にする。
using System.Runtime.InteropServices;
using UiaTrigger.Models;
using UiaTrigger.Resolution;

namespace UiaTrigger.Interop;

/// <summary>
/// UIA 要素 1 個。識別プロパティは CacheRequest 経由で取得済みなので、
/// <see cref="IElementNode"/> のメンバー参照はクロスプロセス呼び出しを起こさない。
/// </summary>
/// <remarks>
/// <para>
/// **この型にファイナライザーが無く <see cref="IDisposable"/> も実装していないことは、
/// 設計であって手抜きではない** (docs/DESIGN.md §7)。use-after-free (同 §7) は
/// 「GC のファイナライザースレッドが、UIA スレッドの呼び出し中に RCW を解放する」ことで起きる。
/// ここには**非同期に回収する主体が存在しない**ので、<see cref="Unwrap"/> が生ポインターを
/// 返す形が同じでも危険は同じでない。解放を呼ぶ主体は <see cref="UiaElementTree.Release"/>
/// 経由の解決ループだけであり、それは同期に走る。
/// </para>
/// <para>
/// 足すなら <see cref="Unwrap"/> を借用スコープへ変える必要がある —
/// <see cref="UiaElement.Borrow"/> と同じ形である。この**不在**は
/// <c>ElementBorrowTests</c> が固定している。
/// </para>
/// </remarks>
internal sealed class UiaElementNode : IElementNode
{
    private readonly bool _releasable;
    private int _released;

    private UiaElementNode(IUIAutomationElement element, bool releasable, bool readCache)
    {
        Element = element;
        _releasable = releasable;
        AutomationId = string.Empty;
        Name = string.Empty;
        ClassName = string.Empty;
        if (!readCache)
        {
            return;
        }
        // すべて cached。CacheRequest に入れたプロパティなので追加の往復は発生しない
        element.get_CachedProcessId(out int processId);
        element.get_CachedControlType(out int controlType);
        element.get_CachedAutomationId(out string automationId);
        element.get_CachedName(out string name);
        element.get_CachedClassName(out string className);
        ProcessId = processId;
        ControlType = controlType;
        AutomationId = automationId ?? string.Empty;
        Name = name ?? string.Empty;
        ClassName = className ?? string.Empty;
    }

    public IUIAutomationElement Element { get; }

    public int ProcessId { get; }

    public int ControlType { get; }

    public string AutomationId { get; }

    public string Name { get; }

    public string ClassName { get; }

    /// <summary>キャッシュ済みの要素を包む。<paramref name="releasable"/> は一意 RCW かどうか。</summary>
    public static UiaElementNode FromCached(IUIAutomationElement element, bool releasable) => new(element, releasable, readCache: true);

    /// <summary>
    /// キャッシュを持たない要素を「子を辿るための取っ手」としてだけ包む。
    /// 識別プロパティは空のままなので、採点や同一性比較には使えない。
    /// </summary>
    public static UiaElementNode ForNavigation(IUIAutomationElement element) => new(element, releasable: false, readCache: false);

    /// <summary>解決結果から COM 要素を取り出す (イベント購読・プロパティ読取に使う)。</summary>
    public static IUIAutomationElement Unwrap(IElementNode node) => ((UiaElementNode)node).Element;

    /// <summary>一意 RCW を解放する。**2 度呼んでも安全**。</summary>
    /// <remarks>
    /// <para>
    /// **この冪等化は保険であり、二重解放の証拠があって入れたものではない**
    /// (docs/DESIGN.md §7)。今それを防いでいるのは
    /// <c>ElementResolver.ReleaseLevels</c> の生き残り走査だけだが、あれは冪等化とは
    /// 別に要る — 段の候補は**子を組み立てる前に** <c>levels</c> へ登録してあり
    /// (途中で落ちても、その段で既に採った分が解放されるように)、勝ち残った経路を
    /// 「負け」として解放しないために生き残り集合が要る。段ごとにその場で解放する形へ
    /// 単純化できないのは、ビームが経路の前半を共有するからである
    /// (<c>ElementResolver.cs:257-259</c>)。
    /// </para>
    /// <para>
    /// <see cref="UiaElement"/> と違い**参照をヌル化しないのは意図である**。
    /// あちらは解放後の <c>Borrow()</c> を失敗させる必要がある (ファイナライザーが
    /// 非同期に解放するので、掴んだ後の失敗を見せないと静かに壊れる)。こちらには
    /// その回収者が居ないので、ヌル化しても捕まえられる誤りが増えない。
    /// 一方で費用は確実にある: <see cref="Unwrap"/> が
    /// <see cref="ObjectDisposedException"/> を投げるようになると、
    /// <c>TriggerMonitor</c> の 7 箇所がそれを捕まえられない — あそこは
    /// <c>COMException</c> しか捕まえず、しかもディスパッチャーへ post された仕事の中なので
    /// **誰も観測しない faulted Task** になる。この失敗形は実際に起きて catch を
    /// 足して塞いだ実績があり、証拠の無い変更で新しく作ってよいものではない。
    /// </para>
    /// </remarks>
    public void Release()
    {
        if (_releasable && Interlocked.Exchange(ref _released, 1) == 0)
        {
            UiaFactory.ReleaseUnique(Element);
        }
    }
}

internal sealed class UiaElementTree(UiaContext context) : IElementTree
{
    public IElementNode? GetWindow(nint hwnd)
    {
        context.Automation.ElementFromHandleBuildCache(hwnd, context.IdentityCacheRequest, out nint pointer);
        var element = UiaFactory.WrapUnique<IUIAutomationElement>(pointer);
        return element is null ? null : UiaElementNode.FromCached(element, releasable: true);
    }

    public IReadOnlyList<IElementNode> GetChildren(IElementNode parent, TreeViewMode view, int max)
    {
        ArgumentNullException.ThrowIfNull(parent);
        if (max <= 0)
        {
            return [];
        }

        // 条件と CacheRequest.TreeFilter の両方にビュー条件を入れる。
        // TreeFilter を設定しないと Children の走査が Raw ビューで行われ、
        // Control/Content ビューでの記録経路と食い違う。
        var element = UiaElementNode.Unwrap(parent);
        element.FindAllBuildCache(
            TreeScope.Children,
            context.GetViewCondition(view),
            context.GetChildCacheRequest(view),
            out nint arrayPointer);

        var array = UiaFactory.WrapUnique<IUIAutomationElementArray>(arrayPointer);
        if (array is null)
        {
            return [];
        }
        try
        {
            array.get_Length(out int length);
            int count = Math.Min(length, max);
            var children = new List<IElementNode>(Math.Max(count, 0));
            for (int i = 0; i < count; i++)
            {
                array.GetElement(i, out nint childPointer);
                var child = UiaFactory.WrapUnique<IUIAutomationElement>(childPointer);
                if (child is not null)
                {
                    children.Add(UiaElementNode.FromCached(child, releasable: true));
                }
            }
            return children;
        }
        finally
        {
            UiaFactory.ReleaseUnique(array);
        }
    }

    public bool AreSame(IElementNode a, IElementNode b)
    {
        ArgumentNullException.ThrowIfNull(a);
        ArgumentNullException.ThrowIfNull(b);
        return context.AreSame(UiaElementNode.Unwrap(a), UiaElementNode.Unwrap(b));
    }

    public IReadOnlyList<IElementNode> FindByAutomationId(IElementNode scope, TreeViewMode view, string automationId, int max)
    {
        ArgumentNullException.ThrowIfNull(scope);
        if (string.IsNullOrEmpty(automationId) || max <= 0)
        {
            return [];
        }

        // Subtree の FindAll 1 発。段ごとに子を列挙するビーム探索と違い、
        // 途中の段の構造が変わっても影響を受けない (docs/DESIGN.md §3 の Search 方式)
        UiaElementNode.Unwrap(scope).FindAllBuildCache(
            TreeScope.Subtree,
            context.CreateAutomationIdCondition(view, automationId),
            context.GetChildCacheRequest(view),
            out nint arrayPointer);

        var array = UiaFactory.WrapUnique<IUIAutomationElementArray>(arrayPointer);
        if (array is null)
        {
            return [];
        }
        try
        {
            array.get_Length(out int length);
            int count = Math.Min(length, max);
            var found = new List<IElementNode>(Math.Max(count, 0));
            for (int i = 0; i < count; i++)
            {
                array.GetElement(i, out nint pointer);
                var element = UiaFactory.WrapUnique<IUIAutomationElement>(pointer);
                if (element is not null)
                {
                    found.Add(UiaElementNode.FromCached(element, releasable: true));
                }
            }
            return found;
        }
        finally
        {
            UiaFactory.ReleaseUnique(array);
        }
    }

    public IElementNode? GetParent(IElementNode node, TreeViewMode view)
    {
        ArgumentNullException.ThrowIfNull(node);
        context.GetWalker(view).GetParentElementBuildCache(
            UiaElementNode.Unwrap(node), context.IdentityCacheRequest, out nint pointer);
        var parent = UiaFactory.WrapUnique<IUIAutomationElement>(pointer);
        return parent is null ? null : UiaElementNode.FromCached(parent, releasable: true);
    }

    public ElementIdentity ReadIdentity(IElementNode node)
    {
        ArgumentNullException.ThrowIfNull(node);
        UiaElementNode.Unwrap(node).BuildUpdatedCache(context.IdentityCacheRequest, out nint pointer);
        var updated = UiaFactory.WrapUniqueRequired<IUIAutomationElement>(pointer);
        try
        {
            return ElementIdentity.Of(UiaElementNode.FromCached(updated, releasable: false));
        }
        finally
        {
            UiaFactory.ReleaseUnique(updated);
        }
    }

    public void Release(IElementNode? node)
    {
        if (node is UiaElementNode uia)
        {
            uia.Release();
        }
    }
}
