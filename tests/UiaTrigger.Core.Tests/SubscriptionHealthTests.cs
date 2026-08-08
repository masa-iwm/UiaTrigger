using UiaTrigger.Monitoring;
using Xunit;

namespace UiaTrigger.Tests;

/// <summary>
/// スロット状態の導出規則の回帰テスト (docs/DESIGN.md §8)。
///
/// <para>
/// この規則が**緩いほうへ**壊れると、まだ起動していないアプリのトリガーが常に
/// 「壊れている」と判定され、復旧の再試行が回りっぱなしになる。
/// **そうなっても機能は動いたままなので、他のテストは 1 件も落ちない** —
/// 「イベント駆動でポーリングしない」という設計の売りだけが静かに消える。
/// </para>
/// <para>
/// 逆に**厳しいほうへ**壊れると、購読を失ったスロットが二度と拾われない
/// (docs/DESIGN.md §8 の閉路。未解決側は CI で実際に観測され、解決済み側は
/// レビューで「復旧経路ゼロの恒久沈黙」として見つかった)。掃引の対象選定・修復の
/// 武装・診断の孤児数はすべてこの導出から決まるので、**全状態を表で固定する**。
/// </para>
/// </summary>
public sealed class SubscriptionHealthTests
{
    private const nint SomeWindow = 0x1234;

    /// <summary>
    /// 全状態の導出表。列は
    /// (resolved, pathDepth, structureCount, attemptedHwnd, pathScoped, wantsProps, hasHandler)。
    /// 期待値が <see cref="int"/> なのは、内部列挙 (SlotSubscriptionState) を public な
    /// テストシグネチャに出せないため (C7 — 公開型を増やさない)。中でキャストして比べる。
    /// </summary>
    public static TheoryData<string, bool, int, int, nint, bool, bool, bool, int> Cases =>
    [
        // ---- 未解決側 (従来の 4 分類の 3 つ) ----
        ("未解決・アプリがまだ起動していない",
            false, 0, 0, 0, false, false, false, (int)SlotSubscriptionState.Unresolved),
        ("未解決・ウィンドウ全体を購読して出現待ち",
            false, 0, 1, SomeWindow, false, false, false, (int)SlotSubscriptionState.WaitingSubscribed),
        ("未解決・ウィンドウはあるのに購読できなかった (§8 の閉路)",
            false, 0, 0, SomeWindow, false, false, false, (int)SlotSubscriptionState.WaitingOrphaned),
        // HandleRemoved 直後: 要素は手放したが経路購読は残っている (次の解決が張り替える)
        ("消滅直後・経路購読が残っている (未解決扱いで再解決対象)",
            false, 0, 2, SomeWindow, true, true, false, (int)SlotSubscriptionState.WaitingSubscribed),

        // ---- 解決済み側 ----
        ("解決済み・経路購読健全 (2 段)",
            true, 2, 2, SomeWindow, true, false, false, (int)SlotSubscriptionState.Resolved),
        ("解決済み・プロパティ購読も健全",
            true, 2, 2, SomeWindow, true, true, true, (int)SlotSubscriptionState.Resolved),
        ("解決済み・対象がウィンドウ自身 (経路 0 段。購読 0 が正常)",
            true, 0, 0, 0, false, false, false, (int)SlotSubscriptionState.ResolvedWindowSelf),
        ("解決済み・未解決期の Subtree 購読のまま (購読はあり機能する — 修復対象ではない)",
            true, 2, 1, SomeWindow, false, false, false, (int)SlotSubscriptionState.ResolvedSubtreeFallback),
        ("解決済み・構造購読を失った (復旧経路が要る)",
            true, 2, 0, SomeWindow, true, false, false, (int)SlotSubscriptionState.ResolvedOrphaned),
        ("解決済み・プロパティ購読を張るべきなのに張れていない (解決済みだが聞こえない)",
            true, 2, 2, SomeWindow, true, true, false, (int)SlotSubscriptionState.ResolvedOrphaned),
        // プロパティ購読の欠落は、対象がウィンドウ自身でも「聞こえない」ことに変わりない
        ("解決済み・ウィンドウ自身だがプロパティ購読が張れていない",
            true, 0, 0, 0, false, true, false, (int)SlotSubscriptionState.ResolvedOrphaned),
    ];

    [Theory]
    [MemberData(nameof(Cases))]
    public void StateOf_DerivesTheStateFromTheSlotFields(
        string because, bool resolved, int pathDepth, int structureCount, nint attemptedHwnd,
        bool pathScoped, bool wantsProperties, bool hasPropertyHandler, int expected)
    {
        SlotSubscriptionState state = SubscriptionHealth.StateOf(
            resolved, pathDepth, structureCount, attemptedHwnd, pathScoped, wantsProperties, hasPropertyHandler);

        Assert.Equal((SlotSubscriptionState)expected, state);
        Assert.NotEmpty(because);
    }

    /// <summary>
    /// 修復対象は Orphaned の 2 状態だけであること。
    /// <see cref="SlotSubscriptionState.ResolvedSubtreeFallback"/> を含めると、
    /// 経路購読に失敗し続ける相手に対して復旧タイマーと掃引の再解決が恒久的に回り続ける —
    /// ビーム探索 1 回とプロパティ購読の解除・再張りが毎回走り、張り替えの窓で変化まで落とす。
    /// あの状態は**購読があってイベントが来る**ので閉路ではない (経路購読 B3 の外に居るだけ)。
    /// <see cref="SlotSubscriptionState.Unresolved"/> を含めると、アプリ未起動のトリガーが
    /// 常に「壊れている」になる — どちらも設計の売りを消す壊れ方である。
    /// </summary>
    [Theory]
    [InlineData((int)SlotSubscriptionState.Unresolved, false)]
    [InlineData((int)SlotSubscriptionState.WaitingSubscribed, false)]
    [InlineData((int)SlotSubscriptionState.WaitingOrphaned, true)]
    [InlineData((int)SlotSubscriptionState.Resolved, false)]
    [InlineData((int)SlotSubscriptionState.ResolvedWindowSelf, false)]
    [InlineData((int)SlotSubscriptionState.ResolvedSubtreeFallback, false)]
    [InlineData((int)SlotSubscriptionState.ResolvedOrphaned, true)]
    public void IsOrphaned_OnlyForTheTwoOrphanedStates(int state, bool expected)
    {
        Assert.Equal(expected, SubscriptionHealth.IsOrphaned((SlotSubscriptionState)state));
    }

    /// <summary>
    /// 購読を 1 件でも持つ未解決スロットは、ウィンドウの有無にかかわらず孤立ではないこと。
    /// 「0 件かどうか」が先に来る条件であることを、ハンドル側を動かして固定する。
    /// </summary>
    [Theory]
    [InlineData(1)]
    [InlineData(5)]
    public void AWaitingSlotWithAnySubscription_IsNeverOrphaned(int structureCount)
    {
        Assert.Equal(
            SlotSubscriptionState.WaitingSubscribed,
            SubscriptionHealth.StateOf(false, 0, structureCount, SomeWindow, false, false, false));
        Assert.Equal(
            SlotSubscriptionState.WaitingSubscribed,
            SubscriptionHealth.StateOf(false, 0, structureCount, 0, false, false, false));
    }
}
