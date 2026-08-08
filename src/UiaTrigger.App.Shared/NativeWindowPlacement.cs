// サンプルホストが自分の窓を置くための、少数の Win32 API。
//
// **入力スタックには 1 バイトも入らない。**SetWindowPos は Z オーダーもフォーカスも
// 動かさない (SWP_NOZORDER | SWP_NOACTIVATE)。合成入力の政策 (docs/TESTING.md §3) には
// 掛からない。
//
// CsWin32 は入れない。ここに要るのは 5 つだけで、そのために生成器と NativeMethods.txt を
// 持ち込むのは釣り合わない (T4 の NativeWindows.cs が同じ判断をしている)。
//
// **コールバックはデリゲートではなく [UnmanagedCallersOnly] の関数ポインタで渡す。**
// App.WinUI は PublishAot=true であり、根に留めていないデリゲートは回収される —
// そうなるとフックは**落ちるのではなく黙って効かなくなる**。関数ポインタなら
// 回収されるものが無いので、その失敗形が原理的に起きない
// (UiaTrigger.Core の EnumWindowsCallback が同じ理由で同じ形を採っている)。
using System.Runtime.InteropServices;

namespace UiaTrigger.App.Shared;

internal static unsafe partial class NativeWindowPlacement
{
    /// <summary>ウィンドウが可視になった。</summary>
    internal const uint EVENT_OBJECT_SHOW = 0x8002;

    /// <summary>ウィンドウの位置か大きさが変わった。</summary>
    /// <remarks>
    /// **これが無いと足りない。**フレームワークは自前の配置を**あとから**当てることがあり、
    /// 出た瞬間に置くだけでは元へ戻される (T4 が実際にそれで落ちている)。
    /// </remarks>
    internal const uint EVENT_OBJECT_LOCATIONCHANGE = 0x800B;

    /// <summary>イベントの対象がウィンドウそのものであることを示す。</summary>
    /// <remarks>
    /// <c>EVENT_OBJECT_SHOW</c> は**子コントロールが出ても飛ぶ**。
    /// これで絞らないとコールバックが殺到する。
    /// </remarks>
    internal const int OBJID_WINDOW = 0;

    /// <summary>子ではなくその要素自身。</summary>
    internal const int CHILDID_SELF = 0;

    /// <summary>フックしたプロセスの外に出て呼ばれる (= こちらのスレッドで受ける)。</summary>
    internal const uint WINEVENT_OUTOFCONTEXT = 0x0000;

    private const uint GA_ROOT = 2;
    private const int GWL_EXSTYLE = -20;
    private const uint SWP_NOZORDER = 0x0004;
    private const uint SWP_NOACTIVATE = 0x0010;

    /// <summary>
    /// <c>WS_EX_TOOLWINDOW</c>。**オーバーレイを見分ける唯一の目印として使う。**
    /// </summary>
    /// <remarks>
    /// <para>
    /// ピッカーのオーバーレイ (枠と確定アイコン) は 2 つともこれを立てており、
    /// ホストが見せる窓 (メイン / ピッカー / エディタ) はどれも立てていない。
    /// **オーバーレイは動かしてはいけない** — あれは選択された要素の上に出るのが仕事で、
    /// 退かしたら検査対象そのものを壊す。
    /// </para>
    /// <para>
    /// **クラス名では絞らない。**T4 が <c>UiaTriggerOverlay</c> の綴りを写しているのは
    /// 意図で、製品側を改名したらテストが落ちるべきだからである。出荷するホストに
    /// 同じ綴りを置くとその向きが逆になり、改名が「テストが落ちる」ではなく
    /// 「**配置が黙って壊れる**」になる。
    /// </para>
    /// <para>
    /// <c>WS_CAPTION</c> の有無では絞らない。**置き損ねは目に見えない失敗**なので、
    /// 素通りする経路は 1 つでも少ないほうがよい。立てているのが実際に
    /// オーバーレイだけである以上、目印は 1 つで足りる。
    /// </para>
    /// </remarks>
    internal const nint WS_EX_TOOLWINDOW = 0x00000080;

    /// <summary>そのウィンドウがトップレベル (自分自身が根) か。</summary>
    internal static bool IsTopLevel(nint hwnd) => GetAncestor(hwnd, GA_ROOT) == hwnd;

    /// <summary>そのウィンドウがまだ存在するか (破棄済みの台帳を捨てるために使う)。</summary>
    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool IsWindow(nint hWnd);

    /// <summary>
    /// 置いてよい窓か。**可視で、最小化されていないトップレベル**だけが対象である。
    /// </summary>
    /// <remarks>
    /// <para>
    /// **可視で絞るのは実測に基づく。**絞らないと、WinUI が内部で持っている
    /// 不可視の小窓 (実測で <c>(0,0)-(60,60)</c> が 3 つ) まで掴んで動かしにいく。
    /// ハーネス側が <c>TopLevelWindowsOf</c> で <c>IsWindowVisible</c> を見ていたのと同じ絞りである。
    /// </para>
    /// <para>
    /// **最小化を外すのも実測である。**最小化された窓は <c>SetWindowPos</c> で動かず、
    /// <c>GetWindowRect</c> は <c>(-32000,-32000)</c> を返し続ける。
    /// つまり「目標に届いたか」で判定する限り**永久に届かない**ので、
    /// 置き直しの上限まで撃ち続けることになる (実測でそうなった)。
    /// 復元されれば <c>EVENT_OBJECT_LOCATIONCHANGE</c> がまた飛ぶので、取りこぼしはしない。
    /// </para>
    /// </remarks>
    internal static bool IsPlaceable(nint hwnd)
        => IsTopLevel(hwnd) && IsWindowVisible(hwnd) && !IsIconic(hwnd);

    /// <summary>オーバーレイの窓か (動かしてはいけないもの)。</summary>
    internal static bool IsOverlay(nint hwnd) => (GetWindowLongPtr(hwnd, GWL_EXSTYLE) & WS_EX_TOOLWINDOW) != 0;

    /// <summary>ウィンドウの外枠 (スクリーン座標・物理ピクセル)。取れなければ null。</summary>
    internal static (int Left, int Top, int Right, int Bottom)? RectOf(nint hwnd)
        => GetWindowRect(hwnd, out RECT r) ? (r.left, r.top, r.right, r.bottom) : null;

    /// <summary>ウィンドウを動かす。Z オーダーもフォーカスも動かさない。</summary>
    internal static bool MoveTo(nint hwnd, int left, int top, int width, int height)
        => SetWindowPos(hwnd, 0, left, top, width, height, SWP_NOZORDER | SWP_NOACTIVATE);

    /// <summary>自プロセスのウィンドウイベントを購読する。失敗なら 0。</summary>
    internal static nint HookOwnProcess(
        uint eventMin,
        uint eventMax,
        delegate* unmanaged[Stdcall]<nint, uint, nint, int, int, uint, uint, void> callback)
        => SetWinEventHook(
            eventMin, eventMax, 0, callback, (uint)Environment.ProcessId, 0, WINEVENT_OUTOFCONTEXT);

    [StructLayout(LayoutKind.Sequential)]
    private struct RECT
    {
        public int left;
        public int top;
        public int right;
        public int bottom;
    }

    [LibraryImport("user32.dll")]
    private static partial nint SetWinEventHook(
        uint eventMin,
        uint eventMax,
        nint hmodWinEventProc,
        delegate* unmanaged[Stdcall]<nint, uint, nint, int, int, uint, uint, void> pfnWinEventProc,
        uint idProcess,
        uint idThread,
        uint dwFlags);

    [LibraryImport("user32.dll")]
    private static partial nint GetAncestor(nint hwnd, uint gaFlags);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool IsWindowVisible(nint hWnd);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool IsIconic(nint hWnd);

    [LibraryImport("user32.dll", EntryPoint = "GetWindowLongPtrW", SetLastError = true)]
    private static partial nint GetWindowLongPtr(nint hWnd, int nIndex);

    [LibraryImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool GetWindowRect(nint hWnd, out RECT lpRect);

    [LibraryImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool SetWindowPos(
        nint hWnd, nint hWndInsertAfter, int X, int Y, int cx, int cy, uint uFlags);
}
