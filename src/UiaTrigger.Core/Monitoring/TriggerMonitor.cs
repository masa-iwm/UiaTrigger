using System.Runtime.InteropServices;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;
using UiaTrigger.Inspection;
using UiaTrigger.Interop;
using UiaTrigger.Models;
using UiaTrigger.Resolution;
using UiaTrigger.Resources;
using UiaTrigger.Serialization;
using UiaTrigger.Threading;

namespace UiaTrigger.Monitoring;

/// <summary>
/// Watches UI elements in other applications and raises an event when a trigger's condition is met.
/// </summary>
/// <remarks>
/// <para>
/// All UI Automation work happens on a <see cref="UiaSession"/>'s dedicated thread. A monitor
/// created with <c>new</c> owns a session of its own; one created with
/// <see cref="UiaSession.CreateMonitor"/> shares the caller's, which is what a host that also
/// inspects elements should do.
/// </para>
/// <para>
/// Monitoring is event driven: the monitor subscribes to the UI Automation events that can affect
/// its triggers and re-resolves when the tree changes. Triggers can be added and removed while it
/// runs, without disturbing the others.
/// </para>
/// <para>
/// **The monitor never polls on its own initiative.** A trigger that sets
/// <see cref="TriggerDefinition.PollInterval"/> has its already-resolved elements re-read on that
/// cadence, which is the way out when an application does not raise the event a trigger needs.
/// Finding elements is event driven either way — polling never drives resolution.
/// </para>
/// </remarks>
public sealed class TriggerMonitor : IAsyncDisposable
{
    private readonly TriggerMonitorOptions _options;
    private readonly UiaSession _session;
    private readonly bool _ownsSession;
    private readonly TimeProvider _timeProvider;
    private readonly ILogger? _logger;
    private readonly UiaDispatcher _dispatcher;
    private readonly Dictionary<string, TriggerRuntime> _runtimes = new(StringComparer.Ordinal);
    private readonly SerialEventQueue _events;
    private readonly SweepDebouncer _sweep;
    /// <summary>
    /// 購読を失ったトリガーを拾い直す再試行 (docs/DESIGN.md §8)。
    /// **壊れている間だけ動く** — 健全なときは予約されない。
    /// </summary>
    private readonly SubscriptionRepair _repair;
    private readonly IWindowSource _windowSource = new Win32WindowSource();
    private AutomationEventHandler? _windowOpenedHandler;
    private AutomationEventHandler? _windowClosedHandler;
    // 監視状態は診断 API から任意のスレッドで読まれるので volatile。
    // 書き込むのは常に UiaDispatcher スレッド 1 本だけである
    private volatile bool _started;
    private volatile bool _disposed;
    private int _sweepCount;
    private int _structureEventCount;
    private int _structureSubscriptionCount;
    private int _structurePathScoped;
    private int _triggerCount;
    private int _resolvedCount;
    private int _orphanedCount;
    private int _watchedPropertyCount;
    private int _slotCount;
    private int _resolvedSlotCount;
    private long _suppressedFireCount;
    private long _pollCount;
    private long _polledReadCount;

    /// <summary>購読を失ったトリガーを拾い直すまでの最初の間隔。</summary>
    /// <remarks>
    /// 公開の設定にしていない。これは調整つまみではなく**復旧の経路**であり、
    /// 利用者が触って意味がある値ではない。必要になれば
    /// <see cref="TriggerMonitorOptions"/> へ足せる (プロパティの追加は破壊的変更ではない)。
    /// </remarks>
    private static readonly TimeSpan RepairInitialInterval = TimeSpan.FromSeconds(2);

    /// <summary>再試行の間隔の上限。塞がったままの相手を叩き続けないための頭打ち。</summary>
    private static readonly TimeSpan RepairMaxInterval = TimeSpan.FromSeconds(30);

    /// <summary>Creates a monitor that owns its own <see cref="UiaSession"/>.</summary>
    /// <param name="options">
    /// Settings, or null for the defaults. <see cref="TriggerMonitorOptions.Session"/> configures
    /// the session this monitor creates.
    /// </param>
    /// <remarks>
    /// Prefer <see cref="UiaSession.CreateMonitor"/> when the host already has a session: each
    /// session is another automation thread and another set of automation objects.
    /// </remarks>
    public TriggerMonitor(TriggerMonitorOptions? options = null)
        : this(new UiaSession((options ?? new TriggerMonitorOptions()).Session), options, ownsSession: true)
    {
    }

    internal TriggerMonitor(UiaSession session, TriggerMonitorOptions? options)
        : this(session, options, ownsSession: false)
    {
    }

    private TriggerMonitor(UiaSession session, TriggerMonitorOptions? options, bool ownsSession)
    {
        _session = session;
        _ownsSession = ownsSession;
        _options = options ?? new TriggerMonitorOptions();
        _timeProvider = session.TimeProvider;
        _logger = session.Logger;
        _dispatcher = session.Dispatcher;
        _events = new SerialEventQueue(RaiseUnhandledException);
        _sweep = new SweepDebouncer(_options.Debounce, () => _dispatcher.Post(Sweep), _timeProvider);
        _repair = new SubscriptionRepair(
            RepairInitialInterval, RepairMaxInterval, () => _dispatcher.Post(ScheduleSweep), _timeProvider);
        _dispatcher.UnhandledException += RaiseUnhandledException;
    }

    /// <summary>
    /// Raised when a trigger's condition is met.
    /// </summary>
    /// <remarks>
    /// Handlers run on a single background worker, one at a time and in the order the conditions
    /// were detected. An exception thrown by a handler is caught and reported through
    /// <see cref="UnhandledException"/> instead of terminating the process; subsequent events are
    /// still delivered. Because delivery is serialized, a slow handler delays later events —
    /// hand off to your own queue if that matters.
    /// </remarks>
    public event EventHandler<TriggerFiredEventArgs>? TriggerFired;

    /// <summary>
    /// Raised when the resolution state of a trigger's target element changes (for diagnostics
    /// and display). Delivered on the same serialized worker as <see cref="TriggerFired"/>, so the
    /// relative order of the two is preserved.
    /// </summary>
    /// <remarks>
    /// <see cref="TriggerResolutionChangedEventArgs.Message"/> is the place to look when a trigger
    /// never fires. It names the reason, including the one that is otherwise invisible: an elevated
    /// application's windows cannot be inspected by a client that is not itself elevated.
    /// </remarks>
    public event EventHandler<TriggerResolutionChangedEventArgs>? ResolutionChanged;

    /// <summary>
    /// Raised for exceptions that have nowhere else to go: failures on the internal UIA thread and
    /// exceptions thrown by <see cref="TriggerFired"/> / <see cref="ResolutionChanged"/> handlers.
    /// </summary>
    public event Action<Exception>? UnhandledException;

    /// <summary>The session whose automation thread this monitor runs on.</summary>
    public UiaSession Session => _session;

    /// <summary>
    /// 解決の単位 = 相異なる 1 つの (Window, Locator) (docs/DESIGN.md §4)。
    ///
    /// 1 つのトリガーが複数の要素・複数のウィンドウにまたがれるので、
    /// 「解決した要素」まわりの状態はトリガーではなくここに属する。
    /// 句が要素を上書きしていなければスロットは 1 つだけで、単一要素のトリガーと同じ形になる。
    /// </summary>
    private sealed class ElementSlot
    {
        /// <summary>この要素を探すときのウィンドウ (句が上書きしていなければトリガーの既定)。</summary>
        public required WindowIdentity Window { get; init; }

        /// <summary>この要素を探すときの経路 (同上)。</summary>
        public required ElementLocator Locator { get; init; }

        /// <summary>
        /// 購読する UIA プロパティ ID
        /// (このスロットの、<see cref="PropertyClause.Watch"/> が true の句から導いた重複なしの集合)。
        /// </summary>
        public required int[] PropertyIds { get; init; }

        public IElementNode? Element;
        /// <summary>解決時点の安定属性。ゾンビ要素の検出に使う (docs/DESIGN.md A8)。</summary>
        public ElementIdentity Identity;
        /// <summary>
        /// 解決した要素が属するウィンドウの HWND。
        /// 破棄されたウィンドウの UIA 要素は答え続けることがあるので、
        /// ウィンドウの生存だけは OS に直接訊く (docs/DESIGN.md A21)。
        /// </summary>
        public nint WindowHandle;
        public PropertyChangedEventHandler? PropertyHandler;
        public StructureChangedEventHandler? StructureHandler;
        /// <summary>StructureChanged を購読している要素 (経路購読では複数)。解放責任もここにある。</summary>
        public readonly List<IElementNode> StructureElements = [];
        public nint StructureHandlerHwnd;
        /// <summary>true = 解決済みの経路購読 / false = ウィンドウ全体の Subtree 購読。</summary>
        public bool StructureIsPathScoped;
        public ElementPropertySnapshot? LastSnapshot;

        /// <summary>
        /// <see cref="TriggerProperty.Custom"/> の直前値 (鍵は <see cref="PropertyClause.CustomPropertyId"/>)。
        ///
        /// <para>
        /// Custom はスナップショットに入らないので、<see cref="WatchedValuesChanged"/> は
        /// 「変わったか」を答えられず**無条件に true** を返している。イベント経路ではそれでよい
        /// (UIA が何かの変化を伝えてきている) が、**ポーリング経路では誰も何も言っていない**ため、
        /// そのままだと毎周期鳴る。ここに前回値を残しておくのがその埋め合わせである。
        /// </para>
        /// <para>
        /// 鍵が句ではなく**プロパティ ID** なのは、同じスロットの 2 つの句が同じ ID を
        /// 指しうるからである (値は同じなので 1 つ持てば足りる)。
        /// </para>
        /// </summary>
        public Dictionary<int, ClauseValue>? CustomValues;

        public bool IsResolved => Element is not null;
    }

    private sealed class TriggerRuntime
    {
        public required TriggerDefinition Definition { get; init; }

        /// <summary>検証済み・Regex コンパイル済みの句。</summary>
        public required List<CompiledClause> Clauses { get; init; }

        /// <summary>解決の単位。句が要素を上書きしていなければ 1 つだけ。</summary>
        public required ElementSlot[] Slots { get; init; }

        /// <summary>句の添字 → その句が読むスロットの添字。<see cref="Clauses"/> と同じ長さ。</summary>
        public required int[] ClauseSlots { get; init; }

        /// <summary>句の実効名。<see cref="Clauses"/> と同じ長さで、式が指す名前と同じものである。</summary>
        public required string[] ClauseNames { get; init; }

        /// <summary>
        /// 直前の評価で各句が読んだ値。**使い回す作業領域である** (docs/DESIGN.md §4)。
        /// </summary>
        /// <remarks>
        /// <para>
        /// <see cref="Evaluate"/> はプロパティの変化ごとに走り、発火するときだけではない。
        /// ここで公開用のリストを作ると、鳴らない周のぶんまで確保することになる。
        /// 公開用に写すのは <see cref="Fire"/> の中だけである — 配送は別スレッドなので、
        /// そこで固めておかないと次の評価に上書きされる。
        /// </para>
        /// <para>
        /// **読む前に必ず <see cref="Evaluate"/> か <see cref="ResetReadings"/> を通ること。**
        /// 前の周の値が残っていると、評価を飛ばした発火 (要素が消えて
        /// <c>lastSnapshot</c> が null のとき) が**古い値を載せる**。
        /// </para>
        /// </remarks>
        public required ClauseValue[] LastValues { get; init; }

        /// <summary>その周に句が評価されたか。短絡した句は false のままになる。</summary>
        public required bool[] LastEvaluated { get; init; }

        /// <summary>
        /// 解析済みの <see cref="TriggerDefinition.Expression"/>。
        /// null なら <see cref="TriggerDefinition.Combine"/> で平坦に結合する。
        /// </summary>
        public ClauseExpressionNode? Expression { get; init; }

        public required MinIntervalGate Gate { get; init; }

        /// <summary>
        /// 利用者が頼んだポーリング。<see cref="TriggerDefinition.PollInterval"/> が
        /// 正のときだけ存在する (既定は null = タイマーを 1 本も作らない)。
        /// </summary>
        public SlotPoller? Poller;

        public string Id => Definition.Id;

        public bool LastMatch;

        /// <summary>利用者がこのトリガーのポーリングを頼んでいるか。</summary>
        public bool WantsPolling => Definition.PollInterval is { Ticks: > 0 };

        /// <summary>スロットが 1 つ残らず解決できているか。</summary>
        public bool IsResolved
        {
            get
            {
                foreach (ElementSlot slot in Slots)
                {
                    if (!slot.IsResolved)
                    {
                        return false;
                    }
                }
                return true;
            }
        }

        /// <summary>このスロットのプロパティ変化を購読する必要があるか。</summary>
        /// <remarks>
        /// ElementRemoved も Watch な句があれば購読する。発火源にするためではなく
        /// (OnPropertyChanged は ElementRemoved では鳴らさない)、LastSnapshot を最新に
        /// 保つためである — 購読しないと HandleRemoved の「最後に見えていた状態」が
        /// 実際には**解決時の状態**になり、条件が古い値と比較される (docs/DESIGN.md C15)。
        /// ElementAppeared は対象外 — 発火は解決の瞬間だけで、以後の鮮度に意味が無い
        /// </remarks>
        public bool WatchesProperties(ElementSlot slot) =>
            Definition.On is TriggerOn.PropertyChanged or TriggerOn.WhileMatching or TriggerOn.ElementRemoved
                && slot.PropertyIds.Length > 0;

        /// <summary>句が読むスロット。</summary>
        public ElementSlot SlotOf(int clauseIndex) => Slots[ClauseSlots[clauseIndex]];
    }

    /// <summary>
    /// Starts monitoring. Definition errors surface here as <see cref="ArgumentException"/>, on the
    /// calling thread, before any UIA work is queued.
    /// </summary>
    /// <param name="triggers">
    /// Trigger definitions, or null to start empty and add them with <see cref="AddAsync"/>. Each
    /// <see cref="TriggerDefinition.Id"/> must be non-empty and unique.
    /// </param>
    /// <param name="cancellationToken">
    /// Cancels the start request while it is still queued. Once the start has begun on the
    /// automation thread it runs to completion — cancellation never leaves monitoring
    /// half-started.
    /// </param>
    public Task StartAsync(IEnumerable<TriggerDefinition>? triggers = null, CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        var runtimes = new List<TriggerRuntime>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (TriggerDefinition definition in triggers ?? [])
        {
            ArgumentNullException.ThrowIfNull(definition);
            if (string.IsNullOrEmpty(definition.Id))
            {
                throw new ArgumentException(Strings.Error_TriggerIdRequired, nameof(triggers));
            }
            if (!seen.Add(definition.Id))
            {
                throw new ArgumentException(
                    Message.Format(Strings.Error_DuplicateTriggerId, definition.Id), nameof(triggers));
            }
            // 検証 (と Regex コンパイル) は呼び出し元スレッドで行い、早期にエラーを返す
            runtimes.Add(CreateRuntime(definition));
        }
        return _dispatcher.InvokeAsync(() => StartCore(runtimes), cancellationToken);
    }

    /// <summary>
    /// Adds one trigger, resolving it immediately if monitoring has already started.
    /// </summary>
    /// <remarks>
    /// Only this trigger's subscriptions are touched; every other trigger keeps its working
    /// subscriptions.
    /// </remarks>
    /// <exception cref="ArgumentException">
    /// The definition is invalid, or its <see cref="TriggerDefinition.Id"/> is already monitored.
    /// </exception>
    public Task AddAsync(TriggerDefinition definition, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(definition);
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (string.IsNullOrEmpty(definition.Id))
        {
            throw new ArgumentException(Strings.Error_TriggerIdRequired, nameof(definition));
        }
        TriggerRuntime runtime = CreateRuntime(definition);
        return _dispatcher.InvokeAsync(() => AddCore(runtime), cancellationToken);
    }

    /// <summary>
    /// Removes one trigger and releases only its subscriptions.
    /// </summary>
    /// <returns>True when a trigger with that id was being monitored.</returns>
    public Task<bool> RemoveAsync(string triggerId, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(triggerId);
        ObjectDisposedException.ThrowIf(_disposed, this);
        return _dispatcher.InvokeAsync(() => RemoveCore(triggerId), cancellationToken);
    }

    /// <summary>The ids of the triggers currently being monitored.</summary>
    public Task<IReadOnlyList<string>> GetTriggerIdsAsync(CancellationToken cancellationToken = default) =>
        _dispatcher.InvokeAsync<IReadOnlyList<string>>(() => _runtimes.Keys.ToArray(), cancellationToken);

    /// <summary>Stops monitoring and releases every UIA subscription.</summary>
    public Task StopAsync(CancellationToken cancellationToken = default) =>
        _dispatcher.InvokeAsync(StopCore, cancellationToken);

    /// <summary>
    /// A snapshot of what the monitor is currently doing.
    /// </summary>
    /// <remarks>
    /// The counters are the only way to observe a property this library depends on but cannot state
    /// positively: that it does **not** receive events it has no use for. A subscription scoped
    /// too widely costs throughput on a busy application and shows up here as a structure-event
    /// count that keeps climbing while nothing relevant changes.
    /// </remarks>
    public TriggerMonitorDiagnostics GetDiagnostics() => new()
    {
        IsRunning = _started,
        TriggerCount = Volatile.Read(ref _triggerCount),
        ResolvedTriggerCount = Volatile.Read(ref _resolvedCount),
        SweepCount = Volatile.Read(ref _sweepCount),
        StructureEventCount = Volatile.Read(ref _structureEventCount),
        StructureSubscriptionCount = Volatile.Read(ref _structureSubscriptionCount),
        StructureIsPathScoped = Volatile.Read(ref _structurePathScoped) != 0,
        OrphanedTriggerCount = Volatile.Read(ref _orphanedCount),
        WatchedPropertyCount = Volatile.Read(ref _watchedPropertyCount),
        ElementSlotCount = Volatile.Read(ref _slotCount),
        ResolvedElementCount = Volatile.Read(ref _resolvedSlotCount),
        SuppressedFireCount = Interlocked.Read(ref _suppressedFireCount),
        PollCount = Interlocked.Read(ref _pollCount),
        PolledReadCount = Interlocked.Read(ref _polledReadCount),
    };

    /// <summary>
    /// Stops monitoring. Events that were already detected are delivered to their handlers before
    /// this completes, so a handler that never returns will block disposal.
    /// </summary>
    /// <remarks>
    /// The session is disposed only if this monitor created it; a shared session outlives its
    /// monitors.
    /// </remarks>
    public async ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return;
        }
        _disposed = true;
        try
        {
            await _dispatcher.InvokeAsync(StopCore).ConfigureAwait(false);
        }
        catch (ObjectDisposedException)
        {
        }
        _dispatcher.UnhandledException -= RaiseUnhandledException;
        _sweep.Dispose();
        _repair.Dispose();
        if (_ownsSession)
        {
            await _session.DisposeAsync().ConfigureAwait(false);
        }
        // 投入済みの発火をハンドラへ流し切ってから戻る
        await _events.DisposeAsync().ConfigureAwait(false);
    }

    /// <summary>通知先が投げてもここで止める (通知先の例外でプロセスを落とさない)。</summary>
    private void RaiseUnhandledException(Exception exception)
    {
        try
        {
            UnhandledException?.Invoke(exception);
        }
        catch
        {
        }
    }

    private TriggerRuntime CreateRuntime(TriggerDefinition definition)
    {
        string id = definition.Id;
        ArgumentNullException.ThrowIfNull(definition.Window);
        ArgumentNullException.ThrowIfNull(definition.Locator);
        ArgumentNullException.ThrowIfNull(definition.Clauses);

        // StoppedMatching は TriggerFiredEventArgs.On に載せるためだけの値である。
        // 定義の lifecycle として受けると「立ち下がりだけの WhileMatching」という
        // 二重の書き方が生まれ、以後すべての On 分岐が 2 値を見ることになる
        if (definition.On == TriggerOn.StoppedMatching)
        {
            throw new ArgumentException(Message.Format(Strings.Error_StoppedMatchingIsEventOnly, id));
        }
        // 黙って効かない設定を残さない (Error_PollIntervalNotApplicable と同じ規律)。
        // 立ち下がりは WhileMatching の水準 (LastMatch) から作られるので、他の lifecycle に
        // このフラグが載っていても鳴る日は来ない
        if (definition.NotifyOnStoppedMatching && definition.On != TriggerOn.WhileMatching)
        {
            throw new ArgumentException(
                Message.Format(Strings.Error_NotifyOnStoppedMatchingRequiresWhileMatching, id, definition.On));
        }
        if (definition.MinInterval is { Ticks: < 0 })
        {
            throw new ArgumentException(Message.Format(Strings.Error_MinIntervalNegative, id));
        }
        if (definition.PollInterval is { Ticks: < 0 })
        {
            throw new ArgumentException(Message.Format(Strings.Error_PollIntervalNegative, id));
        }
        // 出現・消滅で鳴るトリガーにポーリングは効かない。ポーリングが読み直すのは
        // **解決済み**の要素だけで、解決そのものはイベント駆動のままだからである。
        // 黙って効かない設定を残さない (Error_UnreferencedClause / Error_ClausesRequired と同じ規律)
        if (definition.PollInterval is { Ticks: > 0 } &&
            definition.On is TriggerOn.ElementAppeared or TriggerOn.ElementRemoved)
        {
            throw new ArgumentException(
                Message.Format(Strings.Error_PollIntervalNotApplicable, id, definition.On));
        }
        // 出現・消滅で鳴るトリガーの**対象**は定義の要素である (SlotBuilder がそれを
        // スロット 0 に置く)。句が自分の要素を名指すと、「消えたのはスロット 0」なのに
        // 「イベントに載る値は先頭の句の要素」という食い違いが起きる — どちらを指しているのか
        // 言えないイベントになるので、組み合わせとして弾く。
        // PollInterval と同じ規律である: **黙って意味が変わる設定を残さない** (docs/DESIGN.md §4)
        if (definition.On is TriggerOn.ElementAppeared or TriggerOn.ElementRemoved &&
            definition.Clauses.Any(c => c.Window is not null || c.Locator is not null))
        {
            throw new ArgumentException(
                Message.Format(Strings.Error_ClauseElementNotApplicable, id, definition.On));
        }
        string[] names = ClauseNames(id, definition.Clauses);
        var builder = new SlotBuilder(definition);

        var compiled = new List<CompiledClause>(definition.Clauses.Count);
        var clauseSlots = new int[definition.Clauses.Count];
        int watched = 0;
        for (int i = 0; i < definition.Clauses.Count; i++)
        {
            PropertyClause clause = definition.Clauses[i];
            compiled.Add(CompileClause(id, clause));
            clauseSlots[i] = builder.SlotFor(clause);

            // 監視しない句は購読を作らない。この 1 行が「A が存在していて、B の値が
            // 変化したとき」を書けるようにしている — 購読は Property から作られ Op を
            // 見ないので、存在だけを見るつもりの Always の句もそのままでは発火源になる
            if (!clause.Watch)
            {
                continue;
            }
            builder.Watch(clauseSlots[i], PropertyReader.ToPropertyId(clause));
            watched++;
        }

        // PropertyChanged / WhileMatching は「何を購読するか」を句から決めるので、監視する句が
        // 無いと購読対象が存在しない = 永久に発火しないトリガーになる。設定ミスとして即座に弾く。
        // 句が 0 個のときだけでなく、全句が Watch = false のときも同じ形である
        if (definition.On is TriggerOn.PropertyChanged or TriggerOn.WhileMatching && watched == 0)
        {
            throw new ArgumentException(Message.Format(Strings.Error_ClausesRequired, id, definition.On));
        }

        ElementSlot[] slots = builder.Build();
        // ウィンドウの要求はスロットごとに確かめる。定義の既定だけを見ていると、
        // 句が上書きしたウィンドウの誤りが「永久に解決しない」としてしか現れない
        foreach (ElementSlot slot in slots)
        {
            if (string.IsNullOrEmpty(slot.Window.ProcessName) &&
                slot.Window.ProcessNameMatch == MatchStrength.Required)
            {
                throw new ArgumentException(Message.Format(Strings.Error_ProcessNameRequired, id));
            }
            if (WindowCandidateCache.HasUnsatisfiableRequirement(slot.Window))
            {
                throw new ArgumentException(Message.Format(Strings.Error_WindowRequirementUnsatisfiable, id));
            }
        }

        return new TriggerRuntime
        {
            Definition = definition,
            Clauses = compiled,
            Slots = slots,
            ClauseSlots = clauseSlots,
            ClauseNames = names,
            LastValues = new ClauseValue[compiled.Count],
            LastEvaluated = new bool[compiled.Count],
            Expression = ParseExpression(id, definition.Expression, names),
            Gate = new MinIntervalGate(definition.MinInterval, _timeProvider),
        };
    }

    /// <summary>
    /// 句を実効 (Window, Locator) でまとめてスロットを作る (docs/DESIGN.md §4)。
    ///
    /// 同じ要素を指す句が別々のスロットになると、**1 要素にプロパティハンドラが 2 本立ち、
    /// 1 回の変化で 2 回発火する**。ただの無駄ではなく誤りである。
    /// </summary>
    private sealed class SlotBuilder
    {
        private readonly TriggerDefinition _definition;
        private readonly List<string> _keys = [];
        private readonly List<WindowIdentity> _windows = [];
        private readonly List<ElementLocator> _locators = [];
        private readonly List<List<int>> _propertyIds = [];

        public SlotBuilder(TriggerDefinition definition)
        {
            _definition = definition;
            // 「トリガーの既定の要素」が要るのは次のとき。要るなら必ず先頭に置き、
            // 上書きしていない句がそこへ集まるようにする:
            //   ・句が 1 つも無い (出現・削除だけを見るトリガー)
            //   ・On が ElementAppeared / ElementRemoved — **どの**要素が出現したのかを言う必要がある
            // 逆に、全句が要素を上書きしているトリガーで既定まで解決してはならない。
            // 誰も要らない要素の解決と構造購読が走り、それが存在しなければ
            // IsOrphaned(0, hwnd) が永久に true = SubscriptionRepair が armed のまま =
            // 事実上のポーリングになる (docs/DESIGN.md §8)
            if (definition.Clauses.Count == 0 ||
                definition.On is TriggerOn.ElementAppeared or TriggerOn.ElementRemoved)
            {
                Add(definition.Window, definition.Locator);
            }
        }

        public int SlotFor(PropertyClause clause)
        {
            ArgumentNullException.ThrowIfNull(clause);
            WindowIdentity window = clause.Window ?? _definition.Window;
            ElementLocator locator = clause.Locator ?? _definition.Locator;
            int existing = _keys.IndexOf(KeyOf(window, locator));
            return existing >= 0 ? existing : Add(window, locator);
        }

        public void Watch(int slot, int propertyId)
        {
            if (!_propertyIds[slot].Contains(propertyId))
            {
                _propertyIds[slot].Add(propertyId);
            }
        }

        public ElementSlot[] Build()
        {
            var slots = new ElementSlot[_keys.Count];
            for (int i = 0; i < slots.Length; i++)
            {
                slots[i] = new ElementSlot
                {
                    Window = _windows[i],
                    Locator = _locators[i],
                    PropertyIds = [.. _propertyIds[i]],
                };
            }
            return slots;
        }

        private int Add(WindowIdentity window, ElementLocator locator)
        {
            ArgumentNullException.ThrowIfNull(window);
            ArgumentNullException.ThrowIfNull(locator);
            _keys.Add(KeyOf(window, locator));
            _windows.Add(window);
            _locators.Add(locator);
            _propertyIds.Add([]);
            return _keys.Count - 1;
        }

        /// <summary>
        /// 2 つの句が同じ要素を指すかを決める鍵。
        ///
        /// **参照同値では駄目である。**<see cref="WindowIdentity"/> と
        /// <see cref="ElementLocator"/> は <c>Equals</c> を持たない可変クラスなので、
        /// JSON から読んだ「同じ要素を指す 2 つの句」は別オブジェクトになる。
        /// **手書きの構造比較も駄目で**、どちらかにプロパティが 1 つ増えた日に黙って腐る
        /// (増えたぶんを見落として、違う要素を同じと見なす)。
        /// シリアライザに任せれば全プロパティを自動で覆う。
        /// <see cref="TriggerDefinition"/> は source-gen の context に登録済みなので、
        /// その殻に詰めればここだけのために型を増やさずに済む。
        /// 走るのはトリガーを追加するときの句あたり 1 回だけである。
        /// </summary>
        private static string KeyOf(WindowIdentity window, ElementLocator locator) =>
            JsonSerializer.Serialize(
                new TriggerDefinition { Id = string.Empty, Window = window, Locator = locator },
                TriggerJsonContext.Default.TriggerDefinition);
    }

    /// <summary>
    /// 句の名前を確定して検証する。
    ///
    /// 式を書かないトリガーでも重複を弾くこと。あとから式を足したときに
    /// 「どちらの句を指しているのか」が決まらない定義が、すでに保存済みになっている状態を作らない。
    /// </summary>
    private static string[] ClauseNames(string id, List<PropertyClause> clauses)
    {
        var names = new string[clauses.Count];
        for (int i = 0; i < clauses.Count; i++)
        {
            PropertyClause clause = clauses[i];
            ArgumentNullException.ThrowIfNull(clause);

            string name = ClauseExpression.EffectiveName(clause, i);
            if (!ClauseExpression.IsValidName(name))
            {
                throw new ArgumentException(Message.Format(Strings.Error_InvalidClauseName, id, name));
            }
            for (int j = 0; j < i; j++)
            {
                if (string.Equals(names[j], name, StringComparison.Ordinal))
                {
                    throw new ArgumentException(Message.Format(Strings.Error_DuplicateClauseName, id, name));
                }
            }
            names[i] = name;
        }
        return names;
    }

    /// <summary>式を解析する。空白だけ・null は「式なし」= <c>Combine</c> に落ちる。</summary>
    private static ClauseExpressionNode? ParseExpression(string id, string? expression, string[] names)
    {
        if (string.IsNullOrWhiteSpace(expression))
        {
            return null;
        }

        ClauseExpressionResult parsed = ClauseExpression.Parse(expression, names);
        if (!parsed.IsValid)
        {
            throw new ArgumentException(Message.Format(Strings.Error_Expression, id, parsed.Error));
        }
        // 参照されない句は解決も購読もされるのに結果に一切効かない。
        // 黙って放っておくと「なぜか条件が効かない」として最も長く隠れる
        for (int i = 0; i < names.Length; i++)
        {
            if (!parsed.ReferencedIndices.Contains(i))
            {
                throw new ArgumentException(Message.Format(Strings.Error_UnreferencedClause, id, names[i]));
            }
        }
        return parsed.Root;
    }

    private CompiledClause CompileClause(string id, PropertyClause clause)
    {
        if (clause.Property == TriggerProperty.Custom && clause.CustomPropertyId == 0)
        {
            throw new ArgumentException(Message.Format(Strings.Error_CustomPropertyIdRequired, id));
        }

        switch (clause.Op)
        {
            case ComparisonOp.Between:
            case ComparisonOp.NotBetween:
                if (clause.Low is null || clause.High is null)
                {
                    throw new ArgumentException(Message.Format(Strings.Error_LowHighRequired, id, clause.Op));
                }
                break;
            case ComparisonOp.GreaterThan:
            case ComparisonOp.LessThan:
            case ComparisonOp.LessOrEqual:
            case ComparisonOp.GreaterOrEqual:
                if (clause.Value is null)
                {
                    throw new ArgumentException(Message.Format(Strings.Error_ValueRequired, id, clause.Op));
                }
                break;
            case ComparisonOp.Equals:
            case ComparisonOp.NotEquals:
                if (clause.Text is null)
                {
                    throw new ArgumentException(Message.Format(Strings.Error_TextRequired, id, clause.Op));
                }
                break;
            case ComparisonOp.RegexMatch:
            case ComparisonOp.RegexNotMatch:
                if (string.IsNullOrEmpty(clause.Text))
                {
                    throw new ArgumentException(Message.Format(Strings.Error_RegexPatternRequired, id, clause.Op));
                }
                try
                {
                    return new CompiledClause
                    {
                        Clause = clause,
                        Regex = new Regex(
                            clause.Text, RegexOptions.NonBacktracking | RegexOptions.CultureInvariant, _options.RegexTimeout),
                    };
                }
                // NonBacktracking は後方参照・先読みに対して ArgumentException ではなく
                // NotSupportedException を投げる。捕まえ損ねると「定義の誤り」が
                // ローカライズされない別種の例外として呼び出し元に出てしまう
                catch (Exception ex) when (ex is ArgumentException or NotSupportedException)
                {
                    throw new ArgumentException(Message.Format(Strings.Error_InvalidRegex, id, ex.Message), ex);
                }
        }

        return new CompiledClause { Clause = clause };
    }

    // ---- 以下はすべて UiaDispatcher スレッド上 ----

    private UiaContext Ctx => _session.Context;

    private void StartCore(List<TriggerRuntime> runtimes)
    {
        if (_started)
        {
            throw new InvalidOperationException(Strings.Error_AlreadyStarted);
        }
        _started = true;
        if (!Ctx.SupportsTimeouts)
        {
            Log.TimeoutsUnsupported(_logger);
        }

        foreach (TriggerRuntime rt in runtimes)
        {
            // 開始前に AddAsync で足された分と衝突しうる
            if (!_runtimes.TryAdd(rt.Id, rt))
            {
                throw new ArgumentException(Message.Format(Strings.Error_DuplicateTriggerId, rt.Id));
            }
        }

        // ウィンドウの出現・消滅をルートで一括購読し、デバウンス付きで再解決を回す。
        //
        // WindowOpened はトップレベルウィンドウがルートの直接の子なので Children で足りる
        // (Subtree にすると全プロセスの全ウィンドウイベントを受け取る — docs/DESIGN.md B2)。
        //
        // WindowClosed だけは Children にできない。イベントが届く時点で発生源のウィンドウは
        // 既に消えており、UIA が「これはルートの子か」を評価できないためである。
        // 両方を Children に絞ると WindowClosed が一切届かなくなる
        // (T3 の実測で確認済み — docs/DESIGN.md B2)。参照実装の
        // System.Windows.Automation は同じ制約を明示的に例外にしている:
        //   "WindowClosed event is only applicable to RootElement and TreeScope.Subtree"
        // 手書き interop には検証が無いので、黙って何も来なくなる形で壊れる。
        _windowOpenedHandler = new AutomationEventHandler((_, _) => _dispatcher.Post(ScheduleSweep));
        _windowClosedHandler = new AutomationEventHandler((_, _) => _dispatcher.Post(ScheduleSweep));
        Ctx.Automation.AddAutomationEventHandler(UiaIds.WindowOpenedEvent, Ctx.Root, TreeScope.Children, null, _windowOpenedHandler);
        Ctx.Automation.AddAutomationEventHandler(UiaIds.WindowClosedEvent, Ctx.Root, TreeScope.Subtree, null, _windowClosedHandler);

        var windows = NewCandidateCache();
        foreach (TriggerRuntime rt in _runtimes.Values)
        {
            ResolveOrWait(rt, windows, initial: true);
            // 解決を試みたあとに張る。頼まれていないトリガーではタイマーを 1 本も作らない
            StartPolling(rt);
        }
        UpdateDiagnostics();
        Log.MonitorStarted(_logger, _runtimes.Count, !_ownsSession);
    }

    private void AddCore(TriggerRuntime runtime)
    {
        if (_runtimes.ContainsKey(runtime.Id))
        {
            throw new ArgumentException(Message.Format(Strings.Error_DuplicateTriggerId, runtime.Id));
        }
        _runtimes.Add(runtime.Id, runtime);
        Log.TriggerAdded(_logger, runtime.Id);
        if (_started)
        {
            // 1 件だけ足すのに全体を止める必要は無い。この 1 件だけを解決する
            ResolveOrWait(runtime, NewCandidateCache(), initial: true);
            StartPolling(runtime);
        }
        UpdateDiagnostics();
    }

    private bool RemoveCore(string triggerId)
    {
        if (!_runtimes.Remove(triggerId, out TriggerRuntime? rt))
        {
            return false;
        }
        ReleaseTrigger(rt);
        Log.TriggerRemoved(_logger, triggerId);
        UpdateDiagnostics();
        return true;
    }

    /// <summary>
    /// 1 回の解決パスの間だけ生きる候補キャッシュ。
    /// これを全トリガーで共有することで、ウィンドウ列挙と OpenProcess がパスあたり 1 回になる
    /// (docs/DESIGN.md B4)。パスをまたいで使い回すとウィンドウの増減を見落とす。
    /// </summary>
    private WindowCandidateCache NewCandidateCache() => new(_windowSource, _options.Resolver);

    private void StopCore()
    {
        bool wasStarted = _started;
        if (wasStarted)
        {
            // RemoveAllEventHandlers は使わない。セッションを共有している場合、
            // **他の購読者 (ピッカーや別の TriggerMonitor) の購読まで一緒に消える**。
            // 自分が張ったものだけを、張った単位で外す (docs/DESIGN.md C1)
            RemoveWindowHandler(UiaIds.WindowOpenedEvent, ref _windowOpenedHandler);
            RemoveWindowHandler(UiaIds.WindowClosedEvent, ref _windowClosedHandler);

            foreach (TriggerRuntime rt in _runtimes.Values)
            {
                ReleaseTrigger(rt);
                // 停止をまたいでレート制限を持ち越さない (再開直後の 1 回目は必ず通す)
                rt.Gate.Reset();
            }
        }
        // 開始前に AddAsync された分もここで捨てる (残すと次の StartAsync が重複で落ちる)。
        // 購読は張っていないので UiaContext には触らない
        _runtimes.Clear();
        _started = false;
        // これを忘れると、停止時に残っていたデバウンス予約のせいで
        // 再開後の最初の sweep 要求が黙って捨てられる (docs/DESIGN.md A14)
        _sweep.Reset();
        // 復旧の再試行も同じ理由で解く。あわせて間隔も最初へ戻す
        _repair.Recovered();
        UpdateDiagnostics();
        if (wasStarted)
        {
            Log.MonitorStopped(_logger);
        }
    }

    private void RemoveWindowHandler(int eventId, ref AutomationEventHandler? handler)
    {
        if (handler is null)
        {
            return;
        }
        try
        {
            Ctx.Automation.RemoveAutomationEventHandler(eventId, Ctx.Root, handler);
        }
        catch (COMException)
        {
        }
        handler = null;
    }

    /// <summary>1 トリガーぶんの購読と要素を手放す (他のトリガーには触らない)。</summary>
    private void ReleaseTrigger(TriggerRuntime rt)
    {
        // ポーリングもこのトリガーが抱えているものの 1 つ。停止でも削除でもここを通る
        StopPolling(rt);
        foreach (ElementSlot slot in rt.Slots)
        {
            ReleaseSlot(rt, slot);
        }
        rt.LastMatch = false;
    }

    /// <summary>1 スロットぶんの購読と要素を手放す (同じトリガーの他のスロットには触らない)。</summary>
    private void ReleaseSlot(TriggerRuntime rt, ElementSlot slot)
    {
        RemovePropertySubscription(slot);
        RemoveStructureSubscription(rt, slot);
        Ctx.Tree.Release(slot.Element);
        slot.Element = null;
    }

    private void RemovePropertySubscription(ElementSlot slot)
    {
        if (slot.Element is not null && slot.PropertyHandler is not null)
        {
            try
            {
                Ctx.Automation.RemovePropertyChangedEventHandler(
                    UiaElementNode.Unwrap(slot.Element), slot.PropertyHandler);
            }
            catch (COMException)
            {
            }
        }
        slot.PropertyHandler = null;
    }

    private void ScheduleSweep()
    {
        if (_started)
        {
            _sweep.Schedule();
        }
    }

    /// <summary>
    /// 利用者が頼んでいれば、このトリガーのポーリングを始める (頼んでいなければ何もしない)。
    /// </summary>
    private void StartPolling(TriggerRuntime rt)
    {
        if (!rt.WantsPolling || rt.Poller is not null)
        {
            return;
        }
        rt.Poller = new SlotPoller(
            rt.Definition.PollInterval!.Value, () => _dispatcher.Post(() => PollRound(rt)), _timeProvider);
        rt.Poller.Schedule();
        Log.PollingStarted(_logger, rt.Id, rt.Poller.Interval.TotalMilliseconds);
    }

    /// <summary>
    /// ポーリングを止める。停止をまたいで持ち越さない (<c>_sweep.Reset()</c> と同じ理由 — A14)。
    /// </summary>
    private static void StopPolling(TriggerRuntime rt)
    {
        rt.Poller?.Dispose();
        rt.Poller = null;
    }

    /// <summary>
    /// 利用者が頼んだ 1 周。
    ///
    /// <para>
    /// **やるのは解決済みスロットの読み直しだけで、掃引は走らせない。**
    /// 掃引は全ウィンドウ列挙と <c>OpenProcess</c> を伴い (docs/DESIGN.md B4)、
    /// ピッカーと共有している MTA スレッドの上で相手ごとに止まりうる。
    /// タイマーからそれを叩けば**解決の経路そのものをライブラリの判断でポーリングする**ことになり、
    /// この機能が引く線を跨ぐ。要素の出現は今までどおり
    /// <c>WindowOpened</c> / <c>StructureChanged</c> が拾う。
    /// </para>
    /// <para>
    /// 中身は <see cref="OnPropertyChanged"/> をそのまま使う。**並行する実装を書かないこと** —
    /// 立ち上がり判定・値が実際に変わったかの判定・要素消滅の処理は、あちらに揃っている。
    /// 書き直すことが「毎周期鳴る」の作り方そのものである。
    /// </para>
    /// </summary>
    private void PollRound(TriggerRuntime rt)
    {
        // 次の予約を受け付けられるようにしてから走る (SweepDebouncer と同じ約束)
        rt.Poller?.Reset();
        if (!_started || !_runtimes.ContainsKey(rt.Id))
        {
            return;
        }

        Interlocked.Increment(ref _pollCount);
        foreach (ElementSlot slot in rt.Slots)
        {
            // 未解決スロットは触らない (解決はイベント駆動のまま)。
            // WatchesProperties は ElementAppeared / ElementRemoved に対して既に false なので、
            // それらがここへ来ることはない
            if (!slot.IsResolved || !rt.WatchesProperties(slot))
            {
                continue;
            }
            // 費用は「周」ではなく「読み」で数える。1 周は要素数ぶんの往復である
            Interlocked.Increment(ref _polledReadCount);
            OnPropertyChanged(rt, slot, polled: true);
        }

        // 仕事が終わってから次の周を張る。周期タイマーにすると、相手が止まっている間に
        // 周が積み上がる (SlotPoller のヘッダを参照)
        rt.Poller?.Schedule();
    }

    /// <summary>全トリガーの生存確認と未解決トリガーの再解決。</summary>
    private void Sweep()
    {
        Interlocked.Increment(ref _sweepCount);
        _sweep.Reset();
        if (!_started)
        {
            return;
        }
        // 1 パス = 1 キャッシュ。全トリガーでウィンドウ列挙を共有する (docs/DESIGN.md B4)
        var windows = NewCandidateCache();
        foreach (TriggerRuntime rt in _runtimes.Values)
        {
            // スロットごとに独立して見る。1 つが消えても、他のスロットの購読は巻き添えにしない
            foreach (ElementSlot slot in rt.Slots)
            {
                if (slot.IsResolved)
                {
                    CheckAlive(rt, slot);
                }
                if (!slot.IsResolved)
                {
                    TryResolve(rt, slot, windows, initial: false);
                }
            }
        }
        UpdateDiagnostics();
    }

    /// <summary>解決を試み、できなければ理由付きで「未解決」を通知する。</summary>
    private void ResolveOrWait(TriggerRuntime rt, WindowCandidateCache windows, bool initial)
    {
        foreach (ElementSlot slot in rt.Slots)
        {
            TryResolve(rt, slot, windows, initial);
        }
        if (!rt.IsResolved)
        {
            // 昇格プロセスを見られなかったのなら、それを理由として添える (docs/DESIGN.md A10)。
            // これが無いと「永久に解決しない」という症状しか残らない
            if (windows.InaccessibleProcessCount > 0)
            {
                Log.InaccessibleProcesses(_logger, rt.Id, windows.InaccessibleProcessCount);
            }
            RaiseResolutionChanged(rt, false, ResolutionDiagnostics.DescribeUnresolved(windows.InaccessibleProcessCount));
        }
    }

    /// <summary>
    /// 掴んでいる要素がまだ「同じもの」かを 1 往復で確かめる。
    ///
    /// ProcessId を読んで例外が出ないことだけを見る判定では足りない。要素が生きたまま
    /// 中身だけ作り直される (仮想化リストの再利用など) と、ゾンビ要素を監視し続けることになる
    /// (docs/DESIGN.md A8)。安定属性の組を突き合わせて、変わっていれば手放して再解決に回す。
    /// 往復回数は 1 回のまま増えない。
    /// </summary>
    private void CheckAlive(TriggerRuntime rt, ElementSlot slot)
    {
        // ウィンドウが閉じられたかどうかは OS に直接訊く。ネイティブ UIA プロバイダーでは
        // 破棄されたウィンドウの要素が**しばらく属性を答え続ける**ことがあり、UIA 経由では
        // 判別できない (docs/DESIGN.md A21)。これは競合であって決定的な挙動ではないため、
        // UIA だけに頼ると「対象 = ウィンドウ自身」のトリガーが**ときどき**未解決にならないという形で壊れる。
        // 経路が空のときはここが唯一の手掛かりになる (下の下向き到達性が使えない)
        if (slot.WindowHandle != 0 && !NativeMethods.IsWindow(slot.WindowHandle))
        {
            HandleRemoved(rt, slot, Strings.Status_ElementUnavailable);
            return;
        }
        try
        {
            ElementIdentity current = Ctx.Tree.ReadIdentity(slot.Element!);
            if (current != slot.Identity)
            {
                HandleRemoved(rt, slot, Strings.Status_ElementIdentityChanged);
                return;
            }
            if (!IsStillOnThePath(slot))
            {
                HandleRemoved(rt, slot, Strings.Status_ElementDetached);
            }
        }
        catch (COMException ex) when (ComErrors.IsElementGone(ex))
        {
            HandleRemoved(rt, slot, Strings.Status_ElementUnavailable);
        }
        catch (COMException ex)
        {
            // 一時的なエラー (タイムアウト等) は無視 (次のイベントで再確認)
            Log.LookupFailed(_logger, nameof(CheckAlive), ex);
        }
    }

    private void TryResolve(TriggerRuntime rt, ElementSlot slot, WindowCandidateCache windows, bool initial)
    {
        IElementTree tree = Ctx.Tree;
        ResolvedTarget? target;
        try
        {
            target = ElementResolver.Resolve(tree, windows, slot.Window, slot.Locator, _options.Resolver);
        }
        catch (COMException ex)
        {
            Log.LookupFailed(_logger, nameof(TryResolve), ex);
            target = null;
        }

        if (target is null)
        {
            // 要素は無いがウィンドウがあれば、その配下の構造変化を購読して出現を検知する。
            // 出現位置は分からないので、この場合だけは Subtree で張る
            var window = ElementResolver.ResolveWindow(tree, windows, slot.Window, _options.Resolver);
            if (window is null)
            {
                EnsureStructureSubscription(rt, slot, [], TreeScope.Subtree, 0, pathScoped: false);
            }
            else
            {
                EnsureStructureSubscription(rt, slot, [window.Value.Window], TreeScope.Subtree, window.Value.Hwnd, pathScoped: false);
            }
            return;
        }

        IUIAutomationElement element = UiaElementNode.Unwrap(target.Element);
        ElementPropertySnapshot snapshot;
        try
        {
            snapshot = PropertyReader.ReadSnapshot(Ctx, element);
        }
        catch (COMException)
        {
            // 解決直後に消えた。次のイベントで再解決
            tree.Release(target.Element);
            foreach (IElementNode ancestor in target.Ancestors)
            {
                tree.Release(ancestor);
            }
            return;
        }

        // 解決できたので、経路上の各段だけを購読する (docs/DESIGN.md B3)。
        //
        // スコープは Element|Children。発生源を「子が変わった側の親」と考えて
        // Element だけに絞ってはいけない — 実 UIA では追加・削除された子自身が発生源になる
        // ケースがあり、Element だけだと **構造変化を 1 件も受け取らない**
        // (T3 の実測で確認済み — docs/DESIGN.md B3)。
        // 経路を壊す変化は必ず「経路上のどれかの段の子」が増減する形で起きるので、
        // 各段を Element|Children で張れば取りこぼしがなく、
        // それでいて無関係な部分木の変化は一切拾わない。
        // 経路が空 (対象 = ウィンドウ要素) のときは購読なし — その消滅は
        // WindowClosed で拾えるため
        EnsureStructureSubscription(
            rt, slot, target.Ancestors, TreeScope.Element | TreeScope.Children, target.WindowHandle, pathScoped: true);

        slot.Element = target.Element;
        slot.Identity = ElementIdentity.Of(target.Element);
        slot.WindowHandle = target.WindowHandle;
        slot.LastSnapshot = snapshot;

        if (rt.WatchesProperties(slot))
        {
            slot.PropertyHandler = new PropertyChangedEventHandler(
                (_, _) => _dispatcher.Post(() => OnPropertyChanged(rt, slot)));
            Ctx.Automation.AddPropertyChangedEventHandlerNativeArray(
                element, TreeScope.Element, null, slot.PropertyHandler, slot.PropertyIds, slot.PropertyIds.Length);
        }

        Log.TriggerResolved(_logger, rt.Id, target.Ancestors.Count, target.FoundBySearch);

        // 「解決した」と通知するのは全スロットが揃ったときだけ。
        // ただし**評価は待たない** — 未解決スロットの句は不成立なので、
        // a && !b の !b は「b が解決していないこと」そのものが条件である。
        // 揃うまで評価しないと ! が presence に対して永久に使えない
        if (rt.IsResolved)
        {
            RaiseResolutionChanged(rt, true, Strings.Status_ElementResolved);
        }

        bool matched = Evaluate(rt);
        bool wasMatching = rt.LastMatch;
        rt.LastMatch = matched;

        bool allowFire = !initial || _options.FireOnInitialMatch;
        if (!allowFire)
        {
            return;
        }
        // 「出現し、かつ条件を満たしたとき」— ライフサイクルと値の述語を
        // 同じ列挙に同居させるモデルでは表現できない組み合わせ (docs/DESIGN.md §3)
        if (matched && rt.Definition.On == TriggerOn.ElementAppeared)
        {
            Fire(rt, TriggerOn.ElementAppeared, ComparisonString.None, LastValueOf(rt), CurrentSnapshot(rt));
        }
        // WhileMatching は立ち上がりだけ。解決のたびに鳴らすと、
        // 成立したままスロットが 1 つ入れ替わっただけで鳴ってしまう
        else if (matched && !wasMatching && rt.Definition.On == TriggerOn.WhileMatching)
        {
            Fire(rt, TriggerOn.WhileMatching, ComparisonString.None, LastValueOf(rt), CurrentSnapshot(rt));
        }
        // 立ち下がり。解決で起きるのは「入れ替わった要素では条件が満たせない」形である。
        // 3 値は同じサイトの立ち上がりの鏡映し (docs/DESIGN.md C14)
        else if (!matched && wasMatching && rt.Definition.On == TriggerOn.WhileMatching &&
            rt.Definition.NotifyOnStoppedMatching)
        {
            Fire(rt, TriggerOn.StoppedMatching, ComparisonString.None, LastValueOf(rt), CurrentSnapshot(rt));
        }
    }

    /// <summary>
    /// StructureChanged の購読を張り替える。
    ///
    /// 未解決のうちはウィンドウ全体の Subtree (どこに出現するか分からないため)、
    /// 解決後は経路上の各段だけ (docs/DESIGN.md B3)。
    /// 未解決の間は sweep のたびにここへ来るため、同じウィンドウなら張り替えを省く。
    /// </summary>
    private void EnsureStructureSubscription(
        TriggerRuntime rt, ElementSlot slot, IReadOnlyList<IElementNode> targets, TreeScope scope, nint hwnd, bool pathScoped)
    {
        if (!pathScoped && !slot.StructureIsPathScoped &&
            slot.StructureHandler is not null && slot.StructureElements.Count == 1 && targets.Count == 1 &&
            slot.StructureHandlerHwnd == hwnd && IsSameElement(slot.StructureElements[0], targets[0]))
        {
            // 同じウィンドウに購読済み。新しく取ったノードは不要
            Ctx.Tree.Release(targets[0]);
            return;
        }

        if (targets.Count == 0)
        {
            // 張り替える先が無い。ここは 2 つの意味を持つので分ける (docs/DESIGN.md §8)
            if (pathScoped)
            {
                // 解決した結果、経路が空 = 対象がウィンドウ自身。
                // 広いウィンドウ購読はもう要らない (消滅は WindowClosed が拾う)。
                // ここで外さないと Subtree 購読が残り続け、B2 で潰した「無関係な変化まで
                // 受け取る」状態に戻る
                RemoveStructureSubscription(rt, slot);
            }
            // 未解決でウィンドウを特定できなかった場合は**古い購読を残す**。
            // 「見つからない」は「消えた」とは限らず、相手が塞がっているだけのこともある。
            // 本当に消えていれば購読は静かになるだけで害はなく、再出現は WindowOpened が拾う
            return;
        }

        // **新しい購読を先に立て、古いものを外すのは立ったと分かってからにする。**
        // 逆順にすると、相手が忙しくて張り直せなかったときに購読が 0 件で残り、
        // 「イベントが来ない → 掃引が走らない → 張り直されない」の閉路に入る (docs/DESIGN.md §8)
        var handler = new StructureChangedEventHandler((_, _) =>
        {
            Interlocked.Increment(ref _structureEventCount);
            _dispatcher.Post(ScheduleSweep);
        });
        var added = new List<IElementNode>(targets.Count);
        foreach (IElementNode target in targets)
        {
            try
            {
                Ctx.Automation.AddStructureChangedEventHandler(UiaElementNode.Unwrap(target), scope, null, handler);
                added.Add(target);
            }
            catch (COMException ex)
            {
                // 購読できなかった段は保持しない (解放責任は購読リストにあるため、ここで手放す)
                Log.SubscriptionFailed(_logger, rt.Id, ex);
                Ctx.Tree.Release(target);
            }
        }

        if (added.Count == 0)
        {
            // 1 つも張れなかった。古い購読があるならそれを残す — 相手が忙しいだけかもしれず、
            // 壊すと戻る道が無くなる。
            // 古い購読も無いなら、**張ろうとした先だけは記録する**。
            // これが SubscriptionHealth.IsOrphaned の目印になり、復旧の再試行が回りはじめる
            if (slot.StructureElements.Count == 0)
            {
                slot.StructureHandlerHwnd = hwnd;
                slot.StructureIsPathScoped = pathScoped;
                UpdateDiagnostics();
            }
            return;
        }

        RemoveStructureSubscription(rt, slot);
        slot.StructureElements.AddRange(added);
        slot.StructureHandler = handler;
        slot.StructureHandlerHwnd = hwnd;
        slot.StructureIsPathScoped = pathScoped;
        UpdateDiagnostics();
    }

    private void RemoveStructureSubscription(TriggerRuntime rt, ElementSlot slot)
    {
        foreach (IElementNode node in slot.StructureElements)
        {
            if (slot.StructureHandler is not null)
            {
                try
                {
                    Ctx.Automation.RemoveStructureChangedEventHandler(UiaElementNode.Unwrap(node), slot.StructureHandler);
                }
                catch (COMException)
                {
                }
            }
            Ctx.Tree.Release(node);
        }
        slot.StructureElements.Clear();
        slot.StructureHandler = null;
        slot.StructureHandlerHwnd = 0;
        slot.StructureIsPathScoped = false;
        UpdateDiagnostics();
    }

    /// <summary>購読と解決の形を診断カウンタへ写す (UiaDispatcher スレッド上でのみ呼ぶ)。</summary>
    private void UpdateDiagnostics()
    {
        int subscriptions = 0;
        int resolved = 0;
        int orphaned = 0;
        int watchedProperties = 0;
        int slots = 0;
        int resolvedSlots = 0;
        bool allPathScoped = true;
        foreach (TriggerRuntime rt in _runtimes.Values)
        {
            if (rt.IsResolved)
            {
                resolved++;
            }
            // 孤児はスロットごとに見ること。トリガー単位に畳むと、片方のスロットが購読できていて
            // もう片方が孤児のトリガーが「健全」に見え、SubscriptionRepair が永久に armed に
            // ならない = docs/DESIGN.md §8 の閉路が半分だけ再発する
            bool anyOrphaned = false;
            foreach (ElementSlot slot in rt.Slots)
            {
                subscriptions += slot.StructureElements.Count;
                watchedProperties += slot.PropertyIds.Length;
                slots++;
                if (slot.IsResolved)
                {
                    resolvedSlots++;
                }
                if (slot.StructureElements.Count > 0 && !slot.StructureIsPathScoped)
                {
                    allPathScoped = false;
                }
                if (SubscriptionHealth.IsOrphaned(slot.StructureElements.Count, slot.StructureHandlerHwnd))
                {
                    anyOrphaned = true;
                }
            }
            if (anyOrphaned)
            {
                orphaned++;
            }
        }
        Volatile.Write(ref _structureSubscriptionCount, subscriptions);
        Volatile.Write(ref _structurePathScoped, allPathScoped ? 1 : 0);
        Volatile.Write(ref _triggerCount, _runtimes.Count);
        Volatile.Write(ref _resolvedCount, resolved);
        Volatile.Write(ref _orphanedCount, orphaned);
        Volatile.Write(ref _watchedPropertyCount, watchedProperties);
        Volatile.Write(ref _slotCount, slots);
        Volatile.Write(ref _resolvedSlotCount, resolvedSlots);

        // 壊れている間だけ再試行を予約する。戻ったら間隔も最初へ戻す。
        // **ここが「ポーリングしない」を保つ唯一の場所である** —
        // 判定 (SubscriptionHealth) が緩いと、アプリ未起動のトリガーで回りっぱなしになる
        if (orphaned > 0 && _started)
        {
            _repair.Schedule();
        }
        else if (orphaned == 0)
        {
            _repair.Recovered();
        }
    }

    /// <summary>
    /// 掴んでいる要素が、まだウィンドウから経路をたどって**下向きに**届くかを確かめる。
    ///
    /// <para>
    /// <see cref="CheckAlive"/> の属性突合だけでは足りないことは実測で確認済みである
    /// (docs/DESIGN.md §8)。WinForms (MSAA ブリッジ) では要素を消すと HWND ごと消えるため
    /// 属性の読み直しが <c>ElementNotAvailable</c> になるが、**ネイティブ UIA プロバイダー
    /// (WPF) では切り離された要素がそのまま応答し続ける** — 属性も、
    /// <c>GetParent</c> で遡った祖先鎖 (peer が親をキャッシュしている) さえも変わらない。
    /// 結果、対象を削除しても未解決にならず、同名の要素が出現しても**ゾンビを監視し続ける**。
    /// トリガーが黙って動かなくなる、最も避けたい壊れ方である。
    /// </para>
    /// <para>
    /// 上向きの navigation が当てにならない以上、確かめられるのは下向きの到達性だけである。
    /// 経路購読 (docs/DESIGN.md B3) で既に保持している各段を使い、
    /// 「次の段が本当にその段の子か」を段の数だけ確かめる。
    /// 祖先ごとまとめて消えた場合も、切れた段でここが false になる。
    /// </para>
    /// <para>
    /// 費用は段数ぶんの子列挙 (段あたり 1 往復 — B1)。sweep は元から全ウィンドウ列挙を伴い、
    /// 再解決はビーム幅 × 段数の往復を要するので、その手前で 1 × 段数に収めていることになる。
    /// </para>
    /// </summary>
    private bool IsStillOnThePath(ElementSlot slot)
    {
        // 経路購読でないとき = 対象がウィンドウ要素そのもの。その消滅は WindowClosed で拾う
        if (!slot.StructureIsPathScoped || slot.StructureElements.Count == 0)
        {
            return true;
        }

        TreeViewMode view = slot.Locator.View;
        int max = _options.Resolver.MaxChildrenPerLevel;
        for (int i = 0; i < slot.StructureElements.Count; i++)
        {
            IElementNode parent = slot.StructureElements[i];
            IElementNode child = i + 1 < slot.StructureElements.Count ? slot.StructureElements[i + 1] : slot.Element!;
            if (!IsChildOf(parent, child, view, max))
            {
                return false;
            }
        }
        return true;
    }

    /// <summary>
    /// <paramref name="child"/> が <paramref name="parent"/> の子として列挙されるか。
    /// 列挙は 1 往復で、識別属性はキャッシュ済みなので、
    /// <c>CompareElements</c> は候補を属性で絞ってから呼ぶ (docs/DESIGN.md B9 と同じ手)。
    /// </summary>
    private bool IsChildOf(IElementNode parent, IElementNode child, TreeViewMode view, int max)
    {
        IReadOnlyList<IElementNode> children;
        try
        {
            children = Ctx.Tree.GetChildren(parent, view, max);
        }
        catch (COMException ex) when (ComErrors.IsElementGone(ex))
        {
            return false;
        }

        ElementIdentity wanted = ElementIdentity.Of(child);
        bool found = false;
        foreach (IElementNode candidate in children)
        {
            if (!found && ElementIdentity.Of(candidate) == wanted && IsSameElement(candidate, child))
            {
                found = true;
            }
            Ctx.Tree.Release(candidate);
        }
        return found;
    }

    /// <summary>
    /// 2 つの要素が同一かを判定する。
    /// HWND の値は再利用されるため、ウィンドウが閉じて同じ HWND が別のウィンドウに割り当てられると
    /// 「購読済み」と誤判定して死んだ要素への購読を持ち続けることになる (docs/DESIGN.md A9)。
    /// UIA の CompareElements は RuntimeId 比較なので、作り直されたウィンドウは別物と判定される。
    /// </summary>
    private bool IsSameElement(IElementNode? a, IElementNode? b)
    {
        if (a is null || b is null)
        {
            return false;
        }
        try
        {
            return Ctx.Tree.AreSame(a, b);
        }
        catch (COMException)
        {
            // 比較できない = 少なくとも一方が既に死んでいる。張り替える
            return false;
        }
    }

    private void HandleRemoved(TriggerRuntime rt, ElementSlot slot, string reason)
    {
        ElementPropertySnapshot? lastSnapshot = slot.LastSnapshot;

        RemovePropertySubscription(slot);
        Ctx.Tree.Release(slot.Element);
        slot.Element = null;
        slot.WindowHandle = 0;

        RaiseResolutionChanged(rt, false, reason);

        // 消えたスロットの句は「最後に見えていた状態」に対して評価する。
        // 要素はもう無いので読み直しようがなく、これが唯一意味のある解釈である。
        // LastSnapshot は残したまま評価すること。
        //
        // **評価を飛ばす側では記録を戻すこと。**この下の ElementRemoved は
        // lastSnapshot が null でも鳴るので、戻さないと**前の周の句の値が載る**
        // (docs/DESIGN.md §4)
        bool matched = false;
        if (lastSnapshot is null)
        {
            ResetReadings(rt);
        }
        else
        {
            matched = Evaluate(rt);
        }
        bool wasMatching = rt.LastMatch;
        // 水準は代入ではなく評価から求める。false を入れてしまうと、!b で
        // 「b が消えたら成立」が表せなくなる (立ち上がりが起きた瞬間に潰される)
        rt.LastMatch = matched;

        // ElementRemoved が言っているのは「**対象**の要素が消えた」であって
        // 「どれかのスロットが消えた」ではない。対象 = 先頭のスロットである。
        //
        // On が ElementRemoved のとき、句は自前の要素を名指せない (CreateRuntime が弾く) ので、
        // **スロット 0 = 先頭の句のスロット = 消えた要素**である。だから下の
        // CurrentSnapshot(rt) は「消えたときの最後のスナップショット」に一致する
        // (HandleRemoved は LastSnapshot を残す)。
        if (rt.Definition.On == TriggerOn.ElementRemoved && ReferenceEquals(slot, rt.Slots[0]) &&
            (lastSnapshot is null || matched))
        {
            Fire(rt, TriggerOn.ElementRemoved, LastValueOf(rt), ComparisonString.None, CurrentSnapshot(rt));
        }
        // 「消えたら成立する」条件 (!b) の立ち上がりはここでしか起きない。
        // 拾わないと ! が presence に対して使えないままになる。
        //
        // WhileMatching では句が自前の要素を持てるので、消えたのが先頭の句のスロットとは限らない。
        // それでも載せるのは**先頭の句の要素**のものである — イベントの 3 つの値
        // (OldValue / NewValue / Properties) が同じ要素を指すという約束を、経路ごとに崩さない
        else if (rt.Definition.On == TriggerOn.WhileMatching && matched && !wasMatching)
        {
            Fire(rt, TriggerOn.WhileMatching, ComparisonString.None, LastValueOf(rt), CurrentSnapshot(rt));
        }
        // 立ち下がり — 成立中に要素が消えて条件が満たせなくなった形。
        // ElementRemoved の分岐と両方鳴ることは構造的に無い: あちらは On==ElementRemoved、
        // こちらは On==WhileMatching のときだけ入り、フラグは WhileMatching 専用
        // (CreateRuntime が弾く)。3 値は同じサイトの立ち上がりの鏡映し (docs/DESIGN.md C14)
        else if (rt.Definition.On == TriggerOn.WhileMatching && rt.Definition.NotifyOnStoppedMatching &&
            !matched && wasMatching)
        {
            Fire(rt, TriggerOn.StoppedMatching, ComparisonString.None, LastValueOf(rt), CurrentSnapshot(rt));
        }
    }

    /// <param name="rt">評価するトリガー。</param>
    /// <param name="slot">値を読み直すスロット。</param>
    /// <param name="polled">
    /// 利用者が頼んだポーリングから来たか。**UIA からの通知と違い、この経路には
    /// 「何かが変わった」という手掛かりが一切無い**ので、
    /// <see cref="TriggerProperty.Custom"/> の扱いだけが変わる
    /// (<see cref="ElementSlot.CustomValues"/> を参照)。それ以外は完全に同じ道を通る。
    /// </param>
    private void OnPropertyChanged(TriggerRuntime rt, ElementSlot slot, bool polled = false)
    {
        if (slot.Element is null || !_started)
        {
            return;
        }

        IUIAutomationElement element = UiaElementNode.Unwrap(slot.Element);
        ElementPropertySnapshot snapshot;
        try
        {
            // 句が複数のプロパティに跨っても、読み直しは 1 往復のまま (docs/DESIGN.md B1)
            snapshot = PropertyReader.ReadSnapshot(Ctx, element);
        }
        catch (COMException ex) when (ComErrors.IsElementGone(ex))
        {
            HandleRemoved(rt, slot, Strings.Status_ElementUnavailable);
            ScheduleSweep();
            return;
        }
        catch (COMException ex)
        {
            Log.LookupFailed(_logger, nameof(OnPropertyChanged), ex);
            return;
        }

        // イベントに載せる値とスナップショットは**3 つとも先頭の句の要素**のものである
        // (docs/DESIGN.md §4)。変化を報告したスロットのものを混ぜてはいけない —
        // 混ぜると、複合条件で「Properties は B の要素、NewValue は A の値」という
        // **1 つのイベントの中で 2 つの要素を指す**形になる。
        // oldValue が空になりうるのはそのためである (報告したのが先頭の句のスロットでなければ、
        // 先頭の句の「変化前の値」というものが存在しない)
        ElementPropertySnapshot? previous = slot.LastSnapshot;
        ComparisonString oldValue = LastValueOf(rt, slot, previous);
        slot.LastSnapshot = snapshot;
        ComparisonString newValue = LastValueOf(rt);

        // Custom はスナップショットに入らないので、Evaluate が読んだ値でキャッシュが上書きされる。
        // 比較には「この周が始まる前」の写しが要る (ポーリング経路だけ)
        Dictionary<int, ClauseValue>? customBefore =
            polled && slot.CustomValues is { Count: > 0 } ? new Dictionary<int, ClauseValue>(slot.CustomValues) : null;

        // 未解決スロットが残っていても評価する。その句は不成立になるだけで、
        // a && !b の !b はまさにそれを条件にしている (TryResolve と同じ理由)
        bool matched = Evaluate(rt);
        bool wasMatching = rt.LastMatch;
        rt.LastMatch = matched;

        switch (rt.Definition.On)
        {
            case TriggerOn.PropertyChanged:
                // 値が実際に変わっていなければ何もしない。UIA は同じ値でも
                // PropertyChanged を送ってくることがある。
                // 比べるのは**イベントを上げたスロット**の句だけ — スロットをまたいで
                // 和を取ると、他のスロットが動いただけで二重に鳴る
                if (matched && WatchedValuesChanged(rt, slot, previous, snapshot, polled, customBefore))
                {
                    Fire(rt, TriggerOn.PropertyChanged, oldValue, newValue, CurrentSnapshot(rt));
                }
                break;
            case TriggerOn.WhileMatching:
                // 立ち上がりのみ。成立し続けている間は 1 回しか鳴らさない
                if (matched && !wasMatching)
                {
                    Fire(rt, TriggerOn.WhileMatching, oldValue, newValue, CurrentSnapshot(rt));
                }
                // 立ち下がりも同じくエッジ 1 回。ポーリング経路 (PollRound) はこの
                // メソッドに合流するので、通知の無い立ち下がりもここで拾える
                else if (!matched && wasMatching && rt.Definition.NotifyOnStoppedMatching)
                {
                    Fire(rt, TriggerOn.StoppedMatching, oldValue, newValue, CurrentSnapshot(rt));
                }
                break;
            // ElementRemoved の購読は LastSnapshot を最新に保つためだけにある
            // (WatchesProperties を参照)。ここでは何も鳴らさない — 鳴るのは HandleRemoved
        }
    }

    /// <summary>
    /// 句が見ているプロパティのいずれかが実際に変わったか。
    /// **全プロパティ**ではなく句のプロパティだけを見ること — スナップショットには
    /// BoundingRectangle のように常時動く値が含まれており、全体比較にすると
    /// 「UIA の重複通知を落とす」という目的を果たせない。
    /// </summary>
    private static bool WatchedValuesChanged(
        TriggerRuntime rt, ElementSlot slot, ElementPropertySnapshot? previous, ElementPropertySnapshot current,
        bool polled, Dictionary<int, ClauseValue>? customBefore)
    {
        if (previous is null)
        {
            return true;
        }
        for (int i = 0; i < rt.Clauses.Count; i++)
        {
            CompiledClause compiled = rt.Clauses[i];
            // このスロットの句だけを、このスロットの前回スナップショットと比べる
            if (rt.SlotOf(i) != slot)
            {
                continue;
            }
            // 監視しない句は購読もされていないので、その値の変化を発火の理由にしない。
            // ここを外すと Watch = false が「購読を減らすだけ」に退化し、
            // ほかの句の変化で鳴るたびに、絞るだけのはずの句が発火源として数えられる
            if (!compiled.Clause.Watch)
            {
                continue;
            }
            TriggerProperty property = compiled.Clause.Property;
            if (property == TriggerProperty.Custom)
            {
                if (CustomValueChanged(compiled.Clause, slot, polled, customBefore))
                {
                    return true;
                }
                continue;
            }
            if (previous.GetComparisonValue(property) != current.GetComparisonValue(property))
            {
                return true;
            }
        }
        return false;
    }

    /// <summary>
    /// <see cref="TriggerProperty.Custom"/> の句を発火の理由にしてよいか。
    ///
    /// <para>
    /// Custom はスナップショットに入らないので、**イベント経路では前回値を持っていない**。
    /// そこでは通す — UIA がこのスロットの購読集合の何かについて
    /// 「変わった」と言ってきているのだから、落とすより通すほうが安全である。
    /// **「その Custom ID が変わった」と言ってきているわけではない**ことに注意
    /// (ハンドラは購読集合全体に 1 本張られ、event args を捨てている)。
    /// つまりこれは正しさではなく、既定の挙動である。
    /// </para>
    /// <para>
    /// **ポーリング経路には、その手掛かりすら無い。**タイマーで起きただけなので、
    /// ここで通すと毎周期鳴る。<see cref="ElementSlot.CustomValues"/> に残した直前値と
    /// 突き合わせて、本当に変わった周だけを通す。
    /// </para>
    /// </summary>
    private static bool CustomValueChanged(
        PropertyClause clause, ElementSlot slot, bool polled, Dictionary<int, ClauseValue>? customBefore)
    {
        // **この 1 行には検査が無い。**外すとイベント経路にもキャッシュが効くが、
        // それを捕まえるテストは書けなかった: 判別するには「監視付きの句が Custom だけ」かつ
        // 「その値が変わっていないのに通知が届く」状況が要るのに、購読はその Custom ID から
        // 作られるので、通知はその値が変わったときにしか来ない。残るのは UIA が同値で
        // 重複通知を送る場合だけで、それは非決定的である (まさにこの関数が濾すために在るもの)。
        // 意図的な退行で実測し、緑のままであることを確かめてある (docs/DESIGN.md §5)。
        // 検出力を示せないテストは書かない、という docs/TESTING.md §2 の判断である
        if (!polled)
        {
            return true;
        }
        int id = clause.CustomPropertyId;
        // 片方でも欠けていれば判断できない。通す側に倒す。
        // 欠けるのは (a) 初回の周 (b) 式が短絡してこの句を読まなかった周 である
        return customBefore is null ||
               !customBefore.TryGetValue(id, out ClauseValue before) ||
               slot.CustomValues is null ||
               !slot.CustomValues.TryGetValue(id, out ClauseValue after) ||
               before != after;
    }

    /// <summary>
    /// 句を評価する。<see cref="TriggerProperty.Custom"/> だけはその場で読む。
    ///
    /// **各スロットの最新スナップショットに対して評価するのであって、同時に読むわけではない。**
    /// 別プロセスを原子的にスナップショットする方法は無く、イベントごとに全スロットを読み直せば
    /// クロスプロセス呼び出しが要素数倍になる。ここから鋭い角が 1 つ出る —
    /// 未解決スロットの句は読めない = 不成立なので、<c>!b</c> は
    /// **b のアプリが起動していない間ずっと成立する**。回避は <c>a &amp;&amp; !b</c>。
    /// </summary>
    private static bool Evaluate(TriggerRuntime rt)
    {
        ResetReadings(rt);

        // 句の添字が要る。ClauseEvaluator には句そのものが渡るので、位置で引き直す
        ClauseValue Read(PropertyClause clause)
        {
            ElementSlot? slot = null;
            int index = -1;
            for (int i = 0; i < rt.Clauses.Count; i++)
            {
                if (ReferenceEquals(rt.Clauses[i].Clause, clause))
                {
                    slot = rt.SlotOf(i);
                    index = i;
                    break;
                }
            }
            // 読んだ句をここで記録する。**ここが「評価された句」の唯一の定義である** —
            // 短絡は木の側 (ClauseExpression.Evaluate) にあり、読まれなかった句は
            // この閉包に来ない。ClauseEvaluator を触らずに短絡を観測できるのはそのためである
            ClauseValue Record(ClauseValue value)
            {
                if (index >= 0)
                {
                    rt.LastValues[index] = value;
                    rt.LastEvaluated[index] = true;
                }
                return value;
            }

            if (slot?.LastSnapshot is not { } snapshot)
            {
                return Record(ClauseValue.Unsupported);
            }
            if (clause.Property != TriggerProperty.Custom)
            {
                return Record(ClauseValue.From(snapshot, clause.Property));
            }
            if (slot.Element is null)
            {
                return Record(ClauseValue.Unsupported);
            }
            ClauseValue custom = PropertyReader.ReadCustom(
                UiaElementNode.Unwrap(slot.Element), clause.CustomPropertyId);
            // 読んだ値は残しておく。ポーリング経路が「本当に変わったか」を言える唯一の手掛かりで、
            // それが無いと Custom 句を持つトリガーは毎周期鳴る (CustomValueChanged を参照)。
            // **イベント経路もここを通るが、あちらはこの値を読まない** — 書いておくのは、
            // 最初の周が比べる相手を持てるようにするためである
            (slot.CustomValues ??= [])[clause.CustomPropertyId] = custom;
            return Record(custom);
        }

        return rt.Expression is { } expression
            ? ClauseEvaluator.Matches(expression, rt.Clauses, Read)
            : ClauseEvaluator.Matches(rt.Definition.Combine, rt.Clauses, Read);
    }

    /// <summary>
    /// 句ごとの記録を「まだ何も読んでいない」に戻す。
    /// </summary>
    /// <remarks>
    /// **評価を飛ばす経路が呼ぶこと。**要素が消えてスナップショットも無い発火
    /// (<see cref="HandleRemoved"/>) は <see cref="Evaluate"/> を通らないので、
    /// 戻さないと**前の周の値が載る**。イベントは「そのとき何が読めたか」を語るものなので、
    /// 読めなかったのなら空でなければならない。
    /// </remarks>
    private static void ResetReadings(TriggerRuntime rt)
    {
        Array.Clear(rt.LastValues);
        Array.Clear(rt.LastEvaluated);
    }

    /// <summary>
    /// 直前の評価から、イベントに載せる句ごとの読み取りを作る。
    /// </summary>
    /// <remarks>
    /// **ここで固めるのが要点である。**作業領域は次の評価で上書きされ、配送は別スレッドで
    /// 起きるので、渡す時点で写しておかないと「配られたときには別の値」になる。
    /// 成立したかどうかはここで計算する — 短絡を観測するために評価器を触らない代わりに、
    /// 記録した値へ同じ述語をもう一度当てる (どちらも純粋なので結果は変わらない)。
    /// </remarks>
    private static ClauseReading[] ReadingsOf(TriggerRuntime rt)
    {
        if (rt.Clauses.Count == 0)
        {
            return [];
        }
        var readings = new ClauseReading[rt.Clauses.Count];
        for (int i = 0; i < rt.Clauses.Count; i++)
        {
            if (!rt.LastEvaluated[i])
            {
                readings[i] = new ClauseReading(rt.ClauseNames[i], ComparisonString.None, ClauseOutcome.NotEvaluated);
                continue;
            }
            ClauseValue value = rt.LastValues[i];
            ClauseOutcome outcome = !value.IsSupported
                ? ClauseOutcome.Unreadable
                : ClauseEvaluator.MatchesClause(rt.Clauses[i], value)
                    ? ClauseOutcome.Matched
                    : ClauseOutcome.NotMatched;
            readings[i] = new ClauseReading(
                rt.ClauseNames[i], value.IsSupported ? value.Text : ComparisonString.None, outcome);
        }
        return readings;
    }

    /// <summary>
    /// イベントに載せる代表値。最初の句のプロパティを、**その句が属するスロットの**
    /// スナップショットから読む (句が無ければ値なし)。
    /// スロットを取り違えると、句 0 が別の要素に居た瞬間に黙って別の値が載る。
    /// 比較用文字列であり、表示用に整形したものではない (docs/DESIGN.md L4)。
    /// </summary>
    private static ComparisonString LastValueOf(TriggerRuntime rt) =>
        rt.Clauses.Count == 0 ? ComparisonString.None : LastValueOf(rt, rt.SlotOf(0), rt.SlotOf(0).LastSnapshot);

    private static ComparisonString LastValueOf(TriggerRuntime rt, ElementSlot slot, ElementPropertySnapshot? snapshot)
    {
        if (snapshot is null || rt.Clauses.Count == 0 || rt.SlotOf(0) != slot)
        {
            return ComparisonString.None;
        }
        return snapshot.GetComparisonValue(rt.Clauses[0].Clause.Property);
    }

    /// <summary>イベントに載せるスナップショット。最初の句が属するスロットのもの。</summary>
    private static ElementPropertySnapshot? CurrentSnapshot(TriggerRuntime rt) =>
        rt.Clauses.Count == 0 ? rt.Slots[0].LastSnapshot : rt.SlotOf(0).LastSnapshot;

    private void Fire(
        TriggerRuntime rt, TriggerOn on, ComparisonString oldValue, ComparisonString newValue,
        ElementPropertySnapshot? snapshot)
    {
        // レート制限は「発火が決まったあと」に掛ける。条件評価の状態 (LastMatch) は
        // 落とした発火でも進めておかないと、制限が解けた瞬間に古い立ち上がりが鳴る。
        //
        // 立ち下がり (StoppedMatching) はゲートの対象外。窓で落とすとホストは
        // 「まだ成立中」と信じ続ける — 状態の嘘になる。逆向きの非対称 (立ち上がりが
        // 落とされた後に立ち下がりだけ届く) は冗長だが嘘ではない (docs/DESIGN.md C14)。
        // SuppressedFireCount にも数えない — あれは「MinInterval が落とした数」である
        if (on != TriggerOn.StoppedMatching && !rt.Gate.TryPass())
        {
            Interlocked.Increment(ref _suppressedFireCount);
            return;
        }

        var args = new TriggerFiredEventArgs
        {
            TriggerId = rt.Id,
            On = on,
            OldValue = oldValue,
            NewValue = newValue,
            Properties = snapshot,
            // 直前の評価を写して固める。作業領域は次の評価で上書きされ、
            // 配送はこの下で別スレッドへ渡るので、ここで写さないと間に合わない
            Clauses = ReadingsOf(rt),
            // 時刻は TimeProvider から。ライブラリが DateTimeOffset.Now を読むと
            // ホストが差し替えた時計と食い違い、UTC でないぶん比較にも使えない (docs/DESIGN.md C10)
            Timestamp = _timeProvider.GetUtcNow(),
        };
        Log.TriggerFired(_logger, rt.Id, on.ToString());
        // 発火ごとに独立の work item を投げると順序が保証されず、ハンドラの例外も
        // 受け止められない (docs/DESIGN.md A1/A2)。単一ワーカーに直列で流す。
        _events.Enqueue(() => TriggerFired?.Invoke(this, args));
    }

    private void RaiseResolutionChanged(TriggerRuntime rt, bool resolved, string message)
    {
        if (!resolved)
        {
            Log.TriggerUnresolved(_logger, rt.Id, message);
        }
        var args = new TriggerResolutionChangedEventArgs
        {
            TriggerId = rt.Id,
            IsResolved = resolved,
            Message = message,
            Timestamp = _timeProvider.GetUtcNow(),
        };
        _events.Enqueue(() => ResolutionChanged?.Invoke(this, args));
    }
}
