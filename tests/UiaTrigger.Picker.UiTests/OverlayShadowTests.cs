// T4: オーバーレイが<b>UIA のヒットテストで対象を隠していないか</b> (docs/DESIGN.md §9)。
//
// <b>これは測定である。</b>「2 枚目のピッカーで左隣の要素を捕捉しない
// (対象はデスクトップのアイコン)」という報告がある。見立ては次のとおりで、ここで測る:
//
//   枠のウィンドウは選択中の要素の上に在る。ヒットテスト (マウス) は WS_EX_TRANSPARENT で
//   抜けるが、<b>UIA の ElementFromPoint が同じように抜けるとは限らない</b>。抜けないなら
//   自プロセスの要素が返り、UiaSession.ElementFromPointCore は SkipOwnProcessElements により
//   <b>null を返す</b> — つまり「そこには何も無い」と同じ扱いになり、捕捉が起きない。
//
// デスクトップのアイコンで出てほかのアプリで出ないのは、項目の矩形が隣と重なりやすいためだ、
// というのが見立ての残り半分である (隣へ動いた先が、まだ前の要素の枠の中に在る)。
using System.Globalization;
using System.Windows.Automation;
using Xunit;

namespace UiaTrigger.Picker.UiTests;

public sealed class OverlayShadowTests
{
    /// <summary>
    /// 枠が出ている要素の上で、UIA のヒットテストが<b>対象アプリ</b>を返すこと。
    /// </summary>
    /// <remarks>
    /// <para>
    /// 返ってくるのがピッカーのプロセス (= 枠かアイコンの窓) なら、そこはもう
    /// <b>捕捉できない場所</b>になっている。ピッカーは「自プロセスの上ではホバー捕捉しない」
    /// という規則を持っており、それが<b>自分の枠に対しても効いてしまう</b>ためである。
    /// </para>
    /// <para>
    /// 見るのは<b>枠の中で、確定アイコンではない点</b>である。アイコンの上は
    /// 「捕捉しない」が意図された仕様なので (docs/DESIGN.md §11 / TickAsync の除外)、混ぜてはいけない。
    /// </para>
    /// </remarks>
    [Fact]
    public void TheOverlay_DoesNotHideTheTargetFromUiaHitTesting()
    {
        using OverlayScenario scenario = OverlayScenario.Open(1);
        List<System.Windows.Rect> frames = scenario.WaitForOverlays(1);
        System.Windows.Rect frame = frames[0];

        // 枠の中心。確定アイコンは右上に 20px 角で出るので、中心とは重ならない
        var point = new System.Windows.Point(
            frame.Left + (frame.Width / 2), frame.Top + (frame.Height / 2));

        AutomationElement at = AutomationElement.FromPoint(point);
        int pid = at.Current.ProcessId;
        string measured = string.Create(
            CultureInfo.InvariantCulture,
            $"枠={frame} 見た点=({point.X},{point.Y}) → pid {pid} " +
            $"'{at.Current.Name}' class='{at.Current.ClassName}' / " +
            $"ピッカーのホスト pid={scenario.Host.ProcessId}");

        Assert.False(
            pid == scenario.Host.ProcessId,
            "枠のウィンドウが UIA のヒットテストで対象を隠しています。" +
            "この点ではホバー捕捉が起きません (自プロセスの要素は捨てられるため)。" + measured);
    }
}
