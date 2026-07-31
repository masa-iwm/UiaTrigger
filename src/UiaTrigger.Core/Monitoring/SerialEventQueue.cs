// コンシューマ向けイベントの発火経路 (docs/DESIGN.md A1/A2)。
//
// 発火ごとに独立した ThreadPool.QueueUserWorkItem を投げると、
//   A1: ハンドラが投げた例外が未処理例外になりプロセスが落ちる
//   A2: A→B の順に検出しても B→A の順でハンドラに届きうる
// という 2 つの実害がある。ここでは単一のワーカーで順に呼び出し、
// ハンドラの例外は捕捉して通知先へ回す。
//
// bounded Channel + IAsyncEnumerable の公開 API (背圧と欠落カウンタ) へ発展させる将来項が
// docs/DESIGN.md §3 にある。現時点では順序保証と例外隔離だけを担保する。
using System.Threading.Channels;

namespace UiaTrigger.Monitoring;

internal sealed class SerialEventQueue : IAsyncDisposable
{
    private readonly Channel<Action> _channel =
        Channel.CreateUnbounded<Action>(new UnboundedChannelOptions { SingleReader = true });
    private readonly Action<Exception>? _onError;
    private readonly Task _worker;

    public SerialEventQueue(Action<Exception>? onError = null)
    {
        _onError = onError;
        _worker = Task.Run(RunAsync);
    }

    /// <summary>
    /// 発火を待ち行列に入れる。呼び出し元 (UiaDispatcher スレッド) はブロックしない。
    /// 既に完了している場合は false を返して黙って捨てる。
    /// </summary>
    public bool Enqueue(Action raise)
    {
        ArgumentNullException.ThrowIfNull(raise);
        return _channel.Writer.TryWrite(raise);
    }

    /// <summary>キューを閉じ、投入済みの発火をすべて処理し終えるまで待つ。</summary>
    public async ValueTask DisposeAsync()
    {
        _channel.Writer.TryComplete();
        await _worker.ConfigureAwait(false);
    }

    private async Task RunAsync()
    {
        await foreach (Action raise in _channel.Reader.ReadAllAsync().ConfigureAwait(false))
        {
            try
            {
                raise();
            }
            catch (Exception ex)
            {
                // コンシューマのハンドラが投げた例外。ここで止めるとプロセスが落ちるため、
                // 通知先へ回して次のイベントに進む。
                Report(ex);
            }
        }
    }

    private void Report(Exception exception)
    {
        try
        {
            _onError?.Invoke(exception);
        }
        catch
        {
            // 通知先自身が投げた場合まで面倒は見ない (ここで投げるとワーカーが死ぬ)
        }
    }
}
