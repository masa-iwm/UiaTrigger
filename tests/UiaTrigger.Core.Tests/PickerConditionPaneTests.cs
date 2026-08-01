// 条件欄 (ピッカー右下) の横方向 — docs/DESIGN.md §12。
//
// 縦の高さ上限は別に手当てされている (docs/DESIGN.md §12)。横は Disabled のままだと
// 「WinUI で条件設定が左右にはみ出ているのに水平スクロールが出ない」(実測)。
//
// **WinUI の View は T1 から組み立てられない** (docs/DESIGN.md §12: WinUI3 / x64 / WindowsAppSDK)。
// WPF に写して見ることもできない — あちらの行は WrapPanel で**折り返す**ので、
// そもそも同じ壊れ方をしない。つまりこの変種には振る舞いを見る足場が T1 に無い。
//
// ここで見るのは**ソースの形**である。HostMonitorTests と同じ立場で、
// 塞げるのは「腐る側」= 直しが黙って外れることだけである。
// **「はみ出したら横バーが出る」を実際に見るのは T4 と人の担当である。**
using Xunit;

namespace UiaTrigger.Tests;

public sealed class PickerConditionPaneTests
{
    private const string WinUiPicker = "src/UiaTrigger.Picker.WinUI";

    private static string Read(string relativePath)
    {
        string path = RepoPaths.Combine(relativePath.Split('/'));
        Assert.True(File.Exists(path), $"ファイルがありません: {path}");
        return File.ReadAllText(path);
    }

    /// <summary>
    /// WinUI の条件欄が横にスクロールできること。
    ///
    /// <para>
    /// 行は <c>Orientation="Horizontal"</c> の <c>StackPanel</c> で折り返さず、いちばん広い行は
    /// 約 912px ある。窓を狭めると**右が黙って切れる** — 例外も、切れたという表示も出ない。
    /// </para>
    /// <para>
    /// **2 つは独立した設定ではない。**<c>HorizontalScrollBarVisibility="Auto"</c> が在れば
    /// <c>HorizontalScrollMode</c> を <c>Disabled</c> にしてもスクロールは生きたままである
    /// (実測)。つまり**片方だけを外した退行は振る舞いには出ない**ので、対で縛るここが
    /// その退行に対する唯一の網である。
    /// </para>
    /// </summary>
    [Theory]
    [InlineData("HorizontalScrollMode=\"Enabled\"")]
    [InlineData("HorizontalScrollBarVisibility=\"Auto\"")]
    public void TheWinUiConditionPaneCanScrollSideways(string fragment)
    {
        string xaml = Read($"{WinUiPicker}/TriggerPickerWindow.xaml");
        int scroll = xaml.IndexOf("x:Name=\"ConditionScroll\"", StringComparison.Ordinal);
        Assert.True(scroll >= 0, "ConditionScroll が XAML にありません。");

        // 同じ属性はツリーとプロパティ一覧にも付いているので、**条件欄の要素の中だけ**を見る。
        // ここを緩めて全文検索にすると、条件欄から外しても他所の記述で緑になる
        int end = xaml.IndexOf('>', scroll);
        Assert.True(end > scroll, "ConditionScroll の要素が閉じていません。");

        Assert.Contains(fragment, xaml[scroll..end], StringComparison.Ordinal);
    }

    /// <summary>
    /// 折り返す文字列に幅の上限を渡していること。
    ///
    /// <para>
    /// **上の 2 つとひと組である。**横スクロールを有効にすると測りに渡る幅が無限になり、
    /// <c>TextWrapping="Wrap"</c> が何もしなくなる。すると確定済みの文言が 1 行のまま伸びて、
    /// **広い窓でも常に横バーが出た状態**になる — 直したつもりで別の壊れ方を入れる形である。
    /// </para>
    /// </summary>
    [Fact]
    public void TheWrappingTextInTheWinUiConditionPaneIsGivenAWidth()
    {
        string code = Read($"{WinUiPicker}/TriggerPickerWindow.xaml.cs");
        Assert.Contains("ConfirmedText.MaxWidth", code, StringComparison.Ordinal);
    }
}
