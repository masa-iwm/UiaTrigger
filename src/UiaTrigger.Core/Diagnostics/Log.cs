// 開発者向けの診断ログ (docs/DESIGN.md C9)。
//
// このライブラリは COMException を大量に握り潰す設計になっている。要素が消えるのは
// 異常ではなく日常であり、例外にすると呼び出し元が何も書けなくなるためである。
// その代償として「動かないが理由が分からない」状態が生まれる。理由の出口がここ。
//
// ルール (docs/LOCALIZATION.md §3 の分類 3):
//   ・メッセージは <b>英語固定</b>。ログは共有・検索されるものなのでローカライズしない
//   ・ユーザーに見せる文字列は Strings.resx を通す (こちらではない)
//
// LoggerMessage 生成器を使うのは、ログが無効なときの割当を 0 にするため。
// ILogger? を直接渡せないので、null 判定の薄いラッパーを外側に置いている。
using Microsoft.Extensions.Logging;

namespace UiaTrigger;

internal static partial class Log
{
    public static void DpiUnaware(ILogger? logger, string problem)
    {
        if (logger is not null)
        {
            DpiUnawareCore(logger, problem);
        }
    }

    public static void TimeoutsUnsupported(ILogger? logger)
    {
        if (logger is not null)
        {
            TimeoutsUnsupportedCore(logger);
        }
    }

    public static void LookupFailed(ILogger? logger, string operation, Exception exception)
    {
        if (logger is not null)
        {
            LookupFailedCore(logger, operation, exception);
        }
    }

    public static void MonitorStarted(ILogger? logger, int triggerCount, bool sharedSession)
    {
        if (logger is not null)
        {
            MonitorStartedCore(logger, triggerCount, sharedSession);
        }
    }

    public static void MonitorStopped(ILogger? logger)
    {
        if (logger is not null)
        {
            MonitorStoppedCore(logger);
        }
    }

    public static void TriggerAdded(ILogger? logger, string triggerId)
    {
        if (logger is not null)
        {
            TriggerAddedCore(logger, triggerId);
        }
    }

    public static void TriggerRemoved(ILogger? logger, string triggerId)
    {
        if (logger is not null)
        {
            TriggerRemovedCore(logger, triggerId);
        }
    }

    public static void TriggerResolved(ILogger? logger, string triggerId, int steps, bool bySearch)
    {
        if (logger is not null)
        {
            TriggerResolvedCore(logger, triggerId, steps, bySearch);
        }
    }

    public static void TriggerUnresolved(ILogger? logger, string triggerId, string reason)
    {
        if (logger is not null)
        {
            TriggerUnresolvedCore(logger, triggerId, reason);
        }
    }

    public static void TriggerFired(ILogger? logger, string triggerId, string on)
    {
        if (logger is not null)
        {
            TriggerFiredCore(logger, triggerId, on);
        }
    }

    public static void InaccessibleProcesses(ILogger? logger, string triggerId, int count)
    {
        if (logger is not null)
        {
            InaccessibleProcessesCore(logger, triggerId, count);
        }
    }

    public static void SubscriptionFailed(ILogger? logger, string triggerId, Exception exception)
    {
        if (logger is not null)
        {
            SubscriptionFailedCore(logger, triggerId, exception);
        }
    }

    public static void PollingStarted(ILogger? logger, string triggerId, double intervalMs)
    {
        if (logger is not null)
        {
            PollingStartedCore(logger, triggerId, intervalMs);
        }
    }

    [LoggerMessage(
        EventId = 1, Level = LogLevel.Warning,
        Message = "Screen coordinates are unreliable in this process: {Problem}")]
    private static partial void DpiUnawareCore(ILogger logger, string problem);

    [LoggerMessage(
        EventId = 2, Level = LogLevel.Warning,
        Message = "IUIAutomation2 is unavailable, so the configured call timeouts are ignored; " +
                  "one unresponsive application can stall this session indefinitely.")]
    private static partial void TimeoutsUnsupportedCore(ILogger logger);

    [LoggerMessage(
        EventId = 3, Level = LogLevel.Debug,
        Message = "UI Automation call {Operation} failed; treating it as 'no result'.")]
    private static partial void LookupFailedCore(ILogger logger, string operation, Exception exception);

    [LoggerMessage(
        EventId = 4, Level = LogLevel.Debug,
        Message = "Monitoring started with {TriggerCount} trigger(s) (shared session: {SharedSession}).")]
    private static partial void MonitorStartedCore(ILogger logger, int triggerCount, bool sharedSession);

    [LoggerMessage(EventId = 5, Level = LogLevel.Debug, Message = "Monitoring stopped.")]
    private static partial void MonitorStoppedCore(ILogger logger);

    [LoggerMessage(EventId = 6, Level = LogLevel.Debug, Message = "Trigger '{TriggerId}' added.")]
    private static partial void TriggerAddedCore(ILogger logger, string triggerId);

    [LoggerMessage(EventId = 7, Level = LogLevel.Debug, Message = "Trigger '{TriggerId}' removed.")]
    private static partial void TriggerRemovedCore(ILogger logger, string triggerId);

    [LoggerMessage(
        EventId = 8, Level = LogLevel.Debug,
        Message = "Trigger '{TriggerId}' resolved at depth {Steps} (by automation id search: {BySearch}).")]
    private static partial void TriggerResolvedCore(ILogger logger, string triggerId, int steps, bool bySearch);

    [LoggerMessage(
        EventId = 9, Level = LogLevel.Debug,
        Message = "Trigger '{TriggerId}' is not resolved: {Reason}")]
    private static partial void TriggerUnresolvedCore(ILogger logger, string triggerId, string reason);

    [LoggerMessage(EventId = 10, Level = LogLevel.Debug, Message = "Trigger '{TriggerId}' fired on {On}.")]
    private static partial void TriggerFiredCore(ILogger logger, string triggerId, string on);

    [LoggerMessage(
        EventId = 11, Level = LogLevel.Information,
        Message = "Trigger '{TriggerId}': {Count} running process(es) could not be inspected and were " +
                  "skipped. They are almost certainly elevated; a non-elevated client cannot see them.")]
    private static partial void InaccessibleProcessesCore(ILogger logger, string triggerId, int count);

    [LoggerMessage(
        EventId = 12, Level = LogLevel.Debug,
        Message = "Trigger '{TriggerId}': subscribing to a structure change failed for one level.")]
    private static partial void SubscriptionFailedCore(ILogger logger, string triggerId, Exception exception);

    [LoggerMessage(
        EventId = 13, Level = LogLevel.Debug,
        Message = "Trigger '{TriggerId}': polling its resolved elements every {IntervalMs}ms, as requested. " +
                  "This is a cost the caller asked for; monitoring is otherwise event driven.")]
    private static partial void PollingStartedCore(ILogger logger, string triggerId, double intervalMs);
}
