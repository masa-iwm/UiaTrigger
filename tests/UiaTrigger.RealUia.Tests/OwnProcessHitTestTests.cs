// 自プロセスの点では、UIA への問い合わせが**そもそも届かない**ことの回帰テスト。
//
// **戻り値では確かめられない。**自プロセス除外を呼び出しの後で行っていた頃も null は返っていた。
// 違うのは「自分のプロバイダーへ問い合わせが届くかどうか」だけである。そこでテストプロセス
// 自身のウィンドウを 1 つ作り、`WM_GETOBJECT` の到着を数える。
//
// 動機となった実測 (WinUI3 のホストに組み込んだ状態): 呼び出しの**後**で捨てる形だと、
// ピッカーの自動選択が 1 秒静止で捕捉を試みるたびにホスト自身の UIA プロバイダーへ
// ヒットテストが届き、その約 1 秒後にホストの MainWindow が活性化されてピッカーの窓が
// 背面へ回る (EVENT_SYSTEM_FOREGROUND で確認)。同一プロセスなので前面化の制限を受けず
// 必ず通る。枠は動かないため「何も捕捉していない」ようにしか見えず、原因が見えない。
using System.Runtime.InteropServices;
using Xunit;

namespace UiaTrigger.RealUia.Tests;

public sealed class OwnProcessHitTestTests
{
    /// <summary>問い合わせの到着を待つ余裕。UIA は呼び出しの中で訊くが、計測は別スレッドである。</summary>
    private static readonly TimeSpan SettleTime = TimeSpan.FromMilliseconds(400);

    /// <summary>
    /// 自プロセスのウィンドウの上では、UIA へ問い合わせずに打ち切ること (docs/DESIGN.md A24)。
    /// </summary>
    [Fact]
    public async Task ElementFromPoint_OverAWindowOfTheCallingProcess_NeverReachesItsProvider()
    {
        TestTargetProcess.RequireCoordinateSafeProcess();
        using var window = new OwnProcessProbeWindow();
        (int x, int y) = window.ClientCenter();

        await using var session = new UiaSession(new UiaSessionOptions { SkipOwnProcessElements = true });

        // **先に「静かであること」を固定する。**別のアクセシビリティ製品が動いている環境では
        // 背景の問い合わせが数に混ざりうる。混ざったままだと、下の 0 が守っている対象と
        // 違うものを見て赤くなる — その場合はここで落ちるので原因が分かる
        window.ResetQueryCount();
        await Task.Delay(SettleTime, TestContext.Current.CancellationToken);
        Assert.Equal(0, window.ProviderQueryCount);

        using UiaElement? element = await session.ElementFromPointAsync(x, y);
        await Task.Delay(SettleTime, TestContext.Current.CancellationToken);

        Assert.Null(element);
        Assert.Equal(0, window.ProviderQueryCount);
    }

    /// <summary>
    /// 上の検査に検出力が在ることの担保 (ポジティブコントロール)。
    /// </summary>
    /// <remarks>
    /// 除外を切れば、**同じ点・同じ数え方**で問い合わせが届く。届かないなら数え方のほうが
    /// 壊れており、上の 0 は何も証明しない — 「無いこと」の主張が空振りで緑になる形である。
    /// </remarks>
    [Fact]
    public async Task TheCounterSeesTheQueryWhenTheSkipIsOff()
    {
        TestTargetProcess.RequireCoordinateSafeProcess();
        using var window = new OwnProcessProbeWindow();
        (int x, int y) = window.ClientCenter();

        await using var session = new UiaSession(new UiaSessionOptions { SkipOwnProcessElements = false });

        window.ResetQueryCount();
        await Task.Delay(SettleTime, TestContext.Current.CancellationToken);

        using UiaElement? element = await session.ElementFromPointAsync(x, y);
        await Task.Delay(SettleTime, TestContext.Current.CancellationToken);

        Assert.NotNull(element);
        Assert.True(
            window.ProviderQueryCount > 0,
            "自プロセス除外を切っても WM_GETOBJECT が届いていません。数え方が壊れているので、" +
            "対の検査 (問い合わせが届かないこと) は何も守らずに緑になります。");
    }
}

/// <summary>
/// テストプロセス自身のトップレベルウィンドウ。<c>WM_GETOBJECT</c> の到着を数える。
/// </summary>
/// <remarks>
/// 専用スレッドでメッセージループを回す。xunit のスレッドはループを持たないため、
/// ここで回さないとウィンドウプロシージャが呼ばれず、**数が常に 0 になる**
/// (対の検査が守っているのはまさにその壊れ方である)。
/// </remarks>
internal sealed class OwnProcessProbeWindow : IDisposable
{
    private const uint WmGetObject = 0x003D;
    private const uint WsOverlappedWindow = 0x00CF0000;
    private const uint WsExTopmost = 0x0008;
    private const int SwShowNoActivate = 4;
    private const uint PmRemove = 0x0001;

    private readonly WindowProc _windowProc; // GC に回収させない (ネイティブ側が保持する)
    private readonly Thread _thread;
    private readonly ManualResetEventSlim _ready = new();
    private readonly string _className = "UiaTriggerOwnProcessProbe" + Guid.NewGuid().ToString("N");

    private nint _hwnd;
    private int _queryCount;
    private volatile bool _stop;

    public OwnProcessProbeWindow()
    {
        _windowProc = WindowProcedure;
        _thread = new Thread(Run) { IsBackground = true, Name = "UiaTrigger.OwnProcessProbe" };
        _thread.SetApartmentState(ApartmentState.STA);
        _thread.Start();
        Assert.True(_ready.Wait(TimeSpan.FromSeconds(10)), "プローブ用のウィンドウを作れませんでした。");
        Assert.NotEqual(0, _hwnd);
    }

    /// <summary>これまでに届いた <c>WM_GETOBJECT</c> の数。</summary>
    public int ProviderQueryCount => Volatile.Read(ref _queryCount);

    public void ResetQueryCount() => Volatile.Write(ref _queryCount, 0);

    /// <summary>クライアント領域の中心 (物理スクリーン座標)。</summary>
    public (int X, int Y) ClientCenter()
    {
        Assert.True(GetClientRect(_hwnd, out NativeRect client), "GetClientRect に失敗しました。");
        var center = new NativePoint { X = (client.Right - client.Left) / 2, Y = (client.Bottom - client.Top) / 2 };
        Assert.True(ClientToScreen(_hwnd, ref center), "ClientToScreen に失敗しました。");
        return (center.X, center.Y);
    }

    public void Dispose()
    {
        _stop = true;
        _thread.Join(TimeSpan.FromSeconds(5));
        _ready.Dispose();
    }

    private void Run()
    {
        nint instance = GetModuleHandleW(null);
        var windowClass = new WindowClassEx
        {
            cbSize = (uint)Marshal.SizeOf<WindowClassEx>(),
            lpfnWndProc = Marshal.GetFunctionPointerForDelegate(_windowProc),
            hInstance = instance,
            lpszClassName = _className,
        };
        if (RegisterClassExW(ref windowClass) != 0)
        {
            // 画面の左上に小さく出す。最前面にしておかないと、直列実行でも
            // 前のテストが残した窓に覆われて ElementFromPoint が別の要素を返しうる
            _hwnd = CreateWindowExW(
                WsExTopmost, _className, "UiaTrigger own-process probe", WsOverlappedWindow,
                40, 40, 320, 240, 0, 0, instance, 0);
            if (_hwnd != 0)
            {
                ShowWindow(_hwnd, SwShowNoActivate);
            }
        }

        _ready.Set();

        while (!_stop)
        {
            while (PeekMessageW(out NativeMessage message, 0, 0, 0, PmRemove))
            {
                TranslateMessage(in message);
                DispatchMessageW(in message);
            }
            Thread.Sleep(10);
        }

        if (_hwnd != 0)
        {
            DestroyWindow(_hwnd);
            _hwnd = 0;
        }
        UnregisterClassW(_className, GetModuleHandleW(null));
    }

    private nint WindowProcedure(nint hwnd, uint message, nint wParam, nint lParam)
    {
        if (message == WmGetObject)
        {
            Interlocked.Increment(ref _queryCount);
        }
        return DefWindowProcW(hwnd, message, wParam, lParam);
    }

    private delegate nint WindowProc(nint hwnd, uint message, nint wParam, nint lParam);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct WindowClassEx
    {
        public uint cbSize;
        public uint style;
        public nint lpfnWndProc;
        public int cbClsExtra;
        public int cbWndExtra;
        public nint hInstance;
        public nint hIcon;
        public nint hCursor;
        public nint hbrBackground;
        [MarshalAs(UnmanagedType.LPWStr)] public string? lpszMenuName;
        [MarshalAs(UnmanagedType.LPWStr)] public string? lpszClassName;
        public nint hIconSm;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NativePoint
    {
        public int X;
        public int Y;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeRect
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeMessage
    {
        public nint Hwnd;
        public uint Message;
        public nint WParam;
        public nint LParam;
        public uint Time;
        public NativePoint Point;
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern nint GetModuleHandleW(string? moduleName);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern ushort RegisterClassExW(ref WindowClassEx windowClass);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool UnregisterClassW(string className, nint instance);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern nint CreateWindowExW(
        uint exStyle, string className, string? windowName, uint style,
        int x, int y, int width, int height, nint parent, nint menu, nint instance, nint param);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern nint DefWindowProcW(nint hwnd, uint message, nint wParam, nint lParam);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DestroyWindow(nint hwnd);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool ShowWindow(nint hwnd, int command);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool PeekMessageW(
        out NativeMessage message, nint hwnd, uint filterMin, uint filterMax, uint removeFlag);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool TranslateMessage(in NativeMessage message);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern nint DispatchMessageW(in NativeMessage message);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetClientRect(nint hwnd, out NativeRect rect);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool ClientToScreen(nint hwnd, ref NativePoint point);
}
