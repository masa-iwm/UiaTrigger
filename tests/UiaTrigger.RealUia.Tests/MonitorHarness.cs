using System.Collections.Concurrent;
using System.Globalization;
using System.Text;
using UiaTrigger.Models;
using UiaTrigger.Monitoring;

namespace UiaTrigger.RealUia.Tests;

/// <summary>
/// <see cref="TriggerMonitor"/> のイベントを検出順のまま 1 本のキューに集める。
///
/// 発火と解決状態の変化を **同じキュー** に入れるのが要点で、2 つの相対順序が
/// 保たれていること (docs/DESIGN.md A2) をそのまま観測できるようにするため。
/// </summary>
internal sealed class MonitorHarness : IAsyncDisposable
{
    public static readonly TimeSpan DefaultTimeout = TimeSpan.FromSeconds(15);

    private readonly BlockingCollection<EventArgs> _events = [];
    private readonly List<EventArgs> _seen = [];
    private readonly ConcurrentQueue<Exception> _unhandled = new();

    public TriggerMonitor Monitor { get; }

    /// <summary>発火ハンドラの中で追加で走らせる処理 (例外隔離の検証用)。</summary>
    public Action<TriggerFiredEventArgs>? FiredHook { get; set; }

    /// <summary>ハンドラの例外を含む、行き場のない例外。</summary>
    public IReadOnlyCollection<Exception> Unhandled => _unhandled;

    /// <summary>
    /// <paramref name="session"/> を渡すとそのセッションを共有する monitor になる
    /// (渡さなければ monitor が自分のセッションを持つ)。共有した場合、monitor を捨てても
    /// セッションは生きたままである — その性質そのものを見るテストがある。
    /// </summary>
    public MonitorHarness(UiaSession? session = null)
    {
        Monitor = session is null ? new TriggerMonitor() : session.CreateMonitor();

        Monitor.TriggerFired += (_, e) =>
        {
            _events.Add(e);
            FiredHook?.Invoke(e);
        };
        Monitor.ResolutionChanged += (_, e) => _events.Add(e);
        Monitor.UnhandledException += _unhandled.Enqueue;
    }

    public Task StartAsync(params TriggerDefinition[] definitions) => Monitor.StartAsync(definitions);

    /// <summary>次のイベント 1 件 (種類は問わない)。時間切れなら null。</summary>
    public EventArgs? Next(TimeSpan timeout)
    {
        if (!_events.TryTake(out EventArgs? item, timeout))
        {
            return null;
        }
        _seen.Add(item);
        return item;
    }

    public TriggerFiredEventArgs WaitForFire(TimeSpan? timeout = null)
        => WaitFor<TriggerFiredEventArgs>(_ => true, "発火", timeout);

    public TriggerFiredEventArgs WaitForFire(string triggerId, TimeSpan? timeout = null)
        => WaitFor<TriggerFiredEventArgs>(
            e => string.Equals(e.TriggerId, triggerId, StringComparison.Ordinal),
            $"'{triggerId}' の発火", timeout);

    public TriggerResolutionChangedEventArgs WaitForResolution(bool resolved, TimeSpan? timeout = null)
        => WaitFor<TriggerResolutionChangedEventArgs>(
            e => e.IsResolved == resolved, resolved ? "解決" : "未解決化", timeout);

    /// <summary>指定時間、発火が 1 件も来ないこと (ネガティブコントロール用)。</summary>
    public void AssertNoFire(TimeSpan window)
    {
        DateTime deadline = DateTime.UtcNow + window;
        while (true)
        {
            // 残り時間は 1 度だけ計算して使う。while の条件と Next の引数で 2 度読むと、
            // その間に期限を過ぎて負の待ち時間を渡すことがある
            TimeSpan remaining = deadline - DateTime.UtcNow;
            if (remaining <= TimeSpan.Zero)
            {
                return;
            }
            if (Next(remaining) is TriggerFiredEventArgs fired)
            {
                throw new InvalidOperationException(
                    $"発火しないはずの条件で発火しました: {Describe(fired)}{Environment.NewLine}{History()}");
            }
        }
    }

    private T WaitFor<T>(Func<T, bool> match, string what, TimeSpan? timeout) where T : EventArgs
    {
        TimeSpan remaining = timeout ?? DefaultTimeout;
        DateTime deadline = DateTime.UtcNow + remaining;
        while (true)
        {
            remaining = deadline - DateTime.UtcNow;
            if (remaining <= TimeSpan.Zero || Next(remaining) is not { } item)
            {
                throw new TimeoutException(
                    $"{what} を待ちましたが来ませんでした。{Environment.NewLine}{History()}");
            }
            if (item is T typed && match(typed))
            {
                return typed;
            }
        }
    }

    /// <summary>失敗時に「何が来ていたのか」を出す。これが無いと実 UIA のテストは調べようがない。</summary>
    public string History()
    {
        // 診断カウンタも一緒に出す。「イベントが来なかった」ときに知りたいのは
        // 「そもそも sweep が走ったのか (= UIA イベントが届いたのか)」であり、
        // それが無いと配送側の問題か判定側の問題かを切り分けられない
        TriggerMonitorDiagnostics d = Monitor.GetDiagnostics();
        // 要素の数もトリガーの数と別に出す。1 つのトリガーが複数の要素にまたがれるので、
        // 「resolved=0/1」だけでは「2 つのうち片方だけ解決できていない」が読み取れない (docs/DESIGN.md §4)
        var text = new StringBuilder(string.Create(CultureInfo.InvariantCulture,
            $"診断: sweeps={d.SweepCount} structureEvents={d.StructureEventCount} " +
            $"subscriptions={d.StructureSubscriptionCount} pathScoped={d.StructureIsPathScoped} " +
            $"orphaned={d.OrphanedTriggerCount} watchedProperties={d.WatchedPropertyCount} " +
            $"resolved={d.ResolvedTriggerCount}/{d.TriggerCount} " +
            $"elements={d.ResolvedElementCount}/{d.ElementSlotCount}{Environment.NewLine}"));
        text.Append("これまでに観測したイベント:");
        if (_seen.Count == 0)
        {
            text.Append(" (なし)");
        }
        foreach (EventArgs item in _seen)
        {
            text.Append(Environment.NewLine).Append("  ").Append(Describe(item));
        }
        foreach (Exception ex in _unhandled)
        {
            text.Append(Environment.NewLine).Append("  !! ").Append(ex.Message);
        }
        return text.ToString();
    }

    private static string Describe(EventArgs item) => item switch
    {
        TriggerFiredEventArgs e => string.Create(CultureInfo.InvariantCulture,
            $"Fired[{e.TriggerId}] {e.On} '{e.OldValue}' -> '{e.NewValue}'"),
        TriggerResolutionChangedEventArgs e => string.Create(CultureInfo.InvariantCulture,
            $"Resolution[{e.TriggerId}] resolved={e.IsResolved} '{e.Message}'"),
        _ => item.ToString() ?? "?",
    };

    /// <summary>
    /// monitor だけを捨て、観測したイベントのキューは残す。
    /// 「止めた monitor がもう鳴らないこと」を、その monitor のキューで確かめるために要る。
    /// </summary>
    public ValueTask DisposeMonitorAsync() => Monitor.DisposeAsync();

    public async ValueTask DisposeAsync()
    {
        await Monitor.DisposeAsync().ConfigureAwait(false);
        _events.Dispose();
    }
}
