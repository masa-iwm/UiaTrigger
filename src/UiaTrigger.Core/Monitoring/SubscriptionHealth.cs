// スロットの購読状態の分類 (docs/DESIGN.md §8)。
//
// 切り出してあるのは、ここを間違えると**直したつもりで設計の売りを壊す**ためである。
// 判定が緩いと、まだ起動していないアプリのトリガーが常に「壊れている」と見なされ、
// 復旧の再試行が回りっぱなしになる = ポーリングしないという性質が消える。
// 逆に厳しいと、購読を失ったスロットが二度と拾われない (§8 の閉路)。
// 純関数なので T1 が全状態を表で縛れる。
namespace UiaTrigger.Monitoring;

/// <summary>
/// スロットの実状態 (解決 × 構造購読 × プロパティ購読 × 孤児の目印)。
/// </summary>
/// <remarks>
/// <para>
/// フィールドの暗黙の組合せを列挙に起こしたもの。掃引の対象選定・修復 (SubscriptionRepair)
/// の武装・診断の孤児数は、**すべてこの状態から導出する** — 導出をサイトごとの条件式で
/// 持つと、到達可能な異常状態の一部 (解決済みの購読喪失など) がどの条件にも掛からず、
/// 無音の恒久沈黙 (§8 が最も避けたいと呼ぶ壊れ方) になる。
/// </para>
/// <para>
/// **購読が 0 件であること自体は異常ではない。**未解決側は「アプリ未起動」(出現は
/// WindowOpened が拾う) が、解決済み側は「対象がウィンドウ自身」(消滅は WindowClosed が
/// 拾う) が、それぞれ購読 0 の正常な形である。修復対象は Orphaned の 2 状態だけ。
/// </para>
/// </remarks>
internal enum SlotSubscriptionState
{
    /// <summary>未解決・購読 0・目印 0。アプリ未起動は正常 — 出現は WindowOpened が拾う。</summary>
    Unresolved,

    /// <summary>未解決・ウィンドウ全体の Subtree 購読中。出現待ちの正常形。</summary>
    WaitingSubscribed,

    /// <summary>
    /// 未解決・購読 0 だが「張ろうとしたウィンドウ」は記録されている。
    /// 購読が無い → イベントが来ない → 掃引が走らない → 張り直されない、という
    /// **戻る道の無い閉路** (§8)。修復の再試行だけが出口である。
    /// </summary>
    WaitingOrphaned,

    /// <summary>解決済み・経路購読健全。</summary>
    Resolved,

    /// <summary>
    /// 解決済み・対象がウィンドウ自身 (経路 0 段)。購読 0 が正常 —
    /// 消滅は WindowClosed と IsWindow (A21) が拾う。
    /// </summary>
    ResolvedWindowSelf,

    /// <summary>
    /// 解決済みだが未解決期の Subtree 購読のまま (経路への張り替えに失敗した)。
    /// イベントは来るので閉路ではないが、B3 (経路購読) の外に居る — 掃引時に張り替えを再試行する。
    /// </summary>
    ResolvedSubtreeFallback,

    /// <summary>
    /// 解決済み・購読喪失 (構造購読 0、またはプロパティ購読を張るべきなのに張れていない)。
    /// 掃引は解決済みスロットを再解決しないため、放置すると復旧経路ゼロで恒久沈黙する —
    /// 未解決側の閉路 (WaitingOrphaned) と同じ扱いで修復対象。
    /// </summary>
    ResolvedOrphaned,
}

/// <summary>スロットのフィールドから <see cref="SlotSubscriptionState"/> を導出する規則。</summary>
/// <remarks>
/// 状態は**保存せず、毎回ここで導出する**。enum をフィールドに持つと既存フィールドとの
/// 二重管理になり、更新漏れのずれが新しい不具合の形になる。
/// </remarks>
internal static class SubscriptionHealth
{
    /// <param name="resolved">要素を掴んでいるか (<c>Element is not null</c>)。</param>
    /// <param name="pathDepth">解決時の経路段数。0 = 対象がウィンドウ自身。未解決では無意味。</param>
    /// <param name="structureCount">実際に購読できている構造購読の要素数。</param>
    /// <param name="attemptedHwnd">
    /// 購読しようとしたウィンドウ。0 は「ウィンドウを特定できていない」であり、
    /// **失敗ではない** (アプリ未起動か、対象がウィンドウ自身)。
    /// </param>
    /// <param name="pathScoped">true = 経路購読 / false = ウィンドウ全体の Subtree 購読。</param>
    /// <param name="wantsProperties">このスロットにプロパティ購読を張るべきか。</param>
    /// <param name="hasPropertyHandler">プロパティ購読が実際に張れているか。</param>
    public static SlotSubscriptionState StateOf(
        bool resolved, int pathDepth, int structureCount, nint attemptedHwnd,
        bool pathScoped, bool wantsProperties, bool hasPropertyHandler)
    {
        if (!resolved)
        {
            if (structureCount > 0)
            {
                return SlotSubscriptionState.WaitingSubscribed;
            }
            return attemptedHwnd != 0
                ? SlotSubscriptionState.WaitingOrphaned
                : SlotSubscriptionState.Unresolved;
        }
        if (wantsProperties && !hasPropertyHandler)
        {
            // プロパティ購読を張るべきなのに張れていない = 「解決済みだが聞こえない」。
            // 変化通知が一切来ないので、構造購読の有無によらず修復対象である
            return SlotSubscriptionState.ResolvedOrphaned;
        }
        if (pathDepth == 0)
        {
            return SlotSubscriptionState.ResolvedWindowSelf;
        }
        if (structureCount == 0)
        {
            return SlotSubscriptionState.ResolvedOrphaned;
        }
        return pathScoped
            ? SlotSubscriptionState.Resolved
            : SlotSubscriptionState.ResolvedSubtreeFallback;
    }

    /// <summary>修復 (SubscriptionRepair) の対象か。</summary>
    /// <remarks>
    /// <see cref="SlotSubscriptionState.ResolvedSubtreeFallback"/> は**含めない** —
    /// 購読はあり、イベントは来る = 掃引の機会がある側なので、掃引時の機会修復で足りる。
    /// 含めると、張り替えに失敗し続ける相手に対して復旧タイマーが回りっぱなしになる。
    /// </remarks>
    public static bool IsOrphaned(SlotSubscriptionState state) =>
        state is SlotSubscriptionState.WaitingOrphaned or SlotSubscriptionState.ResolvedOrphaned;
}
