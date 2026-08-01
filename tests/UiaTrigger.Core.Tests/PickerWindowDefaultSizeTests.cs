// 3 変種の窓が同じ既定サイズで開くこと (docs/DESIGN.md §12)。
//
// WPF は XAML の Width / Height、Windows Forms は ClientSize で宣言できるが、
// **WinUI3 の Window にはその口が無い** — 何もしないと OS が決めた大きさ (画面に比例) で
// 開くので、高解像度では窓が画面をほぼ埋め、同じピッカーが変種ごとに別物に見える。
// WinUI 側は AppWindow.ResizeClient を呼ぶことで揃えている。
//
// **見るのはソースの形である** (PickerConditionPaneTests と同じ立場)。WinUI の View は
// T1 から組み立てられず (docs/DESIGN.md §12)、T4 はホストの窓を割り付けの矩形へ
// 退かすときに**寸法も合わせる**ので、既定サイズは T4 からも観測できない。
// つまり塞げるのは「腐る側」= 3 つが黙ってばらけることだけである。
// **実際にその大きさで開くこと自体は人が見る** (docs/MANUAL-CHECKS.md §4.3.1)。
//
// 3 つの数字は厳密には同じものを指していない: WPF の Width は**窓の外側**で、
// WinForms の ClientSize と WinUI の ResizeClient は**表示領域**である。差は縁の
// 十数 px であり、揃える対象としてはこれで足りる (**縁のぶんを「不一致」として
// 直しに来ないこと**)。ここが縛るのは「3 つが同じ数字を書いていること」である。
using System.Globalization;
using System.Text.RegularExpressions;
using Xunit;

namespace UiaTrigger.Tests;

public sealed class PickerWindowDefaultSizeTests
{
    private static string Read(string relativePath)
    {
        string path = RepoPaths.Combine(relativePath.Split('/'));
        Assert.True(File.Exists(path), $"ファイルがありません: {path}");
        return File.ReadAllText(path);
    }

    /// <summary>ソースから 1 組の数字を取り出す。見つからなければその場で落とす。</summary>
    private static (int Width, int Height) Sizes(string relativePath, string pattern)
    {
        Match match = Regex.Match(Read(relativePath), pattern, RegexOptions.None, TimeSpan.FromSeconds(5));
        Assert.True(
            match.Success,
            $"{relativePath} に既定サイズの宣言が見つかりません (探した形: {pattern})。");
        return (int.Parse(match.Groups[1].Value, CultureInfo.InvariantCulture),
                int.Parse(match.Groups[2].Value, CultureInfo.InvariantCulture));
    }

    /// <summary>
    /// ピッカーの窓の既定サイズが 3 変種で一致すること。
    ///
    /// <para>
    /// WinUI 側が渡すのは <b>96 DPI 基準</b>の値で、実際の寸法は表示スケールを掛けて決まる。
    /// 掛けないと 175% の画面で他の 2 つの 57% の大きさになる (<c>WindowDefaults</c>)。
    /// </para>
    /// </summary>
    [Fact]
    public void ThePickerWindowOpensAtTheSameSizeInAllThreeVariants()
    {
        (int Width, int Height) wpf = Sizes(
            "src/UiaTrigger.Picker.Wpf/TriggerPickerWindow.xaml",
            @"Width=""(\d+)""\s+Height=""(\d+)""");
        (int Width, int Height) winForms = Sizes(
            "src/UiaTrigger.Picker.WinForms/TriggerPickerForm.cs",
            @"ClientSize = new Size\((\d+), (\d+)\)");
        (int Width, int Height) winUi = Sizes(
            "src/UiaTrigger.Picker.WinUI/TriggerPickerWindow.xaml.cs",
            @"DefaultWidth = (\d+);\s*private const int DefaultHeight = (\d+);");

        Assert.Equal(wpf, winForms);
        Assert.Equal(wpf, winUi);
    }

    /// <summary>トリガ一覧エディタの窓についても同じこと (docs/DESIGN.md §4)。</summary>
    [Fact]
    public void TheEditorWindowOpensAtTheSameSizeInAllThreeVariants()
    {
        (int Width, int Height) wpf = Sizes(
            "src/UiaTrigger.Picker.Wpf/TriggerListEditorWindow.xaml",
            @"Width=""(\d+)""\s+Height=""(\d+)""");
        (int Width, int Height) winForms = Sizes(
            "src/UiaTrigger.Picker.WinForms/TriggerListEditorForm.cs",
            @"ClientSize = new Size\((\d+), (\d+)\)");
        (int Width, int Height) winUi = Sizes(
            "src/UiaTrigger.Picker.WinUI/TriggerListEditorWindow.xaml.cs",
            @"DefaultWidth = (\d+);\s*private const int DefaultHeight = (\d+);");

        Assert.Equal(wpf, winForms);
        Assert.Equal(wpf, winUi);
    }

    /// <summary>
    /// サンプルホストの窓についても同じこと。
    ///
    /// <para>
    /// **ホストは配らないが、ここが揃っていないと最初に見えるものがばらつく。**
    /// 利用者が最初に触るのはこの 3 つである。
    /// </para>
    /// </summary>
    [Fact]
    public void TheSampleHostWindowOpensAtTheSameSizeInAllThreeVariants()
    {
        (int Width, int Height) wpf = Sizes(
            "src/UiaTrigger.App.Wpf/MainWindow.xaml",
            @"Width=""(\d+)""\s+Height=""(\d+)""");
        (int Width, int Height) winForms = Sizes(
            "src/UiaTrigger.App.WinForms/MainForm.cs",
            @"ClientSize = new Size\((\d+), (\d+)\)");
        (int Width, int Height) winUi = Sizes(
            "src/UiaTrigger.App.WinUI/MainWindow.xaml.cs",
            @"DefaultWidth = (\d+);\s*private const int DefaultHeight = (\d+);");

        Assert.Equal(wpf, winForms);
        Assert.Equal(wpf, winUi);
    }

    /// <summary>
    /// WinUI の 3 つの窓が既定サイズを**実際に適用**していること。
    ///
    /// <para>
    /// 上の 3 つは定数の値しか見ないので、<c>ApplyClientSize</c> の呼びを消しても緑のままである。
    /// **呼びが消えると窓は OS 既定 = 画面に比例する大きさに戻る**ので、定数と対で縛る。
    /// </para>
    /// </summary>
    [Theory]
    [InlineData("src/UiaTrigger.Picker.WinUI/TriggerPickerWindow.xaml.cs")]
    [InlineData("src/UiaTrigger.Picker.WinUI/TriggerListEditorWindow.xaml.cs")]
    [InlineData("src/UiaTrigger.App.WinUI/MainWindow.xaml.cs")]
    public void TheWinUiWindowsApplyTheirDefaultSize(string relativePath)
        => Assert.Contains(
            "WindowDefaults.ApplyClientSize(this, DefaultWidth, DefaultHeight)",
            Read(relativePath),
            StringComparison.Ordinal);

    /// <summary>
    /// WinUI 側が表示スケールを掛けていること。
    ///
    /// <para>
    /// <c>AppWindow.ResizeClient</c> が取るのは**物理ピクセル**である。WPF の
    /// <c>Width="1100"</c> は DIP なので、掛けずに渡すと 175% の画面で他の 2 変種の
    /// 57% の大きさになる — **例外も警告も出ず、ただ小さい。**
    /// </para>
    /// </summary>
    [Theory]
    [InlineData("src/UiaTrigger.Picker.WinUI/WindowDefaults.cs")]
    [InlineData("src/UiaTrigger.App.WinUI/WindowDefaults.cs")]
    public void TheWinUiDefaultSizeIsScaledByTheDisplayScale(string relativePath)
    {
        string code = Read(relativePath);
        Assert.Contains("GetDpiForWindow", code, StringComparison.Ordinal);
        Assert.Contains("Scale(widthAt96, dpi)", code, StringComparison.Ordinal);
    }
}
