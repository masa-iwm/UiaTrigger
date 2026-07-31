using System.Collections.Concurrent;
using System.Runtime.CompilerServices;

namespace UiaTrigger.Threading;

/// <summary>
/// UI Automation へのアクセスを一元化する専用 MTA スレッド。
/// UIA の COM オブジェクトはすべてこのスレッド上でのみ操作する。
///
/// internal なのは意図である (docs/DESIGN.md C7)。
/// これ単体では何もできない実装詳細であり、公開する継ぎ目は
/// <see cref="UiaSession"/> の側にある。
/// </summary>
internal sealed class UiaDispatcher : IDisposable
{
    private readonly BlockingCollection<Action> _queue = new();
    private readonly Thread _thread;

    public UiaDispatcher(string name = "UiaTrigger.UiaDispatcher")
    {
        _thread = new Thread(Run) { IsBackground = true, Name = name };
        _thread.SetApartmentState(ApartmentState.MTA);
        _thread.Start();
    }

    public bool CheckAccess() => Thread.CurrentThread == _thread;

    /// <summary>ディスパッチャスレッドで実行し、完了を待てる Task を返す。</summary>
    public Task InvokeAsync(Action action) => InvokeAsync(action, CancellationToken.None);

    /// <inheritdoc cref="InvokeAsync{T}(Func{T}, CancellationToken)"/>
    public Task InvokeAsync(Action action, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(action);
        return InvokeAsync(() => { action(); return true; }, cancellationToken);
    }

    public Task<T> InvokeAsync<T>(Func<T> func) => InvokeAsync(func, CancellationToken.None);

    /// <summary>
    /// Queues <paramref name="func"/> for execution on the dispatcher thread.
    /// </summary>
    /// <remarks>
    /// Cancellation is only observed **before** the work starts running: once the dispatcher
    /// thread has picked the item up it always runs to completion. This keeps the returned task's
    /// result honest — a canceled task means the work never ran, never "it may or may not have
    /// taken effect". UIA calls cannot be aborted midway anyway.
    /// </remarks>
    public Task<T> InvokeAsync<T>(Func<T> func, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(func);
        if (cancellationToken.IsCancellationRequested)
        {
            return Task.FromCanceled<T>(cancellationToken);
        }

        var tcs = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
        // 実行するかキャンセルするかは「先に権利を取った側」が決める。
        // これで「Task は Canceled なのに func は実行済み」という状態が生まれない。
        var claim = new StrongBox<int>(0);
        CancellationTokenRegistration registration = default;
        if (cancellationToken.CanBeCanceled)
        {
            registration = cancellationToken.Register(
                () =>
                {
                    if (Interlocked.Exchange(ref claim.Value, 1) == 0)
                    {
                        tcs.TrySetCanceled(cancellationToken);
                    }
                });
        }

        try
        {
            AddToQueue(() =>
            {
                registration.Dispose();
                if (Interlocked.Exchange(ref claim.Value, 1) != 0)
                {
                    return; // キャンセル済み。func は実行しない
                }
                try
                {
                    tcs.SetResult(func());
                }
                catch (Exception ex)
                {
                    tcs.SetException(ex);
                }
            });
        }
        catch (InvalidOperationException)
        {
            registration.Dispose();
            if (Interlocked.Exchange(ref claim.Value, 1) == 0)
            {
                tcs.SetException(new ObjectDisposedException(nameof(UiaDispatcher)));
            }
        }
        return tcs.Task;
    }

    /// <summary>結果を待たずにディスパッチャスレッドへ投げる (イベントコールバックからの転送用)。</summary>
    public void Post(Action action)
    {
        ArgumentNullException.ThrowIfNull(action);
        try
        {
            AddToQueue(action);
        }
        catch (InvalidOperationException)
        {
            // Dispose 済みなら黙って捨てる
        }
    }

    // 投入自体はキャンセル対象にしない (キューは無制限なので待ちが発生しない)。
    // 呼び出し側の CancellationToken は「実行開始前に取り下げる」ためだけに使う。
    private void AddToQueue(Action action) => _queue.Add(action, CancellationToken.None);

    /// <summary>Post された処理から漏れた例外の通知先 (未設定なら握りつぶす)。</summary>
    public event Action<Exception>? UnhandledException;

    private void Run()
    {
        foreach (Action action in _queue.GetConsumingEnumerable())
        {
            try
            {
                action();
            }
            catch (Exception ex)
            {
                UnhandledException?.Invoke(ex);
            }
        }
    }

    public void Dispose()
    {
        if (!_queue.IsAddingCompleted)
        {
            _queue.CompleteAdding();
        }
    }
}
