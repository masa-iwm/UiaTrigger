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

    [LibraryImport("user32.dll", EntryPoint = "GetClassNameW", StringMarshalling = StringMarshalling.Utf16)]
    private static partial int GetClassNameNative(nint hWnd, Span<char> lpClassName, int nMaxCount);

    [LibraryImport("user32.dll", EntryPoint = "GetWindowTextW", StringMarshalling = StringMarshalling.Utf16)]
    private static partial int GetWindowTextNative(nint hWnd, Span<char> lpString, int nMaxCount);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool GetWindowRect(nint hWnd, out UiaRect lpRect);

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
        var list = (List<nint>)GCHandle.FromIntPtr(lParam).Target!;
        list.Add(hwnd);
        return 1; // continue
    }
}
