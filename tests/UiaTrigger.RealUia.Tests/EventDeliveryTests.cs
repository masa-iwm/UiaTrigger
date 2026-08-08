using System.Globalization;
using UiaTrigger.Models;
using UiaTrigger.Monitoring;
using Xunit;

namespace UiaTrigger.RealUia.Tests;

/// <summary>
/// 発火経路の回帰 (docs/DESIGN.md A1/A2) を実 UIA で確認する。
///
/// <c>SerialEventQueueTests</c> はキュー単体を直接叩いて順序と例外隔離を固定しているが、
/// 「UIA のコールバック → ディスパッチャ → キュー → ハンドラ」という実際の経路で
/// 同じ性質が保たれているかは、実物を通さないと分からない。
/// </summary>
public sealed class EventDeliveryTests
{
    private const int Changes = 6;

    /// <summary>変化を 1 件ずつ検出させるための間隔。詰めて送ると UIA 側でまとまってしまう。</summary>
    private static readonly TimeSpan BetweenChanges = TimeSpan.FromMilliseconds(250);

    /// <summary>
    /// 最終値を受けたあと、残っている発火を読み切るための猶予。
    /// 配送の遅延 (<c>FiredHook</c> の 200ms) より十分長くとる。
    /// </summary>
    private static readonly TimeSpan DrainGrace = TimeSpan.FromSeconds(2);

    /// <summary>
    /// 検出した順のまま届くこと (A2)。
    ///
    /// ハンドラをわざと遅くして配送待ちの列を作る。発火ごとに独立の work item を
    /// 投げる形だと、この状態で順序が入れ替わりうる。
    ///
    /// 完全一致ではなく「送った並びの部分列であること」を見る。UIA は同じ値の変化を
    /// まとめたり落としたりするため件数は保証されないが、**並びが入れ替わらないこと**が
    /// A2 の主張そのものである。最後の値だけは必ず届く (取りこぼしたまま終わらない)。
    /// </summary>
    [Fact]
    public async Task Fire_DeliversValuesInTheOrderTheyWereDetected()
    {
        using var target = TestTargetProcess.Start();
        target.Send("add-button btnTarget");

        TriggerDefinition definition = (await Recording.RecordAsync(target, "btnTarget"))
            .PinToWindow(target).WatchName();

        await using var harness = new MonitorHarness();
        // 配送を遅らせて列を作る。1 本のワーカーが順に呼ぶ設計なので、
        // ここが詰まっている間に検出された発火は後ろに並ぶ
        harness.FiredHook = _ => Thread.Sleep(200);
        await harness.StartAsync(definition);
        harness.WaitForResolution(resolved: true);

        var sent = new List<string>();
        for (int i = 0; i < Changes; i++)
        {
            string value = string.Create(CultureInfo.InvariantCulture, $"v{i:D2}");
            sent.Add(value);
            target.Send("set-text btnTarget " + value);
            await Task.Delay(BetweenChanges, TestContext.Current.CancellationToken);
        }

        var received = new List<string>();
        while (received.Count == 0 || !string.Equals(received[^1], sent[^1], StringComparison.Ordinal))
        {
            received.Add(harness.WaitForFire().NewValue.Value);
        }

        // **最終値が届いた時点で読むのをやめないこと。**やめると「最後の値が先に来た」
        // 形の順序逆転を、後続を一切見ないまま緑にする — 崩れているときだけ検出できない
        // という、いちばん悪い形の穴になる。配送は 1 本のワーカーなので、詰まりが
        // 明けたぶんの残りは短い猶予で出切る
        // **型で受けずに時間で切ること。**Next は種類を問わず次の 1 件を返すので、
        // 猶予の中に解決通知が 1 つ混じっただけでパターンが外れ、そこから先の発火を
        // 読まずに終わる — 塞ぎたかった穴と同じ形が、この読み方の中に再現する
        while (harness.Next(DrainGrace) is { } extra)
        {
            if (extra is TriggerFiredEventArgs fired)
            {
                received.Add(fired.NewValue.Value);
            }
        }

        Assert.True(
            IsSubsequence(received, sent),
            $"届いた並びが送った順序と食い違います。sent=[{string.Join(",", sent)}] " +
            $"received=[{string.Join(",", received)}]{Environment.NewLine}{harness.History()}");
        Assert.True(received.Count >= 2, $"変化が 1 件しか届いておらず順序を確かめられません: {harness.History()}");
    }

    /// <summary>
    /// ハンドラが投げてもプロセスが落ちず、以後の発火も届き続けること (A1)。
    /// 例外は <see cref="TriggerMonitor.UnhandledException"/> に回る。
    /// </summary>
    [Fact]
    public async Task Fire_WhenAHandlerThrows_KeepsDeliveringLaterEvents()
    {
        using var target = TestTargetProcess.Start();
        target.Send("add-button btnTarget");

        TriggerDefinition definition = (await Recording.RecordAsync(target, "btnTarget"))
            .PinToWindow(target).WatchName();

        await using var harness = new MonitorHarness();
        int seen = 0;
        harness.FiredHook = _ =>
        {
            if (Interlocked.Increment(ref seen) == 1)
            {
                throw new InvalidOperationException("consumer blew up");
            }
        };

        await harness.StartAsync(definition);
        harness.WaitForResolution(resolved: true);

        target.Send("set-text btnTarget first");
        Assert.Equal("first", harness.WaitForFire().NewValue.Value);

        target.Send("set-text btnTarget second");
        Assert.Equal("second", harness.WaitForFire().NewValue.Value);

        Assert.Contains(harness.Unhandled, ex => ex.Message.Contains("consumer blew up", StringComparison.Ordinal));
    }

    /// <summary>ネガティブコントロール: 何も変えなければ発火しないこと。</summary>
    [Fact]
    public async Task Fire_WithoutAnyChange_StaysQuiet()
    {
        using var target = TestTargetProcess.Start();
        target.Send("add-button btnTarget");

        TriggerDefinition definition = (await Recording.RecordAsync(target, "btnTarget"))
            .PinToWindow(target).WatchName();

        await using var harness = new MonitorHarness();
        await harness.StartAsync(definition);
        harness.WaitForResolution(resolved: true);

        harness.AssertNoFire(TimeSpan.FromSeconds(3));
    }

    private static bool IsSubsequence(List<string> received, List<string> sent)
    {
        int index = 0;
        foreach (string value in received)
        {
            index = sent.IndexOf(value, index);
            if (index < 0)
            {
                return false;
            }
            index++;
        }
        return true;
    }
}
