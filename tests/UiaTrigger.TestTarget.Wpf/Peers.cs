// ネイティブ UIA プロバイダー (WPF) 側のツリーの形と、実行時に変えられる ControlType。
//
// WinForms 版は「コントロール = HWND」なので UIA のツリーがそのまま出るが、WPF は
// AutomationPeer を持つ要素しかツリーに出ない。素の Canvas は peer を作らないため、
// 何もしないと Window の直下にボタンが並び、WinForms と段数が変わってしまう。
//
// 段数をプロファイルごとに可変にする逃げ道は採らない —— 期待値をアプリが出した形に
// 合わせると「アプリが出した形をそのまま確認するだけ」の検出力ゼロの assert になる。
// ここで IsControlElementCore() => true にしてツリーの形を WinForms に揃え、
// テスト側は Steps.Count == 2 / Depth + 2 を両プロファイルで維持する。
using System.Windows;
using System.Windows.Automation;
using System.Windows.Automation.Peers;
using System.Windows.Controls;

namespace UiaTrigger.TestTarget.Wpf;

/// <summary>
/// Control view に出るコンテナ。WinForms の <c>Panel</c> に相当する。
/// </summary>
internal sealed class TestPanel : Canvas
{
    // Canvas は子を切り取らない。経路外に足した子が対象のボタンに重なると
    // 座標からの記録が別要素を掴む (docs/DESIGN.md A19 と同じ壊れ方)
    public TestPanel() => ClipToBounds = true;

    protected override AutomationPeer OnCreateAutomationPeer()
        => new SwitchablePeer(this, AutomationControlType.Pane, isLeaf: false);
}

/// <summary>
/// 実行時に ControlType を差し替えられるボタン。
/// WinForms の <c>Control.AccessibleRole</c> に相当する (docs/DESIGN.md A5 / A8)。
/// </summary>
internal sealed class TestButton : Button
{
    protected override AutomationPeer OnCreateAutomationPeer()
        => new SwitchablePeer(this, AutomationControlType.Button, isLeaf: true);
}

/// <summary>実行時に ControlType を差し替えられるラベル。</summary>
internal sealed class TestLabel : Label
{
    protected override AutomationPeer OnCreateAutomationPeer()
        => new SwitchablePeer(this, AutomationControlType.Text, isLeaf: true);
}

/// <summary>
/// <b>要素を作り直さずに</b> ControlType だけを変えられる peer。
///
/// A8 (「生きているが別物」) の前提はまさにこれで、ピアを作り直してしまうと
/// クライアントからは「要素消滅 → 別要素の出現」に見え、同一性の再検証を通らないまま
/// テストが<b>間違った理由で通る</b> (docs/TESTING.md §1)。
/// そのため <see cref="AutomationPeer.InvalidatePeer"/> ではなく
/// 値の差し替え + プロパティ変化イベントで済ませる。
/// </summary>
internal sealed class SwitchablePeer(FrameworkElement owner, AutomationControlType controlType, bool isLeaf)
    : FrameworkElementAutomationPeer(owner)
{
    private AutomationControlType _controlType = controlType;

    public AutomationControlType ControlType
    {
        get => _controlType;
        set
        {
            AutomationControlType old = _controlType;
            _controlType = value;
            RaisePropertyChangedEvent(
                AutomationElementIdentifiers.ControlTypeProperty,
                ControlTypeIdOf(old),
                ControlTypeIdOf(value));
        }
    }

    protected override AutomationControlType GetAutomationControlTypeCore() => _controlType;

    /// <summary>
    /// コンテナも Control view に出す。WPF の既定では Canvas 等のレイアウト要素は
    /// Control view に現れず、ツリーの段数が WinForms と変わってしまう。
    /// </summary>
    protected override bool IsControlElementCore() => true;

    /// <summary>
    /// ボタン・ラベルを<b>葉</b>にする。
    ///
    /// WPF の <c>Button</c> はテンプレートの <c>ContentPresenter</c> が文字列を
    /// <c>TextBlock</c> として実体化し、その <c>TextBlockAutomationPeer</c> が
    /// Control view に出る。そのままだと座標からの記録が <c>AutomationId</c> を持たない
    /// TextBlock を掴み、WinForms より 1 段深い経路になってしまう。
    /// 段数をプロファイルで可変にする逃げ道は採らないと決めたので、ここで形を揃える。
    /// </summary>
    protected override List<AutomationPeer>? GetChildrenCore()
        => isLeaf ? null : base.GetChildrenCore();

    protected override string GetClassNameCore() => Owner.GetType().Name;

    /// <summary>
    /// UIA のプロパティ変化イベントは <c>ControlType</c> の <b>ID</b> を運ぶ。
    /// <see cref="AutomationControlType"/> をそのまま渡すと受け手が解釈できない。
    /// </summary>
    private static int ControlTypeIdOf(AutomationControlType type) => type switch
    {
        AutomationControlType.Button => 50000,
        AutomationControlType.Edit => 50004,
        AutomationControlType.Pane => 50033,
        AutomationControlType.Text => 50020,
        _ => 50033,
    };
}
