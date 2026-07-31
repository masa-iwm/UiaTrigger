// Core は「監視ライブラリ」ではなく「UIA セッション + 監視」である (docs/DESIGN.md §3 / C2)。
//
// 要素ツリーの探索・ヒットテスト・スナップショットを Picker の internal な実装に置き、
// Picker が Core の internal な COM 型 (IUIAutomationElement) を直接掴む形だと
//   ・InternalsVisibleTo が製品アセンブリ間に必要になる
//   ・第三者は自前のピッカー / インスペクタを作れない
//   ・MTA スレッドが監視・調査・ピッカーそれぞれの専用型で 3 本立つ
// の 3 つが同時に起きる。1 セッション = 1 MTA スレッド + 1 IUIAutomation に集約し、
// COM を UiaElement という不透明ハンドルの裏に閉じる。
using System.Runtime.InteropServices;
using Microsoft.Extensions.Logging;
using UiaTrigger.Inspection;
using UiaTrigger.Interop;
using UiaTrigger.Models;
using UiaTrigger.Monitoring;
using UiaTrigger.Resources;
using UiaTrigger.Threading;

namespace UiaTrigger;

/// <summary>
/// One UI Automation session: a dedicated automation thread plus the automation objects on it.
/// </summary>
/// <remarks>
/// <para>
/// UI Automation objects are apartment-bound and its cross-process calls can block for a long
/// time, so every call this library makes runs on one dedicated MTA thread owned by the session.
/// That is also why a session is the unit of sharing: create one, then use it for element
/// inspection, for recording definitions, and for the monitors you create from it with
/// <see cref="CreateMonitor"/>. Two sessions mean two threads and two sets of automation objects.
/// </para>
/// <para>
/// All members are safe to call from any thread. The methods that look an element up return null
/// when UI Automation fails transiently (the element vanished mid-call, the provider timed out);
/// the reason goes to <see cref="UiaSessionOptions.Logger"/> rather than becoming an exception,
/// because for a picker following the mouse those failures are routine.
/// </para>
/// <para>
/// Anything taking a screen coordinate requires the host process to be per-monitor DPI aware —
/// check <see cref="CoordinateProblem"/>. See <see cref="DpiAwareness"/> for why this is not
/// optional.
/// </para>
/// </remarks>
public sealed class UiaSession : IAsyncDisposable
{
    private readonly UiaSessionOptions _options;
    private readonly UiaDispatcher _dispatcher;
    private readonly int _ownProcessId = Environment.ProcessId;
    private UiaContext? _ctx;
    private bool _disposed;

    /// <summary>Creates a session and starts its automation thread.</summary>
    /// <param name="options">Settings, or null for the defaults.</param>
    /// <exception cref="ArgumentOutOfRangeException">A timeout or limit in <paramref name="options"/> is out of range.</exception>
    public UiaSession(UiaSessionOptions? options = null)
    {
        _options = options ?? new UiaSessionOptions();
        // 検証は呼び出し元スレッドで前倒しする。設定ミスがディスパッチャスレッド上の例外に
        // なると、呼び出し元には「なぜか動かない」としか見えない
        _options.Validate();
        _dispatcher = new UiaDispatcher(_options.ThreadName);

        // ホストが PerMonitorV2 かどうかは座標を渡すすべての API の前提 (docs/DESIGN.md A19 / C13)。
        // 破れていても例外にはならず「別の要素が返る」だけなので、ここで必ず記録する
        CoordinateProblem = DpiAwareness.DescribeProblem();
        if (CoordinateProblem is not null)
        {
            Log.DpiUnaware(_options.Logger, CoordinateProblem);
        }
    }

    /// <summary>
    /// Why screen coordinates cannot be trusted in this process, or null when they can.
    /// </summary>
    /// <remarks>
    /// Evaluated once, when the session is created, from the DPI awareness of the calling thread —
    /// which is the process default unless the host has deliberately changed it. A picker should
    /// show this to the user instead of recording definitions that point at the wrong element.
    /// </remarks>
    public string? CoordinateProblem { get; }

    /// <summary>
    /// Raised for exceptions on the automation thread that have nowhere else to go.
    /// </summary>
    public event Action<Exception>? UnhandledException
    {
        add => _dispatcher.UnhandledException += value;
        remove => _dispatcher.UnhandledException -= value;
    }

    /// <summary>The clock this session and its monitors use.</summary>
    internal TimeProvider TimeProvider => _options.TimeProvider;

    /// <summary>The log this session and its monitors write to.</summary>
    internal ILogger? Logger => _options.Logger;

    internal UiaDispatcher Dispatcher => _dispatcher;

    /// <summary>ディスパッチャスレッド上でのみ呼ぶこと。</summary>
    internal UiaContext Context =>
        _ctx ??= new UiaContext(_options.TransactionTimeout, _options.ConnectionTimeout);

    /// <summary>
    /// Whether the configured call timeouts are actually in effect.
    /// </summary>
    /// <remarks>
    /// False on systems without <c>IUIAutomation2</c> (before Windows 8), where UI Automation's own
    /// 20-second default applies and one unresponsive application can stall the session. Forces the
    /// session to connect to UI Automation if it has not already.
    /// </remarks>
    public Task<bool> GetSupportsTimeoutsAsync() => _dispatcher.InvokeAsync(() => Context.SupportsTimeouts);

    // ---------- 要素の取得 ----------

    /// <summary>The root element of the desktop.</summary>
    public Task<UiaElement> GetRootAsync() => _dispatcher.InvokeAsync(() =>
    {
        Context.Automation.GetRootElementBuildCache(Context.ElementCacheRequest, out nint pointer);
        return Wrap(UiaFactory.WrapUniqueRequired<IUIAutomationElement>(pointer));
    });

    /// <summary>
    /// The element at a physical screen point, or null when there is none there.
    /// </summary>
    /// <remarks>
    /// Returns null for elements of the calling process when
    /// <see cref="UiaSessionOptions.SkipOwnProcessElements"/> is set. UI Automation itself returns
    /// "no element" for some coordinates without failing, so null is a normal answer.
    /// </remarks>
    public Task<UiaElement?> ElementFromPointAsync(int x, int y) =>
        _dispatcher.InvokeAsync<UiaElement?>(() => ElementFromPointCore(x, y));

    /// <summary>The element under the mouse cursor, or null when there is none.</summary>
    /// <inheritdoc cref="ElementFromPointAsync" path="/remarks"/>
    public Task<UiaElement?> ElementFromCursorAsync() => _dispatcher.InvokeAsync<UiaElement?>(() =>
    {
        if (!NativeMethods.GetCursorPos(out System.Drawing.Point point))
        {
            return null;
        }
        return ElementFromPointCore(point.X, point.Y);
    });

    /// <summary>
    /// The children of an element in the given tree view, read in one cross-process call.
    /// </summary>
    /// <param name="parent">The element whose children to read.</param>
    /// <param name="view">The tree view to enumerate in.</param>
    /// <param name="max">
    /// Maximum number to return, or null for <see cref="UiaSessionOptions.MaxChildren"/>.
    /// </param>
    /// <returns>The children, or an empty list when the element is gone or has none.</returns>
    public Task<IReadOnlyList<UiaElement>> GetChildrenAsync(UiaElement parent, TreeViewMode view = TreeViewMode.Control, int? max = null)
    {
        ArgumentNullException.ThrowIfNull(parent);
        int limit = max ?? _options.MaxChildren;
        return _dispatcher.InvokeAsync<IReadOnlyList<UiaElement>>(() =>
        {
            try
            {
                using UiaElement.Borrowed borrowed = parent.Borrow();
                return GetChildrenCore(borrowed.Element, view, limit);
            }
            catch (COMException ex)
            {
                Log.LookupFailed(_options.Logger, nameof(GetChildrenAsync), ex);
                return [];
            }
        });
    }

    /// <summary>
    /// The chain from the element's top-level window down to the element itself.
    /// </summary>
    /// <param name="element">The element to start from.</param>
    /// <param name="view">The tree view to walk in.</param>
    /// <param name="normalize">
    /// Whether to round the element up to its nearest ancestor that belongs to
    /// <paramref name="view"/>. Off when you need to tell apart elements that would collapse onto
    /// the same ancestor — overlapping elements found by a hit test, for instance.
    /// </param>
    /// <returns>
    /// The chain, first the top-level window and last the element. Empty when it could not be
    /// walked.
    /// </returns>
    public Task<IReadOnlyList<UiaElement>> GetAncestorChainAsync(
        UiaElement element, TreeViewMode view = TreeViewMode.Control, bool normalize = true)
    {
        ArgumentNullException.ThrowIfNull(element);
        return _dispatcher.InvokeAsync<IReadOnlyList<UiaElement>>(() =>
        {
            try
            {
                using UiaElement.Borrowed borrowed = element.Borrow();
                return BuildChain(borrowed.Element, view, normalize);
            }
            catch (COMException ex)
            {
                Log.LookupFailed(_options.Logger, nameof(GetAncestorChainAsync), ex);
                return [];
            }
        });
    }

    /// <summary>
    /// Every element that covers a physical screen point, bottom-most first.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Spans windows and processes: for each visible top-level window containing the point, the hit
    /// chain inside it is appended, with the window first and the deepest element last. Windows of
    /// the calling process are skipped when
    /// <see cref="UiaSessionOptions.SkipOwnProcessElements"/> is set.
    /// </para>
    /// <para>
    /// The front-most window uses UI Automation's own hit test, which is authoritative. Windows
    /// behind it are walked with an approximate hit test over child bounds, because UI Automation
    /// only hit-tests what is on top.
    /// </para>
    /// </remarks>
    public Task<IReadOnlyList<UiaElement>> GetOverlapStackAsync(int x, int y) =>
        _dispatcher.InvokeAsync<IReadOnlyList<UiaElement>>(() =>
        {
            try
            {
                return BuildOverlapStack(x, y);
            }
            catch (COMException ex)
            {
                Log.LookupFailed(_options.Logger, nameof(GetOverlapStackAsync), ex);
                return [];
            }
        });

    /// <summary>
    /// Whether two handles refer to the same element.
    /// </summary>
    /// <remarks>
    /// UI Automation hands out a different object every time it is asked for the same element, so
    /// reference equality is meaningless; this compares runtime ids. False when either element is
    /// already gone.
    /// </remarks>
    public Task<bool> AreSameAsync(UiaElement a, UiaElement b)
    {
        ArgumentNullException.ThrowIfNull(a);
        ArgumentNullException.ThrowIfNull(b);
        return _dispatcher.InvokeAsync(() =>
        {
            try
            {
                using UiaElement.Borrowed left = a.Borrow();
                using UiaElement.Borrowed right = b.Borrow();
                return Context.AreSame(left.Element, right.Element);
            }
            catch (COMException)
            {
                // 比較できない = 少なくとも一方が既に死んでいる
                return false;
            }
        });
    }

    /// <summary>
    /// The position of <paramref name="target"/> in <paramref name="elements"/>, or -1.
    /// </summary>
    /// <remarks>
    /// Prefer this over calling <see cref="AreSameAsync"/> in a loop: the whole comparison runs in
    /// one hop onto the automation thread instead of one per candidate. Matching a freshly
    /// enumerated list of children against the element that is currently selected is the usual
    /// reason to need it, and those lists can be long.
    /// </remarks>
    public Task<int> IndexOfAsync(IReadOnlyList<UiaElement> elements, UiaElement target)
    {
        ArgumentNullException.ThrowIfNull(elements);
        ArgumentNullException.ThrowIfNull(target);
        return _dispatcher.InvokeAsync(() =>
        {
            for (int i = 0; i < elements.Count; i++)
            {
                try
                {
                    using UiaElement.Borrowed candidate = elements[i].Borrow();
                    using UiaElement.Borrowed wanted = target.Borrow();
                    if (Context.AreSame(candidate.Element, wanted.Element))
                    {
                        return i;
                    }
                }
                catch (COMException)
                {
                    // 比較できない候補は「別物」として次へ
                }
            }
            return -1;
        });
    }

    // ---------- プロパティと定義 ----------

    /// <summary>
    /// Every observable property of an element, read in one cross-process call.
    /// </summary>
    /// <returns>The snapshot, or null when the element is gone.</returns>
    /// <remarks>
    /// Values of password fields are withheld; see
    /// <see cref="ElementPropertySnapshot.RedactedMarker"/>.
    /// </remarks>
    public Task<ElementPropertySnapshot?> ReadSnapshotAsync(UiaElement element)
    {
        ArgumentNullException.ThrowIfNull(element);
        return _dispatcher.InvokeAsync<ElementPropertySnapshot?>(() =>
        {
            try
            {
                using UiaElement.Borrowed borrowed = element.Borrow();
                return PropertyReader.ReadSnapshot(Context, borrowed.Element);
            }
            catch (COMException ex)
            {
                Log.LookupFailed(_options.Logger, nameof(ReadSnapshotAsync), ex);
                return null;
            }
        });
    }

    /// <summary>
    /// Records a trigger definition for an element: how to find its window, and how to find it
    /// inside that window.
    /// </summary>
    /// <param name="element">The element to record.</param>
    /// <param name="view">
    /// The tree view to record the path in. The same view is used when resolving, so a path
    /// recorded in one view cannot be resolved in another.
    /// </param>
    /// <returns>The definition, or null when the element went away while it was being recorded.</returns>
    /// <remarks>
    /// The returned definition has an arbitrary unique <see cref="TriggerDefinition.Id"/> and no
    /// clauses: fill both in before monitoring it.
    /// </remarks>
    public Task<TriggerDefinition?> BuildDefinitionAsync(UiaElement element, TreeViewMode view = TreeViewMode.Control)
    {
        ArgumentNullException.ThrowIfNull(element);
        return _dispatcher.InvokeAsync<TriggerDefinition?>(() =>
        {
            try
            {
                using UiaElement.Borrowed borrowed = element.Borrow();
                return DefinitionBuilder.Build(Context, borrowed.Element, view);
            }
            catch (COMException ex)
            {
                Log.LookupFailed(_options.Logger, nameof(BuildDefinitionAsync), ex);
                return null;
            }
        });
    }

    /// <summary>Records a trigger definition for the element at a physical screen point.</summary>
    /// <exception cref="InvalidOperationException">No element was found at that point.</exception>
    /// <inheritdoc cref="BuildDefinitionAsync" path="/remarks"/>
    public Task<TriggerDefinition> BuildDefinitionFromPointAsync(int x, int y, TreeViewMode view = TreeViewMode.Control) =>
        _dispatcher.InvokeAsync(() => BuildDefinitionAtCore(x, y, view));

    /// <summary>Records a trigger definition for the element under the mouse cursor.</summary>
    /// <exception cref="InvalidOperationException">No element was found under the cursor.</exception>
    /// <inheritdoc cref="BuildDefinitionAsync" path="/remarks"/>
    public Task<TriggerDefinition> BuildDefinitionFromCursorAsync(TreeViewMode view = TreeViewMode.Control) =>
        _dispatcher.InvokeAsync(() =>
        {
            NativeMethods.GetCursorPos(out System.Drawing.Point point);
            return BuildDefinitionAtCore(point.X, point.Y, view);
        });

    /// <summary>
    /// The full path of a process's executable, or null when it cannot be read.
    /// </summary>
    /// <remarks>
    /// Null usually means the process runs at a higher integrity level than this one — an elevated
    /// application. Its windows cannot be inspected either, which is the most common reason a
    /// trigger never resolves; see <see cref="Monitoring.TriggerMonitor.ResolutionChanged"/>.
    /// </remarks>
    public static string? GetProcessImagePath(int processId) =>
        processId <= 0 ? null : NativeMethods.GetProcessImagePath((uint)processId);

    // ---------- 監視 ----------

    /// <summary>
    /// Creates a monitor that shares this session's automation thread and objects.
    /// </summary>
    /// <param name="options">
    /// Monitor settings, or null for the defaults. <see cref="TriggerMonitorOptions.Session"/> is
    /// ignored — this session's settings apply.
    /// </param>
    /// <remarks>
    /// Preferred over <c>new TriggerMonitor(...)</c> whenever the host also inspects elements: a
    /// standalone monitor starts a second automation thread and a second set of automation objects.
    /// Disposing the monitor does not dispose this session, but disposing this session while a
    /// monitor is still running does stop that monitor from working — dispose monitors first.
    /// </remarks>
    public TriggerMonitor CreateMonitor(TriggerMonitorOptions? options = null)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        return new TriggerMonitor(this, options);
    }

    /// <summary>Stops the automation thread and releases the session's automation objects.</summary>
    /// <remarks>
    /// Dispose the monitors created from this session first: they run their UI Automation work on
    /// this session's thread, and once it is gone they can neither resolve nor unsubscribe.
    /// </remarks>
    public ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return ValueTask.CompletedTask;
        }
        _disposed = true;
        _dispatcher.Dispose();
        return ValueTask.CompletedTask;
    }

    // ---- 以下はすべて UiaDispatcher スレッド上 ----

    private static UiaElement Wrap(IUIAutomationElement element) => UiaElement.FromCached(element, releasable: true);

    private UiaElement? ElementFromPointCore(int x, int y)
    {
        try
        {
            Context.Automation.ElementFromPointBuildCache(
                new System.Drawing.Point(x, y), Context.ElementCacheRequest, out nint pointer);
            var element = UiaFactory.WrapUnique<IUIAutomationElement>(pointer);
            if (element is null)
            {
                // UIA は「どの要素にも属さない座標」に対して S_OK + null を返す (docs/DESIGN.md A15)
                return null;
            }
            UiaElement wrapped = Wrap(element);
            if (_options.SkipOwnProcessElements && wrapped.ProcessId == _ownProcessId)
            {
                wrapped.Dispose();
                return null;
            }
            if (wrapped.BoundingRectangle.Contains(x, y))
            {
                return wrapped;
            }

            // ヒットテストが<b>その点を含まない要素</b>を返した (docs/DESIGN.md §9)。
            //
            // 実測では、デスクトップのアイコンの上で、シェルは隣の項目を返す
            // ことがある (点 (150,47) に対して (0,5)-(100,70) の「ごみ箱」)。ピッカーはそれを
            // 忠実に表示するので、<b>カーソルを隣のアイコンへ動かしても選択が変わらない</b>。
            // 例外は出ず、同じ操作でも出たり出なかったりする。
            //
            // <b>矛盾はこちらで検出できる。</b>「その点の要素」を訊いたのだから、
            // 返ってきた矩形はその点を含んでいなければならない。含まないなら、
            // 窓から下りる素朴なヒットテストで引き直す — 重なり切替 (←/→) が使っていて
            // 実際に届く経路と同じものである。
            UiaElement? better = DeepestContaining(x, y);
            if (better is null)
            {
                // 引き直しても見つからないなら、元の答えを返す。<b>null にはしない</b> —
                // 「何も無い」は「捕捉しない」を意味し、症状が変わらないまま原因が隠れる
                return wrapped;
            }
            wrapped.Dispose();
            return better;
        }
        catch (COMException ex)
        {
            Log.LookupFailed(_options.Logger, nameof(ElementFromPointAsync), ex);
            return null;
        }
    }

    private TriggerDefinition BuildDefinitionAtCore(int x, int y, TreeViewMode view)
    {
        Context.Automation.ElementFromPointBuildCache(
            new System.Drawing.Point(x, y), Context.ElementCacheRequest, out nint pointer);
        var element = UiaFactory.WrapUnique<IUIAutomationElement>(pointer);
        if (element is null)
        {
            throw new InvalidOperationException(Message.Format(Strings.Error_NoElementAtPoint, x, y));
        }
        try
        {
            return DefinitionBuilder.Build(Context, element, view);
        }
        finally
        {
            UiaFactory.ReleaseUnique(element);
        }
    }

    private List<UiaElement> GetChildrenCore(IUIAutomationElement parent, TreeViewMode view, int max)
    {
        if (max <= 0)
        {
            return [];
        }
        // 条件と CacheRequest.TreeFilter の両方にビュー条件を入れる。TreeFilter を忘れると
        // Children の走査が Raw ビューで行われ、指定ビューでの並びと食い違う
        parent.FindAllBuildCache(
            TreeScope.Children,
            Context.GetViewCondition(view),
            Context.GetElementChildCacheRequest(view),
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
            var children = new List<UiaElement>(Math.Max(count, 0));
            for (int i = 0; i < count; i++)
            {
                array.GetElement(i, out nint childPointer);
                var child = UiaFactory.WrapUnique<IUIAutomationElement>(childPointer);
                if (child is not null)
                {
                    children.Add(Wrap(child));
                }
            }
            return children;
        }
        finally
        {
            UiaFactory.ReleaseUnique(array);
        }
    }

    private List<UiaElement> BuildChain(IUIAutomationElement element, TreeViewMode view, bool normalize)
    {
        var walker = Context.GetWalker(view);
        IUIAutomationElement? start = null;
        if (normalize)
        {
            walker.NormalizeElementBuildCache(element, Context.ElementCacheRequest, out nint pointer);
            start = UiaFactory.WrapUnique<IUIAutomationElement>(pointer);
        }
        // 正規化していない (または正規化で何も返らなかった) 場合は、呼び出し元の要素から
        // 表示用の値を読み直したハンドルを作る。呼び出し元の要素の解放責任はここには無い
        var chain = new List<UiaElement> { start is not null ? Wrap(start) : BuildUpdated(element) };

        using UiaElement.Borrowed start0 = chain[0].Borrow();
        IUIAutomationElement current = start0.Element;
        for (int depth = 1; depth < _options.MaxDepth; depth++)
        {
            walker.GetParentElementBuildCache(current, Context.ElementCacheRequest, out nint parentPointer);
            var parent = UiaFactory.WrapUnique<IUIAutomationElement>(parentPointer);
            if (parent is null)
            {
                break;
            }
            if (Context.AreSame(parent, Context.Root))
            {
                UiaFactory.ReleaseUnique(parent);
                break;
            }
            chain.Add(Wrap(parent));
            current = parent;
        }
        chain.Reverse(); // 先頭 = トップレベルウィンドウ
        return chain;
    }

    /// <summary>キャッシュを持たない要素から、表示用の値をキャッシュしたハンドルを作る (1 往復)。</summary>
    private UiaElement BuildUpdated(IUIAutomationElement element)
    {
        element.BuildUpdatedCache(Context.ElementCacheRequest, out nint pointer);
        return Wrap(UiaFactory.WrapUniqueRequired<IUIAutomationElement>(pointer));
    }

    /// <summary>
    /// 点を含む<b>最前面の窓</b>から下りて、その点を含むいちばん深い要素を返す。
    /// </summary>
    /// <remarks>
    /// <para>
    /// UIA のヒットテストが点を含まない要素を返したときの引き直しである (docs/DESIGN.md §9)。
    /// <see cref="BuildOverlapStack"/> が最前面以外の窓に対して使っているのと同じ
    /// <see cref="AppendHitChain"/> を通す — あちらは子の矩形を見て下りるだけなので、
    /// <b>プロバイダのヒットテストに依存しない</b>。
    /// </para>
    /// <para>
    /// 窓の絞り込みは <see cref="BuildOverlapStack"/> と同じ規則にしてある
    /// (可視 / 点を含む / 自プロセスは除く)。<b>ここで自プロセスを除くのが要である</b> —
    /// 除かないと、ピッカー自身のオーバーレイを引き直しの答えにしてしまう。
    /// </para>
    /// </remarks>
    private UiaElement? DeepestContaining(int x, int y)
    {
        foreach (nint hwnd in NativeMethods.EnumTopLevelWindows())
        {
            if (!NativeMethods.IsWindowVisible(hwnd))
            {
                continue;
            }
            NativeMethods.GetWindowThreadProcessId(hwnd, out uint processId);
            if (_options.SkipOwnProcessElements && processId == (uint)_ownProcessId)
            {
                continue;
            }
            if (!NativeMethods.GetWindowRect(hwnd, out UiaRect rect) ||
                x < rect.Left || x >= rect.Right || y < rect.Top || y >= rect.Bottom)
            {
                continue;
            }

            // EnumTopLevelWindows は Z 順 (最前面が先頭) なので、最初に見つかった窓が答えである
            var chain = new List<UiaElement>();
            AppendHitChain(hwnd, x, y, chain);
            if (chain.Count == 0)
            {
                return null;
            }
            UiaElement deepest = chain[^1];
            for (int i = 0; i < chain.Count - 1; i++)
            {
                chain[i].Dispose();
            }
            return deepest.BoundingRectangle.Contains(x, y) ? deepest : Release(deepest);
        }
        return null;

        static UiaElement? Release(UiaElement element)
        {
            element.Dispose();
            return null;
        }
    }

    private List<UiaElement> BuildOverlapStack(int x, int y)
    {
        var windows = new List<nint>();
        foreach (nint hwnd in NativeMethods.EnumTopLevelWindows())
        {
            if (windows.Count >= _options.MaxOverlapWindows)
            {
                break;
            }
            if (!NativeMethods.IsWindowVisible(hwnd))
            {
                continue;
            }
            NativeMethods.GetWindowThreadProcessId(hwnd, out uint processId);
            if (_options.SkipOwnProcessElements && processId == (uint)_ownProcessId)
            {
                continue;
            }
            if (!NativeMethods.GetWindowRect(hwnd, out UiaRect rect) ||
                x < rect.Left || x >= rect.Right || y < rect.Top || y >= rect.Bottom)
            {
                continue;
            }
            windows.Add(hwnd);
        }

        // EnumWindows は Z 順 (最前面が先頭)。下 → 上 に並べるため逆順に処理する
        var nodes = new List<UiaElement>();
        for (int i = windows.Count - 1; i >= 0; i--)
        {
            if (i == 0)
            {
                // 最前面ウィンドウは UIA 本来のヒットテストで一意に決まる最深要素を起点にする。
                // 下の簡易ヒットテスト (子の BoundingRectangle を比較するだけの近似) は
                // ElementFromPoint と結果が食い違うことがあり、それだけに頼ると
                // 「元の要素」を見失う原因になる
                Context.Automation.ElementFromPointBuildCache(
                    new System.Drawing.Point(x, y), Context.ElementCacheRequest, out nint pointer);
                var deepest = UiaFactory.WrapUnique<IUIAutomationElement>(pointer);
                if (deepest is not null)
                {
                    AppendAncestorChain(windows[i], Wrap(deepest), nodes);
                    continue;
                }
                // ElementFromPoint が null を返す座標もある (docs/DESIGN.md A15)。
                // その場合だけ簡易ヒットテストにフォールバックする
            }
            AppendHitChain(windows[i], x, y, nodes);
        }
        return nodes;
    }

    /// <summary>
    /// 最深要素 (UIA の ElementFromPoint が返す一意な要素) からウィンドウ要素まで祖先を遡り、
    /// 下 (ウィンドウ) → 上 (最深要素) の順で積む。これが「重なり」ナビゲーションのスタックになる。
    /// </summary>
    private void AppendAncestorChain(nint hwnd, UiaElement deepest, List<UiaElement> nodes)
    {
        var walker = Context.GetWalker(TreeViewMode.Raw);
        var chain = new List<UiaElement> { deepest };
        using UiaElement.Borrowed deepestBorrowed = deepest.Borrow();
        IUIAutomationElement current = deepestBorrowed.Element;
        for (int depth = 1; depth < _options.MaxDepth && chain[^1].NativeWindowHandle != hwnd; depth++)
        {
            walker.GetParentElementBuildCache(current, Context.ElementCacheRequest, out nint pointer);
            var parent = UiaFactory.WrapUnique<IUIAutomationElement>(pointer);
            if (parent is null)
            {
                break;
            }
            chain.Add(Wrap(parent));
            current = parent;
        }
        chain.Reverse(); // ウィンドウ → … → 最深要素
        nodes.AddRange(chain);
    }

    private void AppendHitChain(nint hwnd, int x, int y, List<UiaElement> nodes)
    {
        UiaElement? current;
        try
        {
            Context.Automation.ElementFromHandleBuildCache(hwnd, Context.ElementCacheRequest, out nint pointer);
            var element = UiaFactory.WrapUnique<IUIAutomationElement>(pointer);
            if (element is null)
            {
                return; // UIA を提供していないウィンドウ (docs/DESIGN.md A15)
            }
            current = Wrap(element);
        }
        catch (COMException ex)
        {
            Log.LookupFailed(_options.Logger, nameof(GetOverlapStackAsync), ex);
            return;
        }

        for (int depth = 0; depth < _options.MaxDepth; depth++)
        {
            nodes.Add(current);

            // 座標を含む「文書順で最後の」子 (後の兄弟ほど上に描画される近似) へ降りる
            UiaElement? next = null;
            List<UiaElement> children;
            try
            {
                using UiaElement.Borrowed borrowed = current.Borrow();
                children = GetChildrenCore(borrowed.Element, TreeViewMode.Raw, _options.MaxChildren);
            }
            catch (COMException)
            {
                break;
            }
            foreach (UiaElement child in children)
            {
                ElementRect rect = child.BoundingRectangle;
                if (!child.IsOffscreen && x >= rect.Left && x < rect.Right && y >= rect.Top && y < rect.Bottom)
                {
                    next?.Dispose();
                    next = child;
                }
                else
                {
                    child.Dispose();
                }
            }
            if (next is null)
            {
                break;
            }
            current = next;
        }
    }
}
