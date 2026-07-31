// 構造変化の購読を「持つべきなのに持っていない」トリガーの判定 (docs/DESIGN.md §8)。
//
// 切り出してあるのは、ここを間違えると**直したつもりで設計の売りを壊す**ためである。
// 判定が緩いと、まだ起動していないアプリのトリガーが常に「壊れている」と見なされ、
// 復旧の再試行が回りっぱなしになる = ポーリングしないという性質が消える。
// 純関数なので T1 が表で縛れる。
namespace UiaTrigger.Monitoring;

/// <summary>
/// 構造変化の購読を失ったトリガーを見分ける規則。
/// </summary>
/// <remarks>
/// <para>
/// **購読が 0 件であること自体は異常ではない。**4 つの場合があり、
/// 拾いたいのは最後の 1 つだけである:
/// </para>
/// <list type="table">
///   <item><description>
///     **解決済み・経路購読** — 0 件ではない。
///   </description></item>
///   <item><description>
///     **解決済みで対象がウィンドウ自身** (経路が空) — 0 件で、ウィンドウも記録しない。
///     消滅は <c>WindowClosed</c> が拾うので正常である。
///   </description></item>
///   <item><description>
///     **未解決でウィンドウがまだ無い** (アプリが起動していない) — 0 件で、ウィンドウも 0。
///     出現は <c>WindowOpened</c> が拾うので正常である。
///   </description></item>
///   <item><description>
///     **未解決でウィンドウはあるのに購読できなかった** — 0 件だがウィンドウは記録されている。
///     **これだけが「戻る道の無い」状態である**。購読が無い → 構造変化イベントが来ない →
///     掃引が走らない → 購読が張り直されない、という閉路に入り、
///     例外も通知も出ないままそのトリガーは永久に解決しなくなる。
///   </description></item>
/// </list>
/// <para>
/// 判定に使うのは既存の 2 つの状態だけで、新しいフィールドは要らない。
/// 「張ろうとしたウィンドウ」は購読に失敗したときも記録されるので、
/// **3 つ目と 4 つ目はウィンドウハンドルで分かれる**。
/// </para>
/// </remarks>
internal static class SubscriptionHealth
{
    /// <summary>そのトリガーが購読を失っているか。</summary>
    /// <param name="subscribedElements">実際に購読できている要素の数。</param>
    /// <param name="windowHandle">
    /// 購読しようとしたウィンドウ。0 は「ウィンドウを特定できていない」であり、
    /// **失敗ではない** (アプリ未起動か、対象がウィンドウ自身)。
    /// </param>
    public static bool IsOrphaned(int subscribedElements, nint windowHandle)
        => subscribedElements == 0 && windowHandle != 0;
}
