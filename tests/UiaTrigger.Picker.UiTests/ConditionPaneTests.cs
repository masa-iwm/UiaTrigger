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
    /// いちばん広い行は 724px あり、既定の窓 (1100px 幅 / 右側は 5/11) では必ずはみ出す。
    /// <c>HorizontalScrollMode</c> が <c>Disabled</c> だと**右が黙って切れる** —
    /// 例外も、切れたという表示も出ない (実測)。
    /// </para>
    /// <para>
    /// **負の対照は置けない。**「広ければ横バーは出ない」を見るには条件欄を 724px より
    /// 広げる必要があり、それには区切りを掴むか窓を広げるかで**合成入力が要る** (T5 の担当)。
    /// つまりこのテストが言えるのは「狭いときに横へ動ける」ことだけである。
    /// **「広い窓でも常に横バーが出る」= 折り返しが死んだ形は、ここでは捕まらない** —
    /// そちらは MANUAL-CHECKS §4.3.1 の目視に置いてある。
    /// </para>
    /// <para>
    /// 退行: XAML の <c>HorizontalScrollMode</c> を <c>Disabled</c> に戻す → 落ちる (実測)。
    /// </para>
    /// </remarks>
    [Fact]
    public void TheWinUiConditionPaneCanScrollSideways()
    {
        PickerHostProfile profile = PickerHostProfile.WinUI;
        using PickerHostProcess host = PickerHostProcess.StartWithoutATarget(profile, "en-US");
        host.OpenPicker();

        AutomationElement picker = host.PickerWindow();
        AutomationElement scroll = picker.RequireByIdEventually("ConditionScroll", host.Diagnostics);

        Assert.True(
            scroll.TryGetCurrentPattern(ScrollPattern.Pattern, out object? raw),
            "条件欄の ScrollViewer が Scroll パターンを出していません。" + host.Diagnostics());

        ScrollPattern.ScrollPatternInformation info = ((ScrollPattern)raw).Current;
        string measured = string.Create(
            CultureInfo.InvariantCulture,
            $"横={info.HorizontallyScrollable} (見えている割合 {info.HorizontalViewSize}%) " +
            $"縦={info.VerticallyScrollable} (見えている割合 {info.VerticalViewSize}%)");

        Assert.True(
            info.HorizontallyScrollable,
            $"条件欄が横にスクロールできません。右にはみ出した欄は黙って切れます。{measured}");
    }
}
