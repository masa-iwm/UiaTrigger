// 窓の既定サイズ (docs/DESIGN.md §12)。
//
// **WinUI3 の Window には既定サイズを宣言する口が無い。**WPF は XAML の Width / Height、
// Windows Forms は ClientSize で持つが、ここだけは何もしないと OS が決めた大きさで開く。
// その大きさは画面に比例するので、3840x2160 では窓が画面をほぼ埋め、同じピッカーが
// 変種によってまったく違う見た目になる。
//
// AppWindow.ResizeClient が取るのは**物理ピクセル**である。WPF の Width="1100" は DIP で、
// 175% では 1925 物理px に相当する。ここで DPI を掛けないと、同じ 1100 という数字が
// 他の 2 変種の 57% の大きさになる。
using Microsoft.UI;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Windows.Graphics;

namespace UiaTrigger.Picker.WinUI;

internal static partial class WindowDefaults
{
    /// <summary>寸法を書くときの基準 DPI。オーバーレイの幾何と同じ基準である。</summary>
    private const int ReferenceDpi = 96;

    /// <summary>
    /// 表示領域の既定サイズを 96 DPI 基準の値で与える。**窓を出す前に呼ぶこと。**
    /// </summary>
    /// <remarks>
    /// 出したあとで呼ぶと、OS 既定の大きさで一度描かれてから縮む。T4 のように窓を
    /// 外から動かすものが居ると、退かしたあとの寸法変更として現れて割り付けを壊す
    /// (docs/TESTING.md §1)。
    /// </remarks>
    public static void ApplyClientSize(Window window, int widthAt96, int heightAt96)
    {
        AppWindow appWindow = window.AppWindow;
        uint dpi = GetDpiForWindow(Win32Interop.GetWindowFromWindowId(appWindow.Id));
        if (dpi == 0)
        {
            dpi = ReferenceDpi; // 窓がもう無い。既定の縮尺で置いて先へ進む
        }
        appWindow.ResizeClient(new SizeInt32(Scale(widthAt96, dpi), Scale(heightAt96, dpi)));
    }

    private static int Scale(int valueAt96, uint dpi)
        => (int)(((valueAt96 * dpi) + (ReferenceDpi / 2)) / ReferenceDpi);

    // ここに P/Invoke が 1 つだけ在る理由: DPI は**窓を出す前**に要り、その時点では
    // XamlRoot.RasterizationScale がまだ無い (中身が読み込まれていない)。
    // CsWin32 を入れるほどの量ではないので手書きにしてある。
    [System.Runtime.InteropServices.LibraryImport("user32.dll")]
    private static partial uint GetDpiForWindow(nint hwnd);
}
