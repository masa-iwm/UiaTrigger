// T4 で使う数少ない P/Invoke (docs/DESIGN.md §10 / docs/TESTING.md §1)。
//
// ほとんどは読み取り専用である — ウィンドウの位置・Z オーダー・「あるピクセルの上に
// 誰が居るか」を外から観測するだけ。<b>例外は MoveTo だけ</b>で、これはホストの窓を
// pick 点から退かす (docs/TESTING.md §1)。
//
// <b>どれも擬似入力の禁止 (docs/TESTING.md §3) には掛からない。</b>入力スタックには
// 1 バイトも入らず、MoveTo もヒットテストとフォーカスを動かさない
// (SWP_NOZORDER | SWP_NOACTIVATE)。政策が禁じているのは「入力がどこへ届くかを
// 擬似入力で検証すること」であって、ハーネスが自分の舞台を整えることではない。
//
// CsWin32 は入れていない。T4 に P/Invoke はここだけで、そのために生成器と
// NativeMethods.txt を持ち込むのは釣り合わない。
using System.Runtime.InteropServices;

namespace UiaTrigger.Picker.UiTests;

internal static partial class NativeWindows
{
    /// <summary>
    /// そのスクリーン座標のピクセルの上に居るウィンドウ。
    /// </summary>
    /// <remarks>
    /// <para>
    /// レイヤードウィンドウのヒットテストは<b>ピクセルごとのアルファを尊重する</b>ので、
    /// 枠の透明な内側では Z オーダーに関係なく下のウィンドウが返る。
    /// 見るべき点は<b>確定アイコンの内側</b> — そこだけが不透明である。
    /// </para>
    /// <para>
    /// <b>これでクリックスルーは検査できない。</b>この関数は <c>WM_NCHITTEST</c> を送らないので、
    /// <c>HTTRANSPARENT</c> を返す枠の上でもオーバーレイ自身を返す。
    /// あれは実マウス入力の経路の話であり、手動確認のまま残る
    /// (docs/MANUAL-CHECKS.md §3)。
    /// </para>
    /// <para>
    /// <b>較正して確かめてある</b> (docs/DESIGN.md §10):
    /// <c>OverlayController</c> の <c>WM_NCHITTEST</c> が<b>全点で</b> <c>HTTRANSPARENT</c> を
    /// 返すようにしても、確定アイコンの点はオーバーレイを返し続けた。
    /// <b>尊重しないことが実測で確定している</b> — この関数でクリックスルーを論じないこと。
    /// </para>
    /// </remarks>
    public static nint WindowAt(int screenX, int screenY)
        => WindowFromPoint(new POINT { x = screenX, y = screenY });

    /// <summary>ウィンドウの外枠 (スクリーン座標)。取れなければ null。</summary>
    public static (int Left, int Top, int Right, int Bottom)? RectOf(nint hwnd)
        => GetWindowRect(hwnd, out RECT r) ? (r.left, r.top, r.right, r.bottom) : null;

    /// <summary>
    /// いま前面に居るウィンドウ。
    /// </summary>
    /// <remarks>
    /// <b>「クリックがそのウィンドウに届いたか」の観測点として使える</b> (docs/DESIGN.md §10)。
    /// 前面でないウィンドウを押したときの 1 発目は<b>前面化に食われる</b>ことがあり、
    /// そのときボタンの <c>Click</c> は上がらない。だが<b>前面化が起きたということは、
    /// クリックがそのウィンドウまで届いた</b>ということである。
    /// オーバーレイは <c>WS_EX_NOACTIVATE</c> なので、それ自身を押しても前面は変わらない —
    /// つまり前面が変わったなら、クリックはオーバーレイを通り越している。
    /// </remarks>
    public static nint ForegroundWindow() => GetForegroundWindow();

    /// <summary>そのウィンドウを持つプロセス ID。</summary>
    public static uint ProcessOf(nint hwnd)
    {
        _ = GetWindowThreadProcessId(hwnd, out uint pid);
        return pid;
    }

    /// <summary>その点が乗っているモニターの DPI (docs/DESIGN.md §9)。</summary>
    /// <remarks>
    /// <b>96 に決め打ってはいけない。</b>製品は要素が乗っているモニターの DPI で
    /// 枠とアイコンの寸法を決めるので、期待側が 96 固定だと
    /// <b>高 DPI の機械でだけ T4 / T5 が落ちる</b> — 製品は正しいのにテストが赤くなる。
    /// 製品と同じ引き方 (<c>MonitorFromPoint</c> + <c>GetDpiForMonitor</c>) をする。
    /// </remarks>
    public static int DpiAt(int screenX, int screenY)
    {
        nint monitor = MonitorFromPoint(new POINT { x = screenX, y = screenY }, MONITOR_DEFAULTTONEAREST);
        if (monitor != nint.Zero && GetDpiForMonitor(monitor, MDT_EFFECTIVE_DPI, out uint dpiX, out _) == 0 && dpiX > 0)
        {
            return (int)dpiX;
        }
        return OverlayGeometry.ReferenceDpi;
    }

    /// <summary>失敗時の診断用。「アイコンの上に居たのは誰か」を人が読める形にする。</summary>
    public static string Describe(nint hwnd)
    {
        if (hwnd == 0)
        {
            return "(ウィンドウなし)";
        }
        _ = GetWindowThreadProcessId(hwnd, out uint pid);
        Span<char> buffer = stackalloc char[256];
        int length = GetClassName(hwnd, buffer, buffer.Length);
        string cls = length > 0 ? new string(buffer[..length]) : "(クラス名不明)";
        return FormattableString.Invariant($"hwnd=0x{hwnd:X} pid={pid} class='{cls}'");
    }

    /// <summary>そのプロセスのトップレベルウィンドウを Z 順に返す (UIA を通さない)。</summary>
    /// <remarks>
    /// UIA の <c>RootElement.FindAll</c> と同じものを見るが、<b>桁違いに速い</b>。
    /// 窓が出た瞬間を捕まえたいときは往復の要らないこちらを使う。
    /// </remarks>
    public static List<nint> TopLevelWindowsOf(int processId)
    {
        var found = new List<nint>();
        for (nint hwnd = GetTopWindow(0); hwnd != 0; hwnd = GetWindow(hwnd, GW_HWNDNEXT))
        {
            _ = GetWindowThreadProcessId(hwnd, out uint pid);
            if (pid == (uint)processId && IsWindowVisible(hwnd))
            {
                found.Add(hwnd);
            }
        }
        return found;
    }

    /// <summary>
    /// トップレベルの Z オーダーでの位置 (0 が最前面)。見つからなければ -1。
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>「どちらが上か」をヒットテストを通さずに言うための道具である</b> (docs/DESIGN.md §10)。
    /// <see cref="WindowAt"/> でも上下は分かるが、あれは<b>ヒットテストの結果</b>なので、
    /// <c>WS_EX_TRANSPARENT</c> の窓や透過ピクセルの上では「そこに居ない」ことになる。
    /// クリックスルーを測るとき、まさにその窓が上に居ることを先に確かめたい —
    /// つまり<b>ヒットテストに依らない観測点が要る</b>。
    /// </para>
    /// <para>
    /// 可視かどうかで絞らない。相対的な順位だけが問題で、間に見えない窓が挟まっても
    /// 前後関係は変わらないためである。
    /// </para>
    /// </remarks>
    public static int ZOrderIndexOf(nint hwnd)
    {
        int index = 0;
        for (nint w = GetTopWindow(0); w != 0; w = GetWindow(w, GW_HWNDNEXT), index++)
        {
            if (w == hwnd)
            {
                return index;
            }
        }
        return -1;
    }

    /// <summary>ウィンドウのクラス名。取れなければ空文字。</summary>
    public static string ClassOf(nint hwnd)
    {
        Span<char> buffer = stackalloc char[256];
        int length = GetClassName(hwnd, buffer, buffer.Length);
        return length > 0 ? new string(buffer[..length]) : string.Empty;
    }

    /// <summary>
    /// ウィンドウクラスに登録されているカーソル (<c>GCLP_HCURSOR</c>)。無ければ 0。
    /// </summary>
    /// <remarks>
    /// <c>WNDCLASSEXW.hCursor</c> が <c>default</c> のままだと、そのクラスのウィンドウの上では
    /// カーソルが<b>親の状態を引きずる</b> (実際に砂時計になった)。
    /// <c>GetClassLongPtr</c> はマクロで、64bit では <c>GetClassLongPtrW</c> が実体である。
    /// </remarks>
    public static nint ClassCursorOf(nint hwnd) => GetClassLongPtr(hwnd, GCLP_HCURSOR);

    /// <summary>ウィンドウの拡張スタイル (<c>GWL_EXSTYLE</c>)。</summary>
    public static nint ExStyleOf(nint hwnd) => GetWindowLongPtr(hwnd, GWL_EXSTYLE);

    /// <summary>
    /// <c>WS_EX_TRANSPARENT</c>。付いた窓は<b>窓ごと</b>ヒットテストから外れる。
    /// </summary>
    /// <remarks>
    /// 定数を製品と共有しないのは <c>OverlayClassName</c> と同じ理由である (docs/TESTING.md §2) —
    /// 共有すると「両方いっしょに動いて何も守らないテスト」になる。
    /// </remarks>
    public const nint WS_EX_TRANSPARENT = 0x00000020;

    /// <summary>ウィンドウを動かす。Z オーダーもフォーカスも動かさない。</summary>
    public static bool MoveTo(nint hwnd, int left, int top, int width, int height)
        => SetWindowPos(hwnd, 0, left, top, width, height, SWP_NOZORDER | SWP_NOACTIVATE);

    private const uint GW_HWNDNEXT = 2;
    private const int GWL_EXSTYLE = -20;
    private const int GCLP_HCURSOR = -12;
    private const uint SWP_NOZORDER = 0x0004;
    private const uint SWP_NOACTIVATE = 0x0010;

    [StructLayout(LayoutKind.Sequential)]
    private struct RECT
    {
        public int left;
        public int top;
        public int right;
        public int bottom;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct POINT
    {
        public int x;
        public int y;
    }

    [LibraryImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool GetWindowRect(nint hWnd, out RECT lpRect);

    [LibraryImport("user32.dll")]
    private static partial nint WindowFromPoint(POINT point);

    [LibraryImport("user32.dll")]
    private static partial nint GetForegroundWindow();

    private const uint MONITOR_DEFAULTTONEAREST = 2;
    private const int MDT_EFFECTIVE_DPI = 0;

    [LibraryImport("user32.dll")]
    private static partial nint MonitorFromPoint(POINT point, uint dwFlags);

    /// <summary>成功で 0 (S_OK) を返す。</summary>
    [LibraryImport("shcore.dll")]
    private static partial int GetDpiForMonitor(nint hmonitor, int dpiType, out uint dpiX, out uint dpiY);

    [LibraryImport("user32.dll", EntryPoint = "GetClassNameW", StringMarshalling = StringMarshalling.Utf16)]
    private static partial int GetClassName(nint hWnd, Span<char> lpClassName, int nMaxCount);

    [LibraryImport("user32.dll")]
    private static partial uint GetWindowThreadProcessId(nint hWnd, out uint lpdwProcessId);

    [LibraryImport("user32.dll")]
    private static partial nint GetTopWindow(nint hWnd);

    [LibraryImport("user32.dll")]
    private static partial nint GetWindow(nint hWnd, uint uCmd);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool IsWindowVisible(nint hWnd);

    [LibraryImport("user32.dll", EntryPoint = "GetClassLongPtrW")]
    private static partial nint GetClassLongPtr(nint hWnd, int nIndex);

    [LibraryImport("user32.dll", EntryPoint = "GetWindowLongPtrW", SetLastError = true)]
    private static partial nint GetWindowLongPtr(nint hWnd, int nIndex);

    [LibraryImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool SetWindowPos(
        nint hWnd, nint hWndInsertAfter, int X, int Y, int cx, int cy, uint uFlags);
}
