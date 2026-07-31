// カーソル位置。GetCursorPos の P/Invoke はここに集約してあり、
// View (Picker.WinUI ほか) は CsWin32 を参照しない。
namespace UiaTrigger.Picker;

/// <summary>Reads the cursor position from Win32. The implementation views normally use.</summary>
public sealed class Win32CursorSource : ICursorSource
{
    /// <summary>Reads the cursor position in physical screen coordinates.</summary>
    /// <param name="x">Receives the horizontal position.</param>
    /// <param name="y">Receives the vertical position.</param>
    /// <returns>False when the position could not be read; <paramref name="x"/> and
    /// <paramref name="y"/> are then meaningless.</returns>
    /// <remarks>
    /// Physical, not scaled — the same coordinates UI Automation reports. A host that is not
    /// per-monitor DPI aware gets a position it cannot line up with them (docs/DESIGN.md A19).
    /// </remarks>
    public bool TryGetPosition(out int x, out int y)
    {
        if (Windows.Win32.PInvoke.GetCursorPos(out System.Drawing.Point point))
        {
            x = point.X;
            y = point.Y;
            return true;
        }
        x = 0;
        y = 0;
        return false;
    }
}
