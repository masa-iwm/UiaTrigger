// UiaSession の設定 (docs/DESIGN.md §3)。
//
// B5 の「呼び出し上限時間」の設定口はセッション側に置く。モニター側の設定にだけ置くと
// Picker / Inspector 経路は既定値しか使えない。セッション側に置くことで
// 「1 セッション = 1 MTA スレッド + 1 IUIAutomation」の設定が 1 箇所に集まる。
using Microsoft.Extensions.Logging;
using UiaTrigger.Resources;

namespace UiaTrigger;

/// <summary>Settings for a <see cref="UiaSession"/>.</summary>
public sealed class UiaSessionOptions
{
    /// <summary>Default value of <see cref="TransactionTimeout"/>.</summary>
    /// <remarks>
    /// UI Automation's own default is 20 seconds, which is far too long to hold the single
    /// automation thread for.
    /// </remarks>
    public static readonly TimeSpan DefaultTransactionTimeout = TimeSpan.FromSeconds(5);

    /// <summary>Default value of <see cref="ConnectionTimeout"/>.</summary>
    public static readonly TimeSpan DefaultConnectionTimeout = TimeSpan.FromSeconds(2);

    /// <summary>
    /// Maximum time UI Automation waits for a provider to respond to a single request.
    /// </summary>
    /// <remarks>
    /// Every call in a session runs on one dedicated thread, so without this bound a single
    /// unresponsive application blocks the whole session indefinitely. Requires Windows 8 or later
    /// (<c>IUIAutomation2</c>); on older systems the value is ignored and
    /// <see cref="UiaSession.GetSupportsTimeoutsAsync"/> reports false.
    /// </remarks>
    public TimeSpan TransactionTimeout { get; set; } = DefaultTransactionTimeout;

    /// <summary>Maximum time UI Automation waits while connecting to a provider.</summary>
    /// <inheritdoc cref="TransactionTimeout" path="/remarks"/>
    public TimeSpan ConnectionTimeout { get; set; } = DefaultConnectionTimeout;

    /// <summary>Clock used for timestamps, rate limits and debounce timers.</summary>
    /// <remarks>
    /// Injected rather than read from <see cref="DateTimeOffset.Now"/> so that time-dependent
    /// behaviour can be tested without waiting, and so a host can supply its own clock.
    /// </remarks>
    public TimeProvider TimeProvider { get; set; } = TimeProvider.System;

    /// <summary>
    /// Destination for diagnostic messages, or null to discard them.
    /// </summary>
    /// <remarks>
    /// Messages are written in English regardless of the current culture: they are meant to be
    /// searched and shared between developers, not shown to end users. This is the intended way to
    /// find out why a trigger never resolves — most UI Automation failures are reported as an
    /// <c>HRESULT</c> that the library has to treat as "the element is gone".
    /// </remarks>
    public ILogger? Logger { get; set; }

    /// <summary>Name given to the session's automation thread. For debuggers and profilers.</summary>
    public string ThreadName { get; set; } = "UiaTrigger.Session";

    /// <summary>Maximum number of children returned by one <see cref="UiaSession.GetChildrenAsync"/>.</summary>
    public int MaxChildren { get; set; } = 2048;

    /// <summary>Maximum number of ancestors walked when building a chain or an overlap stack.</summary>
    public int MaxDepth { get; set; } = 64;

    /// <summary>Maximum number of top-level windows considered by <see cref="UiaSession.GetOverlapStackAsync"/>.</summary>
    public int MaxOverlapWindows { get; set; } = 12;

    /// <summary>
    /// Whether point-based lookups ignore the calling process's own UI.
    /// </summary>
    /// <remarks>
    /// On by default because the caller is normally a picker: without this, hovering over the picker
    /// itself selects the picker. Turn it off to inspect your own windows.
    /// </remarks>
    public bool SkipOwnProcessElements { get; set; } = true;

    /// <summary>
    /// The bounds recording shares with resolution: how many siblings a step is looked for among,
    /// and how deep the walk up to the top-level window goes.
    /// </summary>
    /// <remarks>
    /// The same settings the monitor resolves with (<see cref="Monitoring.TriggerMonitorOptions.Resolver"/>).
    /// Recording past what resolution will consider produces a definition that records cleanly and
    /// then resolves to a different element without any error, so the two sides share one policy
    /// (docs/DESIGN.md A30). Scores are not used while recording; the limits are.
    /// </remarks>
    public Resolution.ResolverOptions Resolver { get; set; } = new();

    /// <summary>呼び出し上限時間として妥当か (UIA が受け付けるのは正のミリ秒)。</summary>
    internal static bool IsValidTimeout(TimeSpan value) => value.TotalMilliseconds is > 0 and <= uint.MaxValue;

    /// <summary>
    /// 設定の検証。**呼び出し元スレッドで前倒しする** — 設定ミスがディスパッチャスレッド上の
    /// 例外になると、呼び出し元には「なぜか動かない」としか見えないため。
    /// </summary>
    internal void Validate()
    {
        ArgumentNullException.ThrowIfNull(TimeProvider);
        ArgumentException.ThrowIfNullOrEmpty(ThreadName);
        ValidateTimeout(TransactionTimeout, nameof(TransactionTimeout));
        ValidateTimeout(ConnectionTimeout, nameof(ConnectionTimeout));
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(MaxChildren);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(MaxDepth);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(MaxOverlapWindows);
    }

    private static void ValidateTimeout(TimeSpan value, string name)
    {
        if (!IsValidTimeout(value))
        {
            throw new ArgumentOutOfRangeException(
                name, value, Message.Format(Strings.Error_TimeoutOutOfRange, name, uint.MaxValue));
        }
    }
}
