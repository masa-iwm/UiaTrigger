// スロットの観測値ストア (docs/DESIGN.md C15/C16)。
//
// 評価が読む観測値は 2 系統ある — named プロパティのスナップショットと、
// TriggerProperty.Custom の直前値 (Custom はキャッシュ済みスナップショットに入らない)。
// この 2 つを別々のフィールドで持つと、「消えた要素は最後に見えた値で評価し続ける」
// (C15/C16) のような関門側の不変条件が snapshot 側にだけ足され、Custom だけが
// 系統的に欠け続ける。単一の窓口に束ね、評価は必ずここ経由で読む。
//
// **要素が消えてもストアは消さない。**「最後に見えた値」こそが消滅後の評価の根拠である
// (在否 IsAbsent だけが変わる)。消すのはスロットが再解決されて新しい値で上書きされるときだけ。
using UiaTrigger.Models;

namespace UiaTrigger.Monitoring;

internal sealed class SlotObservations
{
    private Dictionary<int, ClauseValue>? _custom;

    /// <summary>最後に読めたスナップショット。未解決のままなら null。</summary>
    public ElementPropertySnapshot? Snapshot { get; private set; }

    public void UpdateSnapshot(ElementPropertySnapshot snapshot) => Snapshot = snapshot;

    /// <summary>
    /// <see cref="TriggerProperty.Custom"/> の最終読み値
    /// (鍵は <see cref="PropertyClause.CustomPropertyId"/>)。
    /// 鍵が句ではなくプロパティ ID なのは、同じスロットの 2 つの句が同じ ID を
    /// 指しうるからである (値は同じなので 1 つ持てば足りる)。
    /// </summary>
    public void UpdateCustom(int propertyId, ClauseValue value) => (_custom ??= [])[propertyId] = value;

    public bool TryGetCustom(int propertyId, out ClauseValue value)
    {
        if (_custom is not null && _custom.TryGetValue(propertyId, out value))
        {
            return true;
        }
        value = default;
        return false;
    }

    /// <summary>
    /// Custom 値の写し (ポーリング周の「前」の値)。評価が上書きする前に取っておくためのもの。
    /// 1 つも無ければ null。
    /// </summary>
    public Dictionary<int, ClauseValue>? SnapshotCustomValues() =>
        _custom is { Count: > 0 } ? new Dictionary<int, ClauseValue>(_custom) : null;
}
