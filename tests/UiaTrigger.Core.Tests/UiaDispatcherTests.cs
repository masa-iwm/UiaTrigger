using UiaTrigger.Threading;
using Xunit;

namespace UiaTrigger.Tests;

/// <summary>
/// ディスパッチャの回帰テスト (docs/DESIGN.md A16)。
///
/// StartAsync の CancellationToken は「実行開始前にのみ効く」意味論で honor する — 走り始めた UIA 呼び出しは
/// 途中で止められないため、途中キャンセルを許すと
/// 「Task は Canceled なのに監視は開始済み」という嘘の結果になる。
/// </summary>
public sealed class UiaDispatcherTests
{
    private static readonly TimeSpan Limit = TimeSpan.FromSeconds(10);

    private static CancellationToken TestCt => TestContext.Current.CancellationToken;

    [Fact]
    public async Task InvokeAsync_RunsOnADedicatedMtaThread()
    {
        using var dispatcher = new UiaDispatcher(nameof(InvokeAsync_RunsOnADedicatedMtaThread));

        var state = await dispatcher.InvokeAsync(() => Thread.CurrentThread.GetApartmentState())
            .WaitAsync(Limit, TestCt);
        bool onDispatcher = await dispatcher.InvokeAsync(dispatcher.CheckAccess).WaitAsync(Limit, TestCt);

        Assert.Equal(ApartmentState.MTA, state);
        Assert.True(onDispatcher);
        Assert.False(dispatcher.CheckAccess());
    }

    [Fact]
    public async Task InvokeAsync_PropagatesExceptions()
    {
        using var dispatcher = new UiaDispatcher(nameof(InvokeAsync_PropagatesExceptions));
        CancellationToken ct = TestCt;

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => dispatcher.InvokeAsync<int>(() => throw new InvalidOperationException("boom")).WaitAsync(Limit, ct));
    }

    /// <summary>A16: 既にキャンセル済みのトークンでは処理を実行しないこと。</summary>
    [Fact]
    public async Task InvokeAsync_WithCanceledToken_DoesNotRunTheWork()
    {
        using var dispatcher = new UiaDispatcher(nameof(InvokeAsync_WithCanceledToken_DoesNotRunTheWork));
        using var cts = new CancellationTokenSource();
        CancellationToken ct = TestCt;
        await cts.CancelAsync();

        int ran = 0;
        Task task = dispatcher.InvokeAsync(() => Interlocked.Increment(ref ran), cts.Token);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => task.WaitAsync(Limit, ct));
        Assert.Equal(0, Volatile.Read(ref ran));
    }

    /// <summary>A16: 実行待ちの間にキャンセルされたら、その処理は実行されないこと。</summary>
    [Fact]
    public async Task InvokeAsync_CanceledWhileQueued_DoesNotRunTheWork()
    {
        using var dispatcher = new UiaDispatcher(nameof(InvokeAsync_CanceledWhileQueued_DoesNotRunTheWork));
        using var blocking = new ManualResetEventSlim(false);
        using var cts = new CancellationTokenSource();
        CancellationToken ct = TestCt;

        // ディスパッチャスレッドを塞いで、後続を「キュー上で待っている」状態にする
        Task blocker = dispatcher.InvokeAsync(() => blocking.Wait(Limit, ct));

        int ran = 0;
        Task queued = dispatcher.InvokeAsync(() => Interlocked.Increment(ref ran), cts.Token);
        await cts.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => queued.WaitAsync(Limit, ct));

        blocking.Set();
        await blocker.WaitAsync(Limit, ct);
        // 塞いでいた処理の後ろに並んでいたので、キャンセル後に走り出す余地は無い
        await dispatcher.InvokeAsync(() => { }, ct).WaitAsync(Limit, ct);
        Assert.Equal(0, Volatile.Read(ref ran));
    }

    /// <summary>
    /// 実行が始まった後にキャンセルしても、結果は握り潰されず正常完了として返ること。
    /// 「Canceled なのに副作用は起きている」状態を作らないための取り決め。
    /// </summary>
    [Fact]
    public async Task InvokeAsync_CanceledAfterWorkStarted_StillCompletesSuccessfully()
    {
        using var dispatcher = new UiaDispatcher(nameof(InvokeAsync_CanceledAfterWorkStarted_StillCompletesSuccessfully));
        using var started = new ManualResetEventSlim(false);
        using var release = new ManualResetEventSlim(false);
        using var cts = new CancellationTokenSource();
        CancellationToken ct = TestCt;

        Task<int> task = dispatcher.InvokeAsync(
            () =>
            {
                started.Set();
                release.Wait(Limit, ct);
                return 42;
            },
            cts.Token);

        Assert.True(started.Wait(Limit, ct));
        await cts.CancelAsync();
        release.Set();

        Assert.Equal(42, await task.WaitAsync(Limit, ct));
    }

    [Fact]
    public async Task InvokeAsync_AfterDispose_ReportsObjectDisposed()
    {
        var dispatcher = new UiaDispatcher(nameof(InvokeAsync_AfterDispose_ReportsObjectDisposed));
        CancellationToken ct = TestCt;
        dispatcher.Dispose();

        await Assert.ThrowsAsync<ObjectDisposedException>(
            () => dispatcher.InvokeAsync(() => { }, ct).WaitAsync(Limit, ct));
    }
}
