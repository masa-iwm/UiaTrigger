// ピッカーが使う UIA サービス。
//
// **Core の公開 API (UiaSession) だけで書いてある** (docs/DESIGN.md §12)。
// Core の internal な IUIAutomationElement を直接掴む形に戻すと、要素の捕捉・祖先チェーン・
// 重なりスタック・簡易ヒットテスト (本来 Core の公開機能) をここが自前で持つことになり、
// 「第三者は自前のピッカーを作れない」「製品アセンブリ間に InternalsVisibleTo が要る」
// の 2 つが同時に起きる。ここに在るのはピッカー固有の都合だけである:
//   ・プロセスの表示ラベル
//   ・「既存のチェーン子が再列挙後の何番目か」
//   ・重なりスタックの現在位置
//
// IPickerServices が継ぎ目で、入出力は IPickerElement である。UiaElement は
// テストから作れない (private コンストラクタ + 引数が internal な COM 型) ため、この継ぎ目が
// 無いとプレゼンターのテストが 1 件も書けない。UiaPickerElement がその適合層である。
using UiaTrigger.Models;

namespace UiaTrigger.Picker;

/// <summary>ホバー/選択された要素のプロセスからの直系チェーン (先頭=トップレベルウィンドウ, 末尾=対象要素)。</summary>
/// <remarks>運び屋であって持ち主ではない。<see cref="Chain"/> の要素は受け取った側のものになる。</remarks>
internal sealed class HoverCapture
{
    public required string ProcessLabel { get; init; }
    public required int ProcessId { get; init; }
    public required IReadOnlyList<IPickerElement> Chain { get; init; }
}

/// <summary>同一座標に重なる要素のスタック (下 → 上)。</summary>
/// <remarks>運び屋であって持ち主ではない。<see cref="Nodes"/> の要素は受け取った側のものになる。</remarks>
internal sealed class ElementStack
{
    public required IReadOnlyList<IPickerElement> Nodes { get; init; }
    public required int CurrentIndex { get; init; }
}

/// <summary>子要素列挙の結果。ChainChildIndex は既存チェーン子と同一の要素の位置 (-1: なし)。</summary>
/// <remarks>
/// 運び屋であって持ち主ではない。<see cref="Children"/> の要素は受け取った側のものになる —
/// <see cref="ChainChildIndex"/> の 1 個を**使わなかった場合も含めて**である。
/// </remarks>
internal sealed class ChildrenResult
{
    public required IReadOnlyList<IPickerElement> Children { get; init; }
    public required int ChainChildIndex { get; init; }
}

/// <summary>プレゼンターから見た UIA。製品の実装は <see cref="PickerServices"/> 1 つだけである。</summary>
/// <remarks>
/// **返った結果から到達できる要素はすべて呼び出し元のものになる。**
/// 7 つのうち 5 つが要素を配る (<see cref="CaptureAtAsync"/> / <see cref="GetChainAsync"/> /
/// <see cref="GetChainForOverlapAsync"/> / <see cref="GetChildrenAsync"/> /
/// <see cref="GetStackAsync"/>)。保持しないものは解放する責任が呼び出し元にある
/// (<see cref="IPickerElement"/> / <see cref="TriggerPickerPresenter"/> の掃き出し)。
/// 逆に**引数で渡した要素の所有権は移らない** — こちらは借りて読むだけである。
/// </remarks>
internal interface IPickerServices : IAsyncDisposable
{
    /// <summary>座標が信用できない理由 (ホストが PerMonitorV2 でない場合)。信用できるなら null。</summary>
    string? CoordinateProblem { get; }

    Task<HoverCapture?> CaptureAtAsync(int x, int y, TreeViewMode view);

    Task<HoverCapture?> GetChainAsync(IPickerElement element, TreeViewMode view);

    Task<HoverCapture?> GetChainForOverlapAsync(IPickerElement element, TreeViewMode view);

    Task<ChildrenResult?> GetChildrenAsync(IPickerElement parent, TreeViewMode view, IPickerElement? chainChild);

    Task<ElementPropertySnapshot?> GetSnapshotAsync(IPickerElement element);

    Task<TriggerDefinition?> BuildDefinitionAsync(IPickerElement element, TreeViewMode view);

    Task<ElementStack?> GetStackAsync(int x, int y, IPickerElement? current);
}

/// <summary><see cref="UiaElement"/> を継ぎ目に適合させる薄いラッパー。</summary>
internal sealed class UiaPickerElement(UiaElement element) : IPickerElement
{
    /// <summary>包んでいる要素。UiaSession に渡し返すときだけ取り出す。</summary>
    /// <remarks>
    /// ラッパーを <see cref="Dispose"/> するとこれも解放される。**別に解放しないこと。**
    /// </remarks>
    public UiaElement Inner { get; } = element;

    public string AutomationId => Inner.AutomationId;

    public string ControlTypeName => Inner.ControlTypeName;

    public string DisplayLabel => Inner.DisplayLabel;

    public ElementRect BoundingRectangle => Inner.BoundingRectangle;

    /// <summary>包んでいる要素の跨プロセス参照を解放する。2 度呼んでも安全。</summary>
    /// <remarks>
    /// <see cref="UiaElement.Dispose"/> は <c>Interlocked.Exchange</c> で守られているので冪等である。
    /// 上の 4 つの値は生成時に読んだスナップショットなので、解放後も読める —
    /// 壊れるのは <see cref="Unwrap"/> を通って UIA へ渡す経路だけで、そこは例外になる。
    /// </remarks>
    public void Dispose() => Inner.Dispose();

    /// <summary>
    /// 継ぎ目越しに来た要素から <see cref="UiaElement"/> を取り出す。
    /// 偽の要素 (テスト用) を実物のセッションへ渡そうとしたら、静かに何かが起きるのではなく
    /// ここで落ちるべきである。
    /// </summary>
    public static UiaElement Unwrap(IPickerElement value) =>
        value is UiaPickerElement wrapper
            ? wrapper.Inner
            : throw new ArgumentException($"{value.GetType().Name} は UIA の要素ではありません。", nameof(value));
}

internal sealed class PickerServices : IPickerServices
{
    private readonly UiaSession _session = new(new UiaSessionOptions { ThreadName = "UiaTrigger.Picker" });

    /// <summary>
    /// 座標が信用できない理由 (ホストが PerMonitorV2 でない場合)。信用できるなら null。
    /// ピッカーは座標を扱う UI そのものなので、これは必ず画面に出す (docs/DESIGN.md A19 / C13)。
    /// </summary>
    public string? CoordinateProblem => _session.CoordinateProblem;

    public event Action<Exception>? UnhandledException
    {
        add => _session.UnhandledException += value;
        remove => _session.UnhandledException -= value;
    }

    /// <summary>スクリーン座標の要素を捕捉。自プロセスの要素なら null。</summary>
    public async Task<HoverCapture?> CaptureAtAsync(int x, int y, TreeViewMode view)
    {
        // 捕捉した要素そのものは起点にしか使わない。チェーンは別のハンドルで返るので手放してよい。
        // この UiaElement は Wrap を通らないので IPickerElement からは参照されえず、
        // 呼び出し元の掃き出しと二重解放になることがない (仮に重なっても Dispose は冪等である)
        using UiaElement? element = await _session.ElementFromPointAsync(x, y).ConfigureAwait(true);
        return element is null ? null : await BuildCaptureAsync(element, view, normalize: true).ConfigureAwait(true);
    }

    /// <summary>
    /// 既知の要素からチェーンを再構築する (ビュー変更用)。
    /// 対象ビューの条件を満たさない要素は、正規化により最も近い祖先に丸められる。
    /// </summary>
    public Task<HoverCapture?> GetChainAsync(IPickerElement element, TreeViewMode view)
        => BuildCaptureAsync(UiaPickerElement.Unwrap(element), view, normalize: true);

    /// <summary>
    /// 重なり切替専用: 対象要素をそのままチェーンの末端として使う (正規化しない)。
    /// 重なりスタックは Raw ビューのヒットテストで構築されるため、現在の表示ビューが
    /// Control/Content の場合に正規化すると、複数の重なり要素が同じ近傍祖先へ丸められ、
    /// 「別要素へ切り替えたつもりが同じ要素のまま」になってしまうため正規化を避ける。
    /// </summary>
    public Task<HoverCapture?> GetChainForOverlapAsync(IPickerElement element, TreeViewMode view)
        => BuildCaptureAsync(UiaPickerElement.Unwrap(element), view, normalize: false);

    public async Task<ChildrenResult?> GetChildrenAsync(IPickerElement parent, TreeViewMode view, IPickerElement? chainChild)
    {
        IReadOnlyList<UiaElement> children =
            await _session.GetChildrenAsync(UiaPickerElement.Unwrap(parent), view).ConfigureAwait(true);
        // 既存のチェーン子が再列挙後の何番目かを 1 往復で調べる
        // (要素ごとに比較を投げると子の数だけディスパッチャ往復が発生する)
        int chainIndex = chainChild is null
            ? -1
            : await _session.IndexOfAsync(children, UiaPickerElement.Unwrap(chainChild)).ConfigureAwait(true);
        return new ChildrenResult { Children = Wrap(children), ChainChildIndex = chainIndex };
    }

    public Task<ElementPropertySnapshot?> GetSnapshotAsync(IPickerElement element)
        => _session.ReadSnapshotAsync(UiaPickerElement.Unwrap(element));

    public Task<TriggerDefinition?> BuildDefinitionAsync(IPickerElement element, TreeViewMode view)
        => _session.BuildDefinitionAsync(UiaPickerElement.Unwrap(element), view);

    /// <summary>指定座標に重なる要素スタックを構築する (別ウィンドウ・別プロセス含む、下 → 上)。</summary>
    public async Task<ElementStack?> GetStackAsync(int x, int y, IPickerElement? current)
    {
        IReadOnlyList<UiaElement> nodes = await _session.GetOverlapStackAsync(x, y).ConfigureAwait(true);
        if (nodes.Count == 0)
        {
            return null;
        }
        int currentIndex = current is null
            ? -1
            : await _session.IndexOfAsync(nodes, UiaPickerElement.Unwrap(current)).ConfigureAwait(true);
        if (currentIndex < 0)
        {
            currentIndex = nodes.Count - 1; // 最前面ウィンドウの最深要素
        }
        return new ElementStack { Nodes = Wrap(nodes), CurrentIndex = currentIndex };
    }

    private async Task<HoverCapture?> BuildCaptureAsync(UiaElement element, TreeViewMode view, bool normalize)
    {
        IReadOnlyList<UiaElement> chain =
            await _session.GetAncestorChainAsync(element, view, normalize).ConfigureAwait(true);
        if (chain.Count == 0)
        {
            return null;
        }
        int processId = chain[0].ProcessId;
        string? imagePath = UiaSession.GetProcessImagePath(processId);
        string label = imagePath is null
            ? $"PID {processId}"
            : $"{Path.GetFileName(imagePath)} (PID {processId})";
        return new HoverCapture { ProcessLabel = label, ProcessId = processId, Chain = Wrap(chain) };
    }

    private static IPickerElement[] Wrap(IReadOnlyList<UiaElement> elements)
    {
        var wrapped = new IPickerElement[elements.Count];
        for (int i = 0; i < elements.Count; i++)
        {
            wrapped[i] = new UiaPickerElement(elements[i]);
        }
        return wrapped;
    }

    public ValueTask DisposeAsync() => _session.DisposeAsync();
}
