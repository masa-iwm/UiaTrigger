// 解決ループと UIA の継ぎ目。
//
// 経路解決は本来「木を辿って候補を採点する」だけの純ロジックであり、具象 UiaContext に
// 直結させると単体テストの継ぎ目が無くなる (docs/DESIGN.md D1)。
// ビーム探索を COM 無しで検証できるよう、解決ループが要素に対して必要とする操作を
// ここに切り出す。実体は UiaTrigger.Interop.UiaElementTree、テストでは擬似ツリーを差し込む。
using UiaTrigger.Models;

namespace UiaTrigger.Resolution;

/// <summary>
/// 解決ループから見た「要素 1 個」。
/// 識別に使うプロパティは生成時に **1 往復で** まとめて読んである (docs/DESIGN.md B1) ため、
/// これらの取得は追加のクロスプロセス呼び出しを起こさない。
/// </summary>
internal interface IElementNode
{
    int ProcessId { get; }

    /// <summary>UIA ControlType ID。0 は取得できなかったことを表す。</summary>
    int ControlType { get; }

    string AutomationId { get; }

    string Name { get; }

    string ClassName { get; }
}

/// <summary>
/// 「掴んでいる要素が作り直されていないか」を見るための安定属性の組 (docs/DESIGN.md A8)。
///
/// <see cref="IElementNode.Name"/> は含めない。Name は最も多く監視対象になる可変プロパティであり、
/// 含めると「監視対象が変化しただけ」で再解決が走ってしまう。
/// ControlType の変化は「別物になった」と扱う — 記録された経路の ControlType とも
/// 食い違うため、そのまま監視を続けるより再解決したほうが正しい
/// (ControlType が状態で変わる要素の追従そのものは A5 の課題)。
/// </summary>
internal readonly record struct ElementIdentity(int ProcessId, int ControlType, string AutomationId, string ClassName)
{
    public static ElementIdentity Of(IElementNode node)
    {
        ArgumentNullException.ThrowIfNull(node);
        return new ElementIdentity(node.ProcessId, node.ControlType, node.AutomationId, node.ClassName);
    }
}

/// <summary>解決ループが要素ツリーに要求する操作。</summary>
internal interface IElementTree
{
    /// <summary>ウィンドウハンドルに対応する要素。UIA を提供していないウィンドウなら null。</summary>
    IElementNode? GetWindow(nint hwnd);

    /// <summary>
    /// 指定ビューでの子要素を **1 往復で** 取得する。
    /// 返ったノードのうち保持しないものは <see cref="Release"/> すること。
    /// </summary>
    /// <param name="parent">親要素。</param>
    /// <param name="view">辿るビュー。</param>
    /// <param name="max">走査する子の上限。</param>
    IReadOnlyList<IElementNode> GetChildren(IElementNode parent, TreeViewMode view, int max);

    /// <summary>
    /// 要素の識別プロパティを 1 往復で読み直す (A8 の同一性再検証)。
    /// 要素が既に無ければ <see cref="System.Runtime.InteropServices.COMException"/>。
    /// </summary>
    ElementIdentity ReadIdentity(IElementNode node);

    /// <summary>
    /// 2 つのノードが同じ要素を指すか。
    /// UIA は同じ要素に対して呼び出しごとに別のオブジェクトを返すため、参照比較は使えない。
    /// </summary>
    bool AreSame(IElementNode a, IElementNode b);

    /// <summary>
    /// <paramref name="scope"/> の部分木から、指定ビューで <paramref name="automationId"/> が
    /// 一致する要素を最大 <paramref name="max"/> 件 **1 往復で** 取得する
    /// (<see cref="ElementSearch"/> の Search 方式)。
    /// </summary>
    /// <remarks>
    /// 上限を設けるのは「一意かどうか」を知りたいだけで全件は要らないため。
    /// 返ったノードのうち保持しないものは <see cref="Release"/> すること。
    /// </remarks>
    IReadOnlyList<IElementNode> FindByAutomationId(IElementNode scope, TreeViewMode view, string automationId, int max);

    /// <summary>
    /// 指定ビューでの親要素を 1 往復で取得する。ビューの最上位なら null。
    /// Search 方式で見つけた要素から、経路購読 (docs/DESIGN.md B3) 用の祖先鎖を作るのに使う。
    /// </summary>
    IElementNode? GetParent(IElementNode node, TreeViewMode view);

    /// <summary>不要になったノードを決定的に解放する (docs/DESIGN.md B6)。以後そのノードは使えない。</summary>
    void Release(IElementNode? node);
}
