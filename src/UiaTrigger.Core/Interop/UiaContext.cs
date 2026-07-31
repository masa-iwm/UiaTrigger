using System.Runtime.InteropServices;
using System.Runtime.InteropServices.Marshalling;
using UiaTrigger.Models;
using UiaTrigger.Resolution;

namespace UiaTrigger.Interop;

/// <summary>
/// UiaDispatcher スレッド上で保持する UIA オブジェクト一式。
/// すべてのメンバーはディスパッチャスレッドからのみ触ること。
/// </summary>
internal sealed class UiaContext
{
    /// <summary>
    /// 要素の識別 (経路解決・同一性再検証) に使うプロパティ。
    /// これを CacheRequest にまとめることで、候補 1 件あたり 3 往復 → 段あたり 1 往復になる
    /// (docs/DESIGN.md B1)。
    /// </summary>
    private static readonly int[] IdentityProperties =
    [
        UiaIds.ProcessIdProperty,
        UiaIds.ControlTypeProperty,
        UiaIds.AutomationIdProperty,
        UiaIds.NameProperty,
        UiaIds.ClassNameProperty,
    ];

    /// <summary><see cref="Inspection.PropertyReader.ReadSnapshot"/> が読むプロパティ一式 (16 往復 → 1 往復)。</summary>
    private static readonly int[] SnapshotProperties =
    [
        UiaIds.NameProperty,
        UiaIds.AutomationIdProperty,
        UiaIds.ClassNameProperty,
        UiaIds.ControlTypeProperty,
        // 表示用のローカライズ名 (docs/DESIGN.md L6)。同じ CacheRequest に載せるので往復は増えない
        UiaIds.LocalizedControlTypeProperty,
        UiaIds.BoundingRectangleProperty,
        UiaIds.AccessKeyProperty,
        UiaIds.AcceleratorKeyProperty,
        UiaIds.HelpTextProperty,
        UiaIds.IsEnabledProperty,
        UiaIds.IsOffscreenProperty,
        // 値をスナップショットから落とす判断に要る (docs/DESIGN.md C12)。
        // 抜けると「パスワード欄だと分からないまま値を配る」ことになるので必須
        UiaIds.IsPasswordProperty,
        // パターンのプロパティも「プロパティとして」要求しないと get_Cached* が読めない
        UiaIds.ValueValueProperty,
        UiaIds.RangeValueValueProperty,
        UiaIds.RangeValueMinimumProperty,
        UiaIds.RangeValueMaximumProperty,
    ];

    /// <summary>
    /// 公開 API (<see cref="UiaElement"/>) が要素 1 個について読むプロパティ一式。
    /// 識別プロパティ + 表示・ヒットテストに要るもの。個別の get_Current* なら
    /// 6 往復かかるところを、これで要素あたり 1 往復にする。
    /// </summary>
    private static readonly int[] ElementProperties =
    [
        UiaIds.ProcessIdProperty,
        UiaIds.ControlTypeProperty,
        // 表示用のローカライズ名 (docs/DESIGN.md L6)
        UiaIds.LocalizedControlTypeProperty,
        UiaIds.AutomationIdProperty,
        UiaIds.NameProperty,
        UiaIds.ClassNameProperty,
        UiaIds.BoundingRectangleProperty,
        UiaIds.NativeWindowHandleProperty,
        UiaIds.IsOffscreenProperty,
    ];

    private static readonly int[] SnapshotPatterns = [UiaIds.ValuePattern, UiaIds.RangeValuePattern];

    public IUIAutomation Automation { get; }
    public IUIAutomationElement Root { get; }

    /// <summary>解決ループが使う要素ツリー (COM を隠蔽したインターフェース)。</summary>
    public IElementTree Tree { get; }

    /// <summary>put_TransactionTimeout を適用できたか (IUIAutomation2 が QI できたか)。</summary>
    public bool SupportsTimeouts { get; }

    private readonly IUIAutomationTreeWalker?[] _walkers = new IUIAutomationTreeWalker?[3];
    private readonly IUIAutomationCondition?[] _viewConditions = new IUIAutomationCondition?[3];
    private readonly IUIAutomationCacheRequest?[] _childCacheRequests = new IUIAutomationCacheRequest?[3];
    private readonly IUIAutomationCacheRequest?[] _elementChildCacheRequests = new IUIAutomationCacheRequest?[3];
    private IUIAutomationCacheRequest? _identityCacheRequest;
    private IUIAutomationCacheRequest? _elementCacheRequest;
    private IUIAutomationCacheRequest? _snapshotCacheRequest;

    public UiaContext()
        : this(UiaSessionOptions.DefaultTransactionTimeout, UiaSessionOptions.DefaultConnectionTimeout)
    {
    }

    public UiaContext(TimeSpan transactionTimeout, TimeSpan connectionTimeout)
    {
        Automation = UiaFactory.CreateAutomation();
        Automation.GetRootElement(out var root);
        Root = root;
        Tree = new UiaElementTree(this);
        SupportsTimeouts = ApplyTimeouts(transactionTimeout, connectionTimeout);
    }

    /// <summary>
    /// 応答しないアプリ 1 つで単一 MTA スレッドが無期限に塞がるのを防ぐ (docs/DESIGN.md B5)。
    /// IUIAutomation2 を QI できない環境 (古い OS) では何もしない。
    /// </summary>
    private bool ApplyTimeouts(TimeSpan transactionTimeout, TimeSpan connectionTimeout)
    {
        // ComWrappers の RCW は IDynamicInterfaceCastable なので、この is で QI が走る
        if (Automation is not IUIAutomation2 automation2)
        {
            return false;
        }
        try
        {
            automation2.put_ConnectionTimeout((uint)connectionTimeout.TotalMilliseconds);
            automation2.put_TransactionTimeout((uint)transactionTimeout.TotalMilliseconds);
            return true;
        }
        catch (COMException)
        {
            return false;
        }
    }

    public IUIAutomationTreeWalker GetWalker(TreeViewMode view)
    {
        int index = ViewIndex(view);
        if (_walkers[index] is { } cached)
        {
            return cached;
        }
        IUIAutomationTreeWalker walker;
        switch (view)
        {
            case TreeViewMode.Raw:
                Automation.get_RawViewWalker(out walker);
                break;
            case TreeViewMode.Content:
                Automation.get_ContentViewWalker(out walker);
                break;
            default:
                Automation.get_ControlViewWalker(out walker);
                break;
        }
        _walkers[index] = walker;
        return walker;
    }

    /// <summary>ビューに対応する条件 (FindAll の条件と CacheRequest の TreeFilter の両方に使う)。</summary>
    public IUIAutomationCondition GetViewCondition(TreeViewMode view)
    {
        int index = ViewIndex(view);
        if (_viewConditions[index] is { } cached)
        {
            return cached;
        }
        IUIAutomationCondition condition;
        switch (view)
        {
            case TreeViewMode.Raw:
                Automation.get_RawViewCondition(out condition);
                break;
            case TreeViewMode.Content:
                Automation.get_ContentViewCondition(out condition);
                break;
            default:
                Automation.get_ControlViewCondition(out condition);
                break;
        }
        _viewConditions[index] = condition;
        return condition;
    }

    /// <summary>
    /// 子要素の一括取得用 CacheRequest。TreeFilter にビュー条件を入れることで、
    /// FindAllBuildCache(Children) が Raw ではなく指定ビューの子を返す。
    /// </summary>
    public IUIAutomationCacheRequest GetChildCacheRequest(TreeViewMode view)
    {
        int index = ViewIndex(view);
        if (_childCacheRequests[index] is { } cached)
        {
            return cached;
        }
        var request = CreateRequest(IdentityProperties, patterns: null);
        request.put_TreeFilter(GetViewCondition(view));
        _childCacheRequests[index] = request;
        return request;
    }

    /// <summary>
    /// 公開 API 用の子要素一括取得。<see cref="GetChildCacheRequest"/> と同じ形だが、
    /// 表示・ヒットテストに要るプロパティまで含む (解決ループの往復を太らせないため別にしてある)。
    /// </summary>
    public IUIAutomationCacheRequest GetElementChildCacheRequest(TreeViewMode view)
    {
        int index = ViewIndex(view);
        if (_elementChildCacheRequests[index] is { } cached)
        {
            return cached;
        }
        var request = CreateRequest(ElementProperties, patterns: null);
        request.put_TreeFilter(GetViewCondition(view));
        _elementChildCacheRequests[index] = request;
        return request;
    }

    /// <summary>単一要素の識別プロパティ再取得用 (A8 の同一性再検証 / ウィンドウ要素の取得)。</summary>
    public IUIAutomationCacheRequest IdentityCacheRequest =>
        _identityCacheRequest ??= CreateRequest(IdentityProperties, patterns: null);

    /// <summary>公開 API の <see cref="UiaElement"/> 1 個ぶんの取得用。</summary>
    public IUIAutomationCacheRequest ElementCacheRequest =>
        _elementCacheRequest ??= CreateRequest(ElementProperties, patterns: null);

    /// <summary>プロパティスナップショット取得用。</summary>
    public IUIAutomationCacheRequest SnapshotCacheRequest =>
        _snapshotCacheRequest ??= CreateRequest(SnapshotProperties, SnapshotPatterns);

    private IUIAutomationCacheRequest CreateRequest(int[] properties, int[]? patterns)
    {
        Automation.CreateCacheRequest(out var request);
        request.put_TreeScope(TreeScope.Element);
        // None にすると「キャッシュ済みの値しか持たない」要素になり、以後のイベント購読に使えない
        request.put_AutomationElementMode(AutomationElementMode.Full);
        foreach (int property in properties)
        {
            request.AddProperty(property);
        }
        if (patterns is not null)
        {
            foreach (int pattern in patterns)
            {
                request.AddPattern(pattern);
            }
        }
        return request;
    }

    /// <summary>
    /// 「指定ビューに属し、かつ AutomationId が一致する」条件 (<see cref="ElementSearch"/> 用)。
    ///
    /// 条件オブジェクトの生成はプロセス内呼び出しなのでキャッシュしない。
    /// ビュー条件と AND を取るのを忘れると Raw ビューの要素まで拾い、記録時に「一意」と
    /// 判定した根拠 (記録ビューでの一意性) が解決時に崩れる。
    /// </summary>
    public IUIAutomationCondition CreateAutomationIdCondition(TreeViewMode view, string automationId)
    {
        using var value = ComVariant.Create(automationId);
        Automation.CreatePropertyCondition(UiaIds.AutomationIdProperty, value, out var idCondition);
        Automation.CreateAndCondition(GetViewCondition(view), idCondition, out var condition);
        return condition;
    }

    public bool AreSame(IUIAutomationElement a, IUIAutomationElement b)
    {
        Automation.CompareElements(a, b, out bool same);
        return same;
    }

    private static int ViewIndex(TreeViewMode view) => view switch
    {
        TreeViewMode.Raw => 0,
        TreeViewMode.Content => 2,
        _ => 1,
    };
}
