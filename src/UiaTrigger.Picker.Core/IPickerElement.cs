// プレゼンターから見た「要素 1 個」(docs/DESIGN.md §12)。
//
// UiaElement は唯一のコンストラクタが private で、引数が Core の internal な COM
// インターフェース (IUIAutomationElement) である。つまり**テストから偽の要素を作れない**。
// 要素が絡むプレゼンターの規則 (チェーンの形・子の差し込み・重なり切替) は
// 要素を作れないかぎり 1 件もテストできないので、ここに継ぎ目を切って T1 で覆う。
//
// メンバーはプレゼンターが実際に読むものだけに絞ってある。UiaElement の
// ProcessId / Name / ClassName / NativeWindowHandle / IsOffscreen は継ぎ目の内側
// (PickerServices) でしか使わないため載せていない — 使わないフィールドは置かない。
using UiaTrigger.Models;

namespace UiaTrigger.Picker;

/// <summary>ピッカーが扱う UI 要素 1 個。</summary>
/// <remarks>
/// <para>
/// **これは相手プロセスの provider を掴んでいるハンドルである。**受け取った側が持ち主になり、
/// 保持しないものは解放する責任を負う。ファイナライザー任せにすると、大きなツリーを歩いた
/// あいだに数千個の跨プロセス参照が生きたままになる (docs/DESIGN.md §7)。
/// </para>
/// <para>
/// 値のメンバー 4 つは生成時のスナップショットなので、**解放後も読める**。
/// 解放済みのハンドルで失敗するのは <see cref="IPickerServices"/> へ渡したときだけであり、
/// そこでは静かに間違うのではなく例外になる。
/// </para>
/// </remarks>
internal interface IPickerElement : IDisposable
{
    /// <summary>要素の automation id。無ければ空文字列。</summary>
    string AutomationId { get; }

    /// <summary>コントロール型の安定名 (既定 id の組み立てに使う。docs/DESIGN.md L6)。</summary>
    string ControlTypeName { get; }

    /// <summary>画面に出す表示名 (相手アプリのロケール。docs/DESIGN.md L6)。</summary>
    string DisplayLabel { get; }

    /// <summary>物理スクリーン座標での矩形。</summary>
    ElementRect BoundingRectangle { get; }
}
