using UiaTrigger.Monitoring;
using Xunit;

namespace UiaTrigger.Tests;

/// <summary>
/// 発火経路の回帰テスト (docs/DESIGN.md A1/A2)。
///
/// A1: コンシューマのハンドラが投げた例外を隔離しないと、未処理例外としてプロセスが落ちる。
/// A2: 発火ごとに独立の work item を投げる形だと、検出順と配送順が一致しない。
/// </summary>
public sealed class SerialEventQueueTests
{
    private static readonly TimeSpan Limit = TimeSpan.FromSeconds(10);

    private static CancellationToken TestCt => TestContext.Current.CancellationToken;

    /// <summary>A2: 投入順にそのまま配送されること。</summary>
    [Fact]
    public async Task Enqueue_DeliversInOrder()
    {
        const int Count = 500;
        var delivered = new List<int>(Count);
        var done = new TaskCompletionSource();

        await using (var queue = new SerialEventQueue())
        {
            for (int i = 0; i < Count; i++)
            {
                int value = i;
                Assert.True(queue.Enqueue(() =>
                {
                    delivered.Add(value);
                    if (delivered.Count == Count)
                    {
                        done.TrySetResult();
                    }
                }));
            }

            await done.Task.WaitAsync(Limit, TestCt);
        }

        Assert.Equal(Enumerable.Range(0, Count), delivered);
    }

    /// <summary>
    /// A2: 配送は 1 本のワーカーで直列に行われること。
    /// 並行して呼ばれるとハンドラ内で重なりが観測できてしまう。
    /// </summary>
    [Fact]
    public async Task Enqueue_NeverRunsHandlersConcurrently()
    {
        const int Count = 100;
        int inFlight = 0;
        int maxInFlight = 0;
        var done = new TaskCompletionSource();
        int completed = 0;

        await using (var queue = new SerialEventQueue())
        {
            for (int i = 0; i < Count; i++)
            {
                queue.Enqueue(() =>
                {
                    int current = Interlocked.Increment(ref inFlight);
                    maxInFlight = Math.Max(maxInFlight, current);
                    Thread.Sleep(1);
                    Interlocked.Decrement(ref inFlight);
                    if (++completed == Count)
                    {
                        done.TrySetResult();
                    }
                });
            }

            await done.Task.WaitAsync(Limit, TestCt);
        }

        Assert.Equal(1, maxInFlight);
    }

    /// <summary>
    /// A1: ハンドラの例外がワーカーを殺さず、通知先に回り、後続の配送も続くこと。
    /// 例外を隔離しないと、未処理例外としてプロセスが落ちる。
    /// </summary>
    [Fact]
    public async Task HandlerException_IsIsolated_AndReported()
    {
        var reported = new List<Exception>();
        var boom = new InvalidOperationException("handler blew up");
        var secondDelivered = new TaskCompletionSource();

        await using (var queue = new SerialEventQueue(reported.Add))
        {
            queue.Enqueue(() => throw boom);
            queue.Enqueue(() => secondDelivered.TrySetResult());

            await secondDelivered.Task.WaitAsync(Limit, TestCt);
        }

        Assert.Same(boom, Assert.Single(reported));
    }

    /// <summary>通知先自身が投げてもワーカーは生き続けること。</summary>
    [Fact]
    public async Task ErrorCallbackException_DoesNotKillTheWorker()
    {
        var delivered = new TaskCompletionSource();

        await using (var queue = new SerialEventQueue(_ => throw new InvalidOperationException("reporter blew up")))
        {
            queue.Enqueue(() => throw new InvalidOperationException("handler blew up"));
            queue.Enqueue(() => delivered.TrySetResult());

            await delivered.Task.WaitAsync(Limit, TestCt);
        }
    }

    /// <summary>Dispose は投入済みの発火を流し切ってから戻ること。</summary>
    [Fact]
    public async Task DisposeAsync_DrainsPendingEvents()
    {
        int delivered = 0;
        var queue = new SerialEventQueue();
        for (int i = 0; i < 200; i++)
        {
            queue.Enqueue(() => Interlocked.Increment(ref delivered));
        }

        await queue.DisposeAsync();

        Assert.Equal(200, delivered);
        // 完了後の投入は黙って捨てられる (例外にはしない)
        Assert.False(queue.Enqueue(() => Interlocked.Increment(ref delivered)));
        Assert.Equal(200, delivered);
    }
}
