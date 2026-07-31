// Core の診断ログ (docs/DESIGN.md C9) を実物で見せるための最小の ILogger。
//
// ロギング実装のパッケージは取らない。Core が依存するのは
// Microsoft.Extensions.Logging.Abstractions だけであり、「ホストが持っている ILogger を
// そのまま渡せる」ことを示すにはこれで十分である (AOT 発行でも動くことを CI で通す)。
using Microsoft.Extensions.Logging;

namespace UiaTrigger.TestHost;

internal sealed class StderrLogger(LogLevel minimumLevel) : ILogger
{
    public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

    public bool IsEnabled(LogLevel logLevel) => logLevel >= minimumLevel && logLevel != LogLevel.None;

    public void Log<TState>(
        LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
    {
        ArgumentNullException.ThrowIfNull(formatter);
        if (!IsEnabled(logLevel))
        {
            return;
        }
        // ログは英語固定・開発者向けなので書式もカルチャに依存させない
        Console.Error.WriteLine(FormattableString.Invariant(
            $"[{logLevel}] {formatter(state, exception)}{(exception is null ? "" : " <- " + exception.Message)}"));
    }
}
