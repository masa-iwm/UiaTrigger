// TriggerPickerPresenter を動かすための偽物一式 (docs/TESTING.md §1 T1)。
//
// 実物の要素 (UiaElement) は private コンストラクタ + 引数が internal な COM 型で、
// T1 からは作れない。ピッカーの継ぎ目
// (IPickerElement / IPickerServices / IPickerView / IUiDispatcher / ICursorSource /
// IPickerStrings / IOverlay — docs/DESIGN.md §12) を全部ここで埋める。
using UiaTrigger.Models;
using UiaTrigger.Picker;

namespace UiaTrigger.Tests;

/// <summary>偽の要素。UiaElement の代わりにプレゼンターへ渡す。</summary>
internal sealed class FakePickerElement(string displayLabel) : IPickerElement
{
    public string AutomationId { get; init; } = string.Empty;

    public string ControlTypeName { get; init; } = "Button";

    public string DisplayLabel { get; } = displayLabel;

    public ElementRect BoundingRectangle { get; init; }

    /// <summary>解放された回数。0 = 生きている、2 以上 = 二重解放。</summary>
    /// <remarks>
    /// **ここで Assert してはいけない。**プレゼンターは <c>catch (Exception)</c> を
    /// 3 箇所持ち (<c>CaptureAtAsync</c> / <c>MoveStackAsync</c> / <c>ConfirmNodeAsync</c>)、
    /// xunit の失敗例外もそこでヒント文字列に変えられて緑で通る。さらに fire-and-forget の
    /// 経路では観測すらされない。判定は必ずテストスレッド側で行う
    /// (<see cref="FakePickerServices.Retained"/> ほか)。
    /// </remarks>
    public int DisposeCount { get; private set; }

    public bool IsDisposed => DisposeCount > 0;

    public void Dispose() => DisposeCount++;

    public override string ToString() => DisplayLabel;
}

/// <summary>カーソル位置をテストから決める。</summary>
internal sealed class FakeCursor : ICursorSource
{
    public int X { get; set; }

    public int Y { get; set; }

    /// <summary>GetCursorPos が失敗した状況を作る。</summary>
    public bool Available { get; set; } = true;

    public bool TryGetPosition(out int x, out int y)
    {
        x = X;
        y = Y;
        return Available;
    }
}

/// <summary>表示スケールをテストから決める (docs/DESIGN.md §9)。</summary>
/// <remarks>
/// 既定は 96 である。**走っている機械の DPI を見にいってはいけない** —
/// T1 が 96 の機械では緑・175% の機械では赤という、再現しないテストになる。
/// スケールを見たいテストは <see cref="Dpi"/> を明示的に立てる。
/// </remarks>
internal sealed class FakeDpiSource : IDpiSource
{
    public int Dpi { get; set; } = OverlayGeometry.ReferenceDpi;

    public int DpiFor(ElementRect rect) => Dpi;
}

/// <summary>キーをそのまま返す。書式指定子を含めたいテストは <see cref="Values"/> に入れる。</summary>
internal sealed class FakeStrings : IPickerStrings
{
    public Dictionary<string, string> Values { get; } = [];

    public List<string> Requested { get; } = [];

    public string GetString(string key)
    {
        Requested.Add(key);
        return Values.TryGetValue(key, out string? value) ? value : key;
    }
}

/// <summary>投稿は同期実行し、タイマーはテストが手で打つ。</summary>
internal sealed class FakeDispatcher : IUiDispatcher
{
    public List<FakeTimer> Timers { get; } = [];

    public int PostCount { get; private set; }

    public void Post(Action action)
    {
        PostCount++;
        action();
    }

    public IUiTimer CreateTimer(TimeSpan interval, Action tick)
    {
        var timer = new FakeTimer(interval, tick);
        Timers.Add(timer);
        return timer;
    }
}

internal sealed class FakeTimer(TimeSpan interval, Action tick) : IUiTimer
{
    public TimeSpan Interval => interval;

    public bool IsRunning { get; private set; }

    public bool IsDisposed { get; private set; }

    public void SetRunning(bool running) => IsRunning = running;

    /// <summary>1 打ぶん進める。動いていなければ何もしない (本物と同じ)。</summary>
    public void Tick()
    {
        if (IsRunning)
        {
            tick();
        }
    }

    public void Dispose() => IsDisposed = true;
}

/// <summary>オーバーレイの呼ばれ方を記録し、確定クリック / ←→ をテストから起こす。</summary>
internal sealed class FakeOverlay : IOverlay
{
    public string? CreationError { get; set; }

    public event Action? ConfirmClicked;

    public event Action<bool>? ArrowKeyPressed;

    public List<ElementRect> ShownRects { get; } = [];

    public int HideCount { get; private set; }

    public bool HookEnabled { get; private set; }

    public bool IsDisposed { get; private set; }

    public void ShowRect(ElementRect rect) => ShownRects.Add(rect);

    public void Hide() => HideCount++;

    public void SetHookEnabled(bool enabled) => HookEnabled = enabled;

    public void Dispose() => IsDisposed = true;

    public void RaiseConfirmClicked() => ConfirmClicked?.Invoke();

    public void RaiseArrowKey(bool right) => ArrowKeyPressed?.Invoke(right);
}

/// <summary>
/// View の代わり。本物の View がすることのうち、プレゼンターから観測できるもの
/// (展開・遅延選択・選択状態) は実際に行う — でないと「展開したことになっているだけ」の
/// テストになる。
/// </summary>
internal sealed class FakePickerView : IPickerView
{
    public string? LastHint { get; set; }

    public string? LastConfirmedText { get; private set; }

    public string? LastCommitStatus { get; private set; }

    public string KeyText { get; set; } = string.Empty;

    public string DisplayNameText { get; set; } = string.Empty;

    public PickerTreeNode? SelectedNode { get; set; }

    public IReadOnlyList<string> PropertyRows { get; private set; } = [];

    public int ShowPropertiesCount { get; private set; }

    public IReadOnlyList<TriggerProperty> ShapeProperties { get; private set; } = [];

    public TriggerOn ShapeLifecycle { get; private set; }

    public ComparisonOp ShapeComparison { get; private set; }

    public int ShowTriggerShapeCount { get; private set; }

    public OperandVisibility? LastOperands { get; private set; }

    public bool? CommitEnabled { get; private set; }

    /// <summary><see cref="ReadDraft"/> が返すもの。null は「形が未選択」。</summary>
    public TriggerDraft? Draft { get; set; }

    /// <summary><see cref="ShowDraft"/> に渡されたもの (呼ばれた順)。</summary>
    public List<TriggerDraft> ShownDrafts { get; } = [];

    /// <summary>
    /// View が受けた指示の順番。<c>ShowDraft</c> が
    /// <c>ShowTriggerShape</c> の後・<c>ShowOperands</c> の前に来ることを見るために使う。
    /// </summary>
    public List<string> Calls { get; } = [];

    public int DiscardCount { get; private set; }

    /// <summary>差し替え中に選択変化を報告してはいけない窓が開いているか。</summary>
    public bool InNodeUpdate { get; private set; }

    /// <summary>差し替えの開始/終了が対で呼ばれたか (入れ子や取りこぼしを検出する)。</summary>
    public int NodeUpdateBalance { get; private set; }

    public List<(IReadOnlyList<PickerTreeNode> Order, PickerTreeNode Target)> ExpandThenSelectCalls { get; } = [];

    public List<PickerTreeNode> Expanded { get; } = [];

    public List<PickerTreeNode> SelectDeferredCalls { get; } = [];

    /// <summary>確定ボタンの現在の文言 (null = 一度も差し替えられていない)。</summary>
    public string? CommitCaption { get; private set; }

    /// <summary><see cref="IPickerView.Close"/> が呼ばれた回数。</summary>
    public int CloseCount { get; private set; }

    string IPickerView.Hint { set => LastHint = value; }

    string IPickerView.ConfirmedText { set => LastConfirmedText = value; }

    string IPickerView.CommitStatus
    {
        set
        {
            LastCommitStatus = value;
            // Close との順序を見るために記録する。本物の WinForms View は Close で
            // 自分を Dispose するので、Close の後の書き込みは例外になる
            Calls.Add("CommitStatus");
        }
    }

    string IPickerView.CommitCaption
    {
        set
        {
            CommitCaption = value;
            Calls.Add("CommitCaption");
        }
    }

    public void Close()
    {
        CloseCount++;
        Calls.Add(nameof(Close));
    }

    public void ShowProperties(IReadOnlyList<string> rows)
    {
        PropertyRows = rows;
        ShowPropertiesCount++;
    }

    public void ShowTriggerShape(IReadOnlyList<TriggerProperty> properties, TriggerOn lifecycle, ComparisonOp comparison)
    {
        ShapeProperties = properties;
        ShapeLifecycle = lifecycle;
        ShapeComparison = comparison;
        ShowTriggerShapeCount++;
        Calls.Add(nameof(ShowTriggerShape));
    }

    public void ShowOperands(OperandVisibility visibility)
    {
        LastOperands = visibility;
        Calls.Add(nameof(ShowOperands));
    }

    public void SetCommitEnabled(bool enabled) => CommitEnabled = enabled;

    public TriggerDraft? ReadDraft() => Draft;

    /// <remarks>
    /// 本物の View は入力欄を埋め、次に <see cref="ReadDraft"/> でそれを読み返す。
    /// ここでも <see cref="Draft"/> に入れておく — でないと「読み込んだ直後に確定する」という
    /// 編集のいちばん普通の経路が、テストでは何も運ばないことになる。
    /// </remarks>
    public void ShowDraft(TriggerDraft draft)
    {
        ShownDrafts.Add(draft);
        Draft = draft;
        KeyText = draft.Id ?? string.Empty;
        DisplayNameText = draft.DisplayName ?? string.Empty;
        Calls.Add(nameof(ShowDraft));
    }

    public void DiscardDeferredWork() => DiscardCount++;

    public void BeginNodeUpdate()
    {
        InNodeUpdate = true;
        NodeUpdateBalance++;
    }

    public void EndNodeUpdate()
    {
        InNodeUpdate = false;
        NodeUpdateBalance--;
    }

    public void ExpandThenSelect(IReadOnlyList<PickerTreeNode> expandOrder, PickerTreeNode target)
    {
        ExpandThenSelectCalls.Add((expandOrder, target));
        Expand(expandOrder);
        SelectDeferred(target);
    }

    public void Expand(IReadOnlyList<PickerTreeNode> nodes)
    {
        foreach (PickerTreeNode node in nodes)
        {
            Expanded.Add(node);
            node.ExpandForDisplay(); // 本物の View と同じく「表示だけ」の展開
        }
    }

    public void SelectDeferred(PickerTreeNode node)
    {
        SelectDeferredCalls.Add(node);
        SelectedNode = node;
    }
}

/// <summary>トリガ一覧エディタの View の代わり (docs/DESIGN.md §4)。</summary>
internal sealed class FakeEditorView : ITriggerListEditorView
{
    public string? LastStatus { get; private set; }

    public string ExpressionText { get; set; } = string.Empty;

    public string UnwatchedText { get; set; } = string.Empty;

    public double? CombinePollIntervalSeconds { get; set; }

    public IReadOnlyList<int> SelectedIndices { get; set; } = [];

    /// <summary>最後に表示された行。</summary>
    public IReadOnlyList<string> Rows { get; private set; } = [];

    /// <summary>ピッカーを開くよう指示された記録 (null = 追加のため)。</summary>
    public List<TriggerDefinition?> PickerRequests { get; } = [];

    string ITriggerListEditorView.Status { set => LastStatus = value; }

    public void ShowRows(IReadOnlyList<string> rows) => Rows = rows;

    public void ShowPicker(TriggerDefinition? definitionToEdit) => PickerRequests.Add(definitionToEdit);
}

/// <summary>UIA の代わり。返す値をテストが仕込み、渡された引数を記録する。</summary>
/// <remarks>
/// <para>
/// **ハンドルの台帳もここにある。**プレゼンターが要素を受け取る先はここだけなので、
/// 配ったものを 1 箇所で数えれば「漏らしていないか」「解放済みを使い回していないか」を
/// **既存のテスト全件に対して**見られる (<see cref="Retained"/> /
/// <see cref="UseAfterDispose"/>)。解決層の <c>FakeElementTree.Retained</c> と同じ形である。
/// </para>
/// <para>
/// **ここでは記録だけを行い、Assert は呼ばない。**プレゼンターは
/// <c>CaptureAtAsync</c> / <c>MoveStackAsync</c> / <c>ConfirmNodeAsync</c> の 3 箇所で
/// <c>catch (Exception)</c> しており、xunit の失敗例外もそこでヒント文字列に変えられて
/// **緑で通る**。さらに fire-and-forget の経路では観測すらされない。
/// 判定は必ずテストスレッド側 (<c>Harness.Dispose</c>) で行う。
/// </para>
/// </remarks>
internal sealed class FakePickerServices : IPickerServices
{
    public string? CoordinateProblem { get; set; }

    /// <summary>継ぎ目越しに配ったハンドル (重複しうる — 同じ仕込みを何度も返すため)。</summary>
    public List<IPickerElement> Handed { get; } = [];

    /// <summary>配ったのに解放されていないハンドル。空でなければ漏れている。</summary>
    public IEnumerable<IPickerElement> Retained =>
        Handed.Distinct().Where(e => e is FakePickerElement { IsDisposed: false });

    /// <summary>解放済みのハンドルを渡し返された記録。空でなければ解放が早すぎる。</summary>
    /// <remarks>
    /// 二重解放は数えていない。<see cref="UiaElement.Dispose"/> が冪等なので本番で害が無く、
    /// この fake は同じ仕込みを何世代にも返すので「所有 → 解放 → もう一度所有」が
    /// 正当に起きうる — 数えると偽陽性になる。
    /// </remarks>
    public List<string> UseAfterDispose { get; } = [];

    /// <summary>スナップショット読み取りを在庫のまま止める (在庫中の解放を試すため)。</summary>
    public TaskCompletionSource? SnapshotGate { get; set; }

    /// <summary>子の列挙を在庫のまま止める。</summary>
    public TaskCompletionSource? ChildrenGate { get; set; }

    public HoverCapture? NextCapture { get; set; }

    public HoverCapture? NextChain { get; set; }

    public HoverCapture? NextOverlapChain { get; set; }

    public ChildrenResult? NextChildren { get; set; }

    public ElementPropertySnapshot? NextSnapshot { get; set; }

    public TriggerDefinition? NextDefinition { get; set; }

    public ElementStack? NextStack { get; set; }

    /// <summary>CaptureAtAsync が投げる例外 (捕捉失敗の表示を見るため)。</summary>
    public Exception? CaptureThrows { get; set; }

    /// <summary>GetChainAsync が返せる要素。空なら何を渡しても null (= 解決不能)。</summary>
    public HashSet<IPickerElement> ChainResolvableFor { get; } = [];

    public List<(int X, int Y, TreeViewMode View)> CaptureRequests { get; } = [];

    public List<(IPickerElement Element, TreeViewMode View)> ChainRequests { get; } = [];

    public List<(IPickerElement Element, TreeViewMode View)> OverlapChainRequests { get; } = [];

    public List<(IPickerElement Parent, TreeViewMode View, IPickerElement? ChainChild)> ChildrenRequests { get; } = [];

    public List<(int X, int Y, IPickerElement? Current)> StackRequests { get; } = [];

    public List<IPickerElement> SnapshotRequests { get; } = [];

    public List<(IPickerElement Element, TreeViewMode View)> DefinitionRequests { get; } = [];

    public bool DisposeAsyncCalled { get; private set; }

    public Task<HoverCapture?> CaptureAtAsync(int x, int y, TreeViewMode view)
    {
        CaptureRequests.Add((x, y, view));
        return CaptureThrows is not null
            ? Task.FromException<HoverCapture?>(CaptureThrows)
            : Task.FromResult(Hand(NextCapture));
    }

    public Task<HoverCapture?> GetChainAsync(IPickerElement element, TreeViewMode view)
    {
        Track(element, nameof(GetChainAsync));
        ChainRequests.Add((element, view));
        // 仕込みがあればそれを、無ければ「この要素からは辿れる/辿れない」表で答える
        return Task.FromResult(Hand(
            ChainResolvableFor.Count == 0 ? NextChain : ChainResolvableFor.Contains(element) ? NextChain : null));
    }

    public Task<HoverCapture?> GetChainForOverlapAsync(IPickerElement element, TreeViewMode view)
    {
        Track(element, nameof(GetChainForOverlapAsync));
        OverlapChainRequests.Add((element, view));
        return Task.FromResult(Hand(NextOverlapChain));
    }

    /// <remarks>
    /// <see cref="ChildrenGate"/> が null のときは <c>await</c> が 1 つも走らないので、
    /// タスクは同期に完了する — 既存のテストのタイミングは変わらない。
    /// </remarks>
    public async Task<ChildrenResult?> GetChildrenAsync(
        IPickerElement parent, TreeViewMode view, IPickerElement? chainChild)
    {
        Track(parent, nameof(GetChildrenAsync));
        Track(chainChild, $"{nameof(GetChildrenAsync)}(chainChild)");
        ChildrenRequests.Add((parent, view, chainChild));
        if (ChildrenGate is { } gate)
        {
            await gate.Task.ConfigureAwait(true);
            ThrowIfReleasedInFlight(parent, nameof(GetChildrenAsync));
        }
        ChildrenResult? result = NextChildren;
        if (result is not null)
        {
            Handed.AddRange(result.Children);
        }
        return result;
    }

    /// <remarks><see cref="ChildrenGate"/> と同じ理由で、gate 無しなら同期に完了する。</remarks>
    public async Task<ElementPropertySnapshot?> GetSnapshotAsync(IPickerElement element)
    {
        Track(element, nameof(GetSnapshotAsync));
        SnapshotRequests.Add(element);
        if (SnapshotGate is { } gate)
        {
            await gate.Task.ConfigureAwait(true);
            ThrowIfReleasedInFlight(element, nameof(GetSnapshotAsync));
        }
        if (SnapshotException is { } failure)
        {
            throw failure;
        }
        return NextSnapshot;
    }

    /// <summary>
    /// <see cref="GetSnapshotAsync"/> が投げるもの。null なら投げない。
    /// 塞がれたアプリの「Operation timed out」(COMException) を演じるのに使う (docs/DESIGN.md §3 の B5)。
    /// </summary>
    public Exception? SnapshotException { get; set; }

    public Task<TriggerDefinition?> BuildDefinitionAsync(IPickerElement element, TreeViewMode view)
    {
        Track(element, nameof(BuildDefinitionAsync));
        DefinitionRequests.Add((element, view));
        return Task.FromResult(NextDefinition);
    }

    public Task<ElementStack?> GetStackAsync(int x, int y, IPickerElement? current)
    {
        Track(current, nameof(GetStackAsync));
        StackRequests.Add((x, y, current));
        if (NextStack is { } stack)
        {
            Handed.AddRange(stack.Nodes);
        }
        return Task.FromResult(NextStack);
    }

    public ValueTask DisposeAsync()
    {
        DisposeAsyncCalled = true;
        return ValueTask.CompletedTask;
    }

    /// <summary>配ったチェーンを台帳に載せる。</summary>
    private HoverCapture? Hand(HoverCapture? capture)
    {
        if (capture is not null)
        {
            Handed.AddRange(capture.Chain);
        }
        return capture;
    }

    /// <summary>渡された引数が解放済みなら記録する (Assert はしない — 上の remarks)。</summary>
    private void Track(IPickerElement? element, string method)
    {
        if (element is FakePickerElement { IsDisposed: true } dead)
        {
            UseAfterDispose.Add($"{method}({dead.DisplayLabel})");
        }
    }

    /// <summary>
    /// 在庫中に解放されたら、本物の借用スコープと同じように
    /// <see cref="ObjectDisposedException"/> にする。
    /// </summary>
    /// <remarks>
    /// ここだけは記録ではなく**投げる**。プレゼンターはこれを生き延びなければならず、
    /// 生き延びるかどうかは投げてみないと分からないからである。
    /// </remarks>
    private static void ThrowIfReleasedInFlight(IPickerElement? element, string method)
    {
        if (element is FakePickerElement { IsDisposed: true })
        {
            throw new ObjectDisposedException(nameof(IPickerElement), $"{method}: 在庫中に解放された");
        }
    }
}
