// 要素が乗っているモニターの DPI (docs/DESIGN.md §9)。
//
// Win32CursorSource と同じ形にしてある: 実装は 1 つ、テストは継ぎ目を差し替える。
// ここを OverlayGeometry の中に埋め込まないことが T1 の決定性の条件である。
using UiaTrigger.Models;
using Windows.Win32;
using Windows.Win32.Graphics.Gdi;
using Windows.Win32.UI.HiDpi;

namespace UiaTrigger.Picker;

/// <summary>Reads the DPI of the monitor an element sits on. The implementation views normally use.</summary>
internal sealed class Win32DpiSource : IDpiSource
{
    /// <summary>矩形の左上が乗っているモニターの DPI。読めなければ 96。</summary>
    /// <remarks>
    /// <para>
    /// <c>MONITOR_DEFAULTTONEAREST</c> なので、矩形が画面の外に出ていても
    /// 一番近いモニターの DPI が返る (要素が画面外へ動いた直後でも 0 にはならない)。
    /// </para>
    /// <para>
    /// <c>GetDpiForMonitor</c> は Windows 8.1 以降である。それ以前は 96 に落ちる —
    /// 従来と同じ見た目になるだけで、壊れはしない。
    /// </para>
    /// </remarks>
    public int DpiFor(ElementRect rect)
    {
        if (!OperatingSystem.IsWindowsVersionAtLeast(8, 1))
        {
            return OverlayGeometry.ReferenceDpi;
        }
        var point = new System.Drawing.Point(rect.Left, rect.Top);
        var monitor = PInvoke.MonitorFromPoint(point, MONITOR_FROM_FLAGS.MONITOR_DEFAULTTONEAREST);
        if (monitor != nint.Zero &&
            PInvoke.GetDpiForMonitor(monitor, MONITOR_DPI_TYPE.MDT_EFFECTIVE_DPI, out uint dpiX, out _).Succeeded &&
            dpiX > 0)
        {
            return (int)dpiX;
        }
        return OverlayGeometry.ReferenceDpi;
    }
}
