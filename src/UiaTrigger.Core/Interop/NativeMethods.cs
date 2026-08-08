// Core が必要とする少数の Win32 API。
// この assembly は DisableRuntimeMarshalling のため、SetLastError=true の DllImport
// (CsWin32 生成物) は使えない。LibraryImport で手書きする (GetLastError には依存しない)。
using System.Drawing;
using System.Runtime.InteropServices;

namespace UiaTrigger.Interop;

internal static partial class NativeMethods
{
    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool GetCursorPos(out Point lpPoint);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static unsafe partial bool EnumWindows(delegate* unmanaged[Stdcall]<nint, nint, int> lpEnumFunc, nint lParam);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool IsWindowVisible(nint hWnd);

    /// <summary>
    /// ウィンドウがまだ存在するか。
    /// 破棄されたウィンドウの UIA 要素は属性を答え続けることがあるため、
    /// 「ウィンドウが生きているか」だけは OS に直接訊く (docs/DESIGN.md A21)。
    /// </summary>
    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool IsWindow(nint hWnd);

    [LibraryImport("user32.dll")]
    internal static partial uint GetWindowThreadProcessId(nint hWnd, out uint lpdwProcessId);

    /// <summary>
    /// 点の下のウィンドウ。自プロセスの点を UIA へ問い合わせる**前**に弾くために使う。
    /// </summary>
    /// <remarks>
    /// 返るのはトップレベルとは限らない**子ウィンドウ**である (WinUI3 なら
    /// <c>Microsoft.UI.Content.DesktopChildSiteBridge</c>)。呼び出し側が見るのは
    /// プロセス ID だけであり、**ハンドルの比較に変えてはいけない**。
    /// <c>WS_EX_TRANSPARENT</c> のウィンドウは飛ばされる — オーバーレイの枠がまさにそれで、
    /// 枠越しの点では下のアプリが返るのが正しい。
    /// </remarks>
    [LibraryImport("user32.dll")]
    internal static partial nint WindowFromPoint(Point point);

    [LibraryImport("user32.dll", EntryPoint = "GetClassNameW", StringMarshalling = StringMarshalling.Utf16)]
    private static partial int GetClassNameNative(nint hWnd, Span<char> lpClassName, int nMaxCount);

    [LibraryImport("user32.dll", EntryPoint = "GetWindowTextW", StringMarshalling = StringMarshalling.Utf16)]
    private static partial int GetWindowTextNative(nint hWnd, Span<char> lpString, int nMaxCount);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool GetWindowRect(nint hWnd, out UiaRect lpRect);

    [LibraryImport("dwmapi.dll")]
    private static partial int DwmGetWindowAttribute(nint hwnd, uint dwAttribute, out uint pvAttribute, uint cbAttribute);

    private const uint DWMWA_CLOAKED = 14;

    /// <summary>
    /// DWM に cloak されたウィンドウか (別仮想デスクトップ・休止 UWP など)。
    /// </summary>
    /// <remarks>
    /// cloaked のウィンドウは <see cref="IsWindowVisible"/> が真のまま画面に存在せず、
    /// 矩形も点を含み続ける。可視判定とセットで使わないと、見えない窓が
    /// ヒットテストの答えになる。属性が読めない環境 (DWM 無効) は「cloak されていない」に倒す。
    /// </remarks>
    internal static bool IsWindowCloaked(nint hwnd) =>
        DwmGetWindowAttribute(hwnd, DWMWA_CLOAKED, out uint cloaked, sizeof(uint)) == 0 && cloaked != 0;

    [LibraryImport("kernel32.dll")]
    internal static partial nint OpenProcess(uint dwDesiredAccess, [MarshalAs(UnmanagedType.Bool)] bool bInheritHandle, uint dwProcessId);

    [LibraryImport("kernel32.dll", EntryPoint = "QueryFullProcessImageNameW", StringMarshalling = StringMarshalling.Utf16)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool QueryFullProcessImageNameNative(nint hProcess, uint dwFlags, Span<char> lpExeName, ref uint lpdwSize);

    [LibraryImport("kernel32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool CloseHandle(nint hObject);

    internal const uint PROCESS_QUERY_LIMITED_INFORMATION = 0x1000;

    internal static string GetClassName(nint hwnd)
    {
        Span<char> buffer = stackalloc char[256];
        int len = GetClassNameNative(hwnd, buffer, buffer.Length);
        return len > 0 ? new string(buffer[..len]) : string.Empty;
    }

    internal static string GetWindowText(nint hwnd)
    {
        Span<char> buffer = stackalloc char[512];
        int len = GetWindowTextNative(hwnd, buffer, buffer.Length);
        return len > 0 ? new string(buffer[..len]) : string.Empty;
    }

    /// <summary>プロセス ID から実行ファイルのフルパスを取得する。取得できなければ null。</summary>
    internal static string? GetProcessImagePath(uint processId)
    {
        nint handle = OpenProcess(PROCESS_QUERY_LIMITED_INFORMATION, false, processId);
        if (handle == 0)
        {
            return null;
        }
        try
        {
            Span<char> buffer = stackalloc char[1024];
            uint size = (uint)buffer.Length;
            if (!QueryFullProcessImageNameNative(handle, 0, buffer, ref size))
            {
                return null;
            }
            return new string(buffer[..(int)size]);
        }
        finally
        {
            CloseHandle(handle);
        }
    }

    /// <summary>トップレベルウィンドウを Z オーダー順 (最前面が先頭) に列挙する。</summary>
    internal static unsafe List<nint> EnumTopLevelWindows()
    {
        var list = new List<nint>();
        var gch = GCHandle.Alloc(list);
        try
        {
            EnumWindows(&EnumWindowsCallback, GCHandle.ToIntPtr(gch));
        }
        finally
        {
            gch.Free();
        }
        return list;
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(System.Runtime.CompilerServices.CallConvStdcall)])]
    private static int EnumWindowsCallback(nint hwnd, nint lParam)
    {
        // コールバックから例外を投げない。アンマネージドの呼び出し元へ抜けると
        // プロセスがその場で落ちる (HostWindowPlacer.OnWindowEvent と同じ規律 / A28 と同族)
        try
        {
            var list = (List<nint>)GCHandle.FromIntPtr(lParam).Target!;
            list.Add(hwnd);
            return 1; // continue
        }
        catch
        {
            return 0; // 列挙を打ち切る (ここまでに集めた分は呼び出し元がそのまま使える)
        }
    }
}
