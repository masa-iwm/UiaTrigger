// 窓の既定サイズ。
//
// **WinUI3 の Window には既定サイズを宣言する口が無い** (WPF の Width / Height、
// Windows Forms の ClientSize に当たるものが XAML に無い)。何もしないと OS が決めた
// 大きさ — 画面に比例する — で開くので、高解像度では窓が画面をほぼ埋める。
//
// AppWindow.ResizeClient が取るのは**物理ピクセル**なので DPI を掛ける。掛けないと
// 175% の画面で他の 2 つのサンプルホストの 57% の大きさになる。
//
// **Picker.WinUI にある同名のものと同じ内容である。**製品アセンブリ間の
// InternalsVisibleTo は持たない (docs/DESIGN.md §12) ので共有できず、
// 共有できたとしてもこのサンプルは**利用者が書けるものだけで書く**必要がある。
// 組み込む側が同じことをやりたくなる箇所なので、写せる形でここに在るほうがよい。
using Microsoft.UI;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Windows.Graphics;

namespace UiaTrigger.App.WinUI;

internal static partial class WindowDefaults
{
    private const int ReferenceDpi = 96;

    /// <summary>表示領域の既定サイズを 96 DPI 基準の値で与える。**窓を出す前に呼ぶこと。**</summary>
    public static void ApplyClientSize(Window window, int widthAt96, int heightAt96)
    {
        AppWindow appWindow = window.AppWindow;
        uint dpi = GetDpiForWindow(Win32Interop.GetWindowFromWindowId(appWindow.Id));
        if (dpi == 0)
        {
            dpi = ReferenceDpi;
        }
        appWindow.ResizeClient(new SizeInt32(Scale(widthAt96, dpi), Scale(heightAt96, dpi)));
    }

    private static int Scale(int valueAt96, uint dpi)
        => (int)(((valueAt96 * dpi) + (ReferenceDpi / 2)) / ReferenceDpi);

    // DPI は**窓を出す前**に要る。その時点では XamlRoot.RasterizationScale がまだ無い
    [System.Runtime.InteropServices.LibraryImport("user32.dll")]
    private static partial uint GetDpiForWindow(nint hwnd);
}
