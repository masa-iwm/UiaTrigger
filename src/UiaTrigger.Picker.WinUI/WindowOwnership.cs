// 窓の所有関係 (docs/DESIGN.md §4)。
//
// **WinUI3 の Window には所有者を付ける口が無い。**WPF は Owner、Windows Forms は
// Show(owner) で持つが、ここだけは何もしないと子ピッカーとエディタが対等な窓になる。
// 対等だと重なりが活性化の順序で決まり、ダブルクリックの入力残りがエディタを前面へ
// 戻した瞬間に**子ピッカーが後ろに隠れる** (実測。ディスパッチャで開くのを遅らせるだけでは
// 環境によって競争に負ける)。Win32 の所有関係 (GWLP_HWNDPARENT) は重なりを構造で決める —
// 所有された窓は所有者より常に上に居る。
//
// 副作用も Win32 の規則のとおりである: 所有された窓はタスクバーに独立のボタンを出さず、
// 所有者と一緒に最小化される。エディタの子として開くピッカーには望んだ性質で、
// ホストが直接開くピッカー (App の [要素を選ぶ]) はこの経路を通らないので変わらない。
using Microsoft.UI;
using Microsoft.UI.Xaml;

namespace UiaTrigger.Picker.WinUI;

internal static partial class WindowOwnership
{
    /// <summary>GWLP_HWNDPARENT。窓の「親」スロットに置くと所有関係になる。</summary>
    private const int HwndParentIndex = -8;

    /// <summary><paramref name="window"/> を <paramref name="owner"/> の所有にする。</summary>
    /// <remarks>Activate の前に呼ぶこと。出したあとに付け替えると一瞬対等な重なりで描かれる。</remarks>
    public static void SetOwner(Window window, Window owner)
    {
        nint hwnd = Win32Interop.GetWindowFromWindowId(window.AppWindow.Id);
        nint ownerHwnd = Win32Interop.GetWindowFromWindowId(owner.AppWindow.Id);
        _ = SetWindowLongPtrW(hwnd, HwndParentIndex, ownerHwnd);
    }

    // WindowDefaults と同じ理由の手書き P/Invoke。SetWindowLongPtrW は 64-bit の user32 に
    // しか無いが、このアセンブリは WindowsAppSDK の制約で元から x64 専用である
    [System.Runtime.InteropServices.LibraryImport("user32.dll", SetLastError = true)]
    private static partial nint SetWindowLongPtrW(nint hwnd, int index, nint value);
}
