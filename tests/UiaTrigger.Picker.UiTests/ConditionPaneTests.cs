// T4: 条件欄が**横にスクロールできる**こと (docs/DESIGN.md §12)。
//
// **ここでしか見られない。**WinUI3 の View は T1 から組み立てられず (docs/DESIGN.md §12)、WPF に写して
// 見ることもできない — あちらの条件欄の行は WrapPanel なので**折り返す**ので、
// そもそも同じ壊れ方をしない。T1 に在るのはソースの形の検査だけである
// (UiaTrigger.Core.Tests/PickerConditionPaneTests)。
//
// 起動は StartWithoutATarget である (SplitterTests の冒頭と同じ理由 —
// StartForLabels は発行レイアウトを優先するので、XAML を直しても発行し直すまで古い配置を通す)。
using System.Globalization;
using System.Windows.Automation;
using Xunit;

namespace UiaTrigger.Picker.UiTests;

public sealed class ConditionPaneTests
{
    /// <summary>
    /// WinUI の条件欄が横にスクロールできること。
    /// </summary>
    /// <remarks>
    /// <para>
    /// 条件欄の行は <c>Orientation="Horizontal"</c> の <c>StackPanel</c> で**折り返さない**。
    /// いちばん広い行は約 912px あり、窓が狭ければ必ずはみ出す。
    /// <c>HorizontalScrollMode</c> が <c>Disabled</c> だと**右が黙って切れる** —
    /// 例外も、切れたという表示も出ない (実測)。
    /// </para>
    /// <para>
    /// **窓の幅は <see cref="DesktopLayout.NarrowConditionHost"/> で固定する。**ハーネスは
    /// ホストの窓を割り付けの矩形へ退かす際に**寸法も合わせる**ので、既定の
    /// <see cref="DesktopLayout.Host"/> だと窓幅が画面の広さに比例してしまう (ピッカー自身の
    /// 既定サイズは、退かされた時点で上書きされるので効かない)。条件欄は**窓の全幅**を
    /// 使うので、1100px (<see cref="DesktopLayout.NarrowHost"/>) でも 96 DPI では最広行が
    /// 収まってしまう。900px 固定なら条件欄は 96 DPI で約 870px となり、
    /// **表示スケールが上がるほど内容だけが大きくなる** (窓は物理ピクセル固定) ので、
    /// はみ出しはどの機械でも成立する。
    /// </para>
    /// <para>
    /// **負の対照は置けない。**「広ければ横バーは出ない」を見るには条件欄を 724px より
    /// 広げる必要があり、それには区切りを掴むか窓を広げるかで**合成入力が要る** (T5 の担当)。
    /// つまりこのテストが言えるのは「狭いときに横へ動ける」ことだけである。
    /// **「広い窓でも常に横バーが出る」= 折り返しが死んだ形は、ここでは捕まらない** —
    /// そちらは MANUAL-CHECKS §4.3.1 の目視に置いてある。
    /// </para>
    /// <para>
    /// 退行: XAML の <c>ConditionScroll</c> から <c>HorizontalScrollMode</c> と
    /// <c>HorizontalScrollBarVisibility</c> を**両方**外す → 落ちる (実測。
    /// 本文は「横=False (見えている割合 100%)」— 切り落とされているので割合は 100% になる)。
    /// </para>
    /// <para>
    /// **<c>HorizontalScrollMode</c> だけを <c>Disabled</c> にしても落ちない** (実測)。
    /// <c>HorizontalScrollBarVisibility="Auto"</c> がスクロールを生かしたままにするためで、
    /// 2 つは独立した設定ではない。**片方だけの退行はここでは捕まらない**が、
    /// 属性の対は T1 (<c>PickerConditionPaneTests</c>) が縛っている。
    /// </para>
    /// </remarks>
    [Fact]
    public void TheWinUiConditionPaneCanScrollSideways()
    {
        PickerHostProfile profile = PickerHostProfile.WinUI;
        using PickerHostProcess host = PickerHostProcess.StartWithoutATarget(
            profile, "en-US", DesktopLayout.NarrowConditionHost);
        host.OpenPicker();

        AutomationElement picker = host.PickerWindow();
        AutomationElement scroll = picker.RequireByIdEventually("ConditionScroll", host.Diagnostics);

        Assert.True(
            scroll.TryGetCurrentPattern(ScrollPattern.Pattern, out object? raw),
            "条件欄の ScrollViewer が Scroll パターンを出していません。" + host.Diagnostics());

        ScrollPattern.ScrollPatternInformation info = ((ScrollPattern)raw).Current;
        System.Windows.Rect window = picker.Current.BoundingRectangle;
        string measured = string.Create(
            CultureInfo.InvariantCulture,
            $"横={info.HorizontallyScrollable} (見えている割合 {info.HorizontalViewSize}%) " +
            $"縦={info.VerticallyScrollable} (見えている割合 {info.VerticalViewSize}%) " +
            $"ピッカーの窓={window.Width}x{window.Height}");

        Assert.True(
            info.HorizontallyScrollable,
            $"条件欄が横にスクロールできません。右にはみ出した欄は黙って切れます。{measured}");
    }
}
