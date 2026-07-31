// 選択要素に枠を重ねるクリックスルーのレイヤードウィンドウ + 低レベルキーボードフック。
// 専用スレッドで**ウィンドウを 2 つ**作成しメッセージループを回す。
//
// **枠とアイコンは別のウィンドウである (docs/DESIGN.md §10)。**
//   ・枠     … WS_EX_TRANSPARENT。**窓ごと**ヒットテストから外れるので常にクリックスルー
//   ・アイコン … 全ピクセル不透明。窓の矩形がそのまま当たり判定で、WM_LBUTTONDOWN を受け取る
//
// **WM_NCHITTEST では直せない。**UpdateLayeredWindow のレイヤードウィンドウでは
// ヒットテストがピクセルごとのアルファで決まり、不透明なピクセルはそのウィンドウのものになる。
// そこで HTTRANSPARENT を返してもクリックは下へ渡されず**落ちる**
// (実測: 全点 HTTRANSPARENT の較正でも枠線のクリックは下のアプリに届かない — docs/DESIGN.md §10)。
// クリックスルーするのはアルファ 0 のピクセルだけである。
// **2 つの窓を 1 つに戻すとこの不具合が再発する。**
//
// スレッドは 1 本のままである — 2 つの窓は同じオーバーレイスレッド / 同じメッセージループに
// 載せる (docs/DESIGN.md §3 の「スレッドを増やさない」決定を守る)。
using System.Collections.Concurrent;
using System.Runtime.InteropServices;
using UiaTrigger.Models;
using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.Graphics.Gdi;
using Windows.Win32.UI.WindowsAndMessaging;

namespace UiaTrigger.Picker;

internal sealed class OverlayController : IOverlay
{
    private const uint MsgUpdate = PInvoke.WM_APP + 1;
    private const uint MsgHide = PInvoke.WM_APP + 2;
    private const uint MsgQuit = PInvoke.WM_APP + 3;
    private const int VK_LEFT = 0x25;
    private const int VK_RIGHT = 0x27;

    private const string WindowClassName = "UiaTriggerOverlay";

    /// <summary>アイコンの窓のクラス名。**枠と別にしてある**。</summary>
    /// <remarks>
    /// 外から数えるとき (T4 の <c>PickerHostProcess.Overlays()</c>) に、枠とアイコンを
    /// 区別できないと「1 枚のピッカーにオーバーレイ 1 つ」が言えなくなる。
    /// クラス名が唯一の見分ける手掛かりである (どちらもタイトルも AutomationId も持たない)。
    /// </remarks>
    private const string IconWindowClassName = "UiaTriggerOverlayIcon";

    /// <summary>2 つの窓に共通の拡張スタイル。</summary>
    private const WINDOW_EX_STYLE CommonExStyle =
        WINDOW_EX_STYLE.WS_EX_LAYERED | WINDOW_EX_STYLE.WS_EX_TOPMOST |
        WINDOW_EX_STYLE.WS_EX_TOOLWINDOW | WINDOW_EX_STYLE.WS_EX_NOACTIVATE;

    // static な wndproc / hookproc からインスタンスを引くための登録表。
    // static singleton (_instance) に戻すとピッカーを 2 つ開けなくなる
    // (docs/DESIGN.md A18)。
    //   ・WndProc は HWND から引く
    //   ・HookProc は「フックを仕掛けたスレッド上で呼ばれる」性質を使ってスレッド ID から引く
    //     (WH_KEYBOARD_LL のフックプロシージャには任意の状態を渡す口が無いため)
    private static readonly ConcurrentDictionary<HWND, OverlayController> _instancesByHwnd = new();
    private static readonly ConcurrentDictionary<int, OverlayController> _instancesByHookThread = new();

    // ウィンドウクラスの登録はプロセスに 1 回だけ (2 回目は ERROR_CLASS_ALREADY_EXISTS になる)
    private static readonly Lock _classLock = new();
    private static bool _classesRegistered;
    private static string? _classError;

    private readonly IDpiSource _dpi;
    private readonly Thread _thread;
    private readonly ManualResetEventSlim _ready = new();
    private HWND _hwnd;
    private HWND _iconHwnd;
    private UnhookWindowsHookExSafeHandle? _hook;
    private readonly Lock _lock = new();
    private ElementRect _pendingRect;
    private volatile bool _hookEnabled;
    // Dispose はどのスレッドからでも呼ばれうる。Interlocked で所有権を取り、
    // 二重解放も取りこぼしも起きないようにする (docs/DESIGN.md A18)
    private int _disposed;

    /// <summary>
    /// ウィンドウ作成に失敗した場合の診断情報 (成功時 null)。
    /// オーバーレイスレッドが書き、_ready の Set/Wait を跨いで読まれる。
    /// </summary>
    public string? CreationError { get; private set; }

    /// <summary>確定アイコンがクリックされた (オーバーレイスレッドから呼ばれる)。</summary>
    public event Action? ConfirmClicked;

    /// <summary>選択モード中に ←(false)/→(true) が押された (フックスレッドから呼ばれる)。</summary>
    public event Action<bool>? ArrowKeyPressed;

    public OverlayController(IDpiSource dpi)
    {
        _dpi = dpi;
        _thread = new Thread(ThreadMain) { IsBackground = true, Name = "UiaTrigger.Overlay" };
        _thread.SetApartmentState(ApartmentState.STA);
        _thread.Start();
        _ready.Wait();
    }

    /// <summary>枠の窓。アイコンは <see cref="IconHwnd"/> の別の窓である (docs/DESIGN.md §10)。</summary>
    public nint OverlayHwnd => _hwnd;

    /// <summary>確定アイコンの窓。**クリックを受け取るのはこちらだけである。**</summary>
    public nint IconHwnd => _iconHwnd;

    /// <summary>指定スクリーン矩形に枠+確定アイコンを表示する。</summary>
    public void ShowRect(ElementRect rect)
    {
        lock (_lock)
        {
            _pendingRect = rect;
        }
        PInvoke.PostMessage(_hwnd, MsgUpdate, 0, 0);
    }

    public void Hide() => PInvoke.PostMessage(_hwnd, MsgHide, 0, 0);

    /// <summary>←/→ キーの捕捉 (選択モード) を切り替える。</summary>
    public void SetHookEnabled(bool enabled) => _hookEnabled = enabled;

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }
        PInvoke.PostMessage(_hwnd, MsgQuit, 0, 0);
        if (!_thread.Join(TimeSpan.FromSeconds(3)))
        {
            // スレッドが終わらなくてもプロセス終了を妨げない (background thread)。
            // 登録表からは落としておき、以後メッセージが来ても無視されるようにする
            Unregister();
        }
    }

    private void Unregister()
    {
        if (_hwnd != default)
        {
            _instancesByHwnd.TryRemove(_hwnd, out _);
        }
        if (_iconHwnd != default)
        {
            _instancesByHwnd.TryRemove(_iconHwnd, out _);
        }
        _instancesByHookThread.TryRemove(_thread.ManagedThreadId, out _);
    }

    /// <summary>2 つのウィンドウクラスをプロセスに 1 回だけ登録する。</summary>
    private static void EnsureWindowClassesRegistered(HMODULE module)
    {
        lock (_classLock)
        {
            if (_classesRegistered)
            {
                return;
            }
            if (!RegisterClass(module, WindowClassName) || !RegisterClass(module, IconWindowClassName))
            {
                return;
            }
            _classesRegistered = true;
        }
    }

    /// <summary>ウィンドウクラスを 1 つ登録する。失敗したら <c>_classError</c> に理由を残す。</summary>
    private static unsafe bool RegisterClass(HMODULE module, string name)
    {
        fixed (char* className = name)
        {
            var wc = new WNDCLASSEXW
            {
                cbSize = (uint)sizeof(WNDCLASSEXW),
                lpfnWndProc = &WndProc,
                hInstance = new HINSTANCE(module.Value),
                lpszClassName = new PCWSTR(className),
                // クラスカーソルを持たないウィンドウは、カーソルが乗っても形を設定しない。
                // その結果「直前に居たウィンドウが出していた形のまま」になり、
                // 待ち カーソルを出しているアプリの上から確定アイコンへ移すと
                // 砂時計のまま張り付く。矢印を明示しておく。
                // **効くのはアイコンの窓のほうである** — 枠は WS_EX_TRANSPARENT で
                // ヒットテストから外れており、カーソルがその上に「乗る」ことがない。
                // 枠にも付けておくのは、外して非対称にする理由が無いためである
                hCursor = PInvoke.LoadCursor(default, PInvoke.IDC_ARROW),
            };
            if (PInvoke.RegisterClassEx(in wc) == 0)
            {
                _classError = $"RegisterClassEx('{name}') failed: error={Marshal.GetLastPInvokeError()}";
                return false;
            }
        }
        return true;
    }

    /// <summary>
    /// 1x1 のレイヤードウィンドウを作る。大きさと位置は <see cref="Redraw"/> の
    /// <c>UpdateLayeredWindow</c> が決めるので、ここでは置き場所を持たない。
    /// </summary>
    private static unsafe HWND CreateOverlayWindow(HMODULE module, string className, WINDOW_EX_STYLE exStyle)
    {
        fixed (char* name = className)
        {
            return PInvoke.CreateWindowEx(
                exStyle,
                new PCWSTR(name), default, WINDOW_STYLE.WS_POPUP,
                0, 0, 1, 1, default, default, new HINSTANCE(module.Value), null);
        }
    }

    private unsafe void ThreadMain()
    {
        HMODULE module = PInvoke.GetModuleHandle(default(PCWSTR));
        EnsureWindowClassesRegistered(module);

        // 枠の窓。WS_EX_TRANSPARENT が**この直しの本体**である —
        // これが付いた窓はヒットテストから丸ごと外れ、クリックは必ず下のウィンドウへ渡る。
        // ピクセルのアルファに左右されないので、不透明な枠線の上でも抜ける (docs/DESIGN.md §10)
        _hwnd = CreateOverlayWindow(module, WindowClassName, CommonExStyle | WINDOW_EX_STYLE.WS_EX_TRANSPARENT);

        // アイコンの窓。**WS_EX_TRANSPARENT を付けてはいけない** — 付けると確定アイコンが
        // 押せなくなる。全ピクセルが不透明なので、窓の矩形がそのまま当たり判定になる
        _iconHwnd = CreateOverlayWindow(module, IconWindowClassName, CommonExStyle);

        // WndProc がこのインスタンスを引けるようにする (メッセージループ開始前に登録)。
        // **2 つとも同じインスタンスを指す**
        if (_hwnd != default)
        {
            _instancesByHwnd[_hwnd] = this;
        }
        if (_iconHwnd != default)
        {
            _instancesByHwnd[_iconHwnd] = this;
        }
        if (_hwnd == default || _iconHwnd == default)
        {
            CreationError = _classError ??
                $"overlay window creation failed: frame=0x{(nint)_hwnd.Value:X} icon=0x{(nint)_iconHwnd.Value:X} " +
                $"error={Marshal.GetLastPInvokeError()}";
        }

        // 低レベルキーボードフック (このスレッドがメッセージループを回す)。
        // フックプロシージャは「仕掛けたスレッド」= このスレッドの上で呼ばれる
        _instancesByHookThread[Environment.CurrentManagedThreadId] = this;
        _hook = PInvoke.SetWindowsHookEx(WINDOWS_HOOK_ID.WH_KEYBOARD_LL, &HookProc, null, 0);

        _ready.Set();

        while (PInvoke.GetMessage(out MSG msg, default, 0, 0))
        {
            PInvoke.TranslateMessage(in msg);
            PInvoke.DispatchMessage(in msg);
        }

        Unregister();
        _hook?.Dispose();
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(System.Runtime.CompilerServices.CallConvStdcall)])]
    private static LRESULT WndProc(HWND hwnd, uint msg, WPARAM wParam, LPARAM lParam)
    {
        if (!_instancesByHwnd.TryGetValue(hwnd, out OverlayController? self))
        {
            // 登録前 (WM_CREATE 等) / 解放後のメッセージ
            return PInvoke.DefWindowProc(hwnd, msg, wParam, lParam);
        }
        switch (msg)
        {
            // **WM_DESTROY はこの引き (登録表の索引) の後ろに置いてある。**どちらの窓かを
            // 知らないと WM_QUIT を投げてよいか決められないためである。引き換えに、Dispose が
            // 3 秒で諦めて Unregister した**後に**窓が壊れた場合、ここへ来ても WM_QUIT が
            // 出ず、その経路ではオーバーレイスレッドが残る。スレッドは background なので
            // プロセス終了は妨げない。意図した取引である — その状況では既にスレッドが
            // 応答していない
            case PInvoke.WM_DESTROY:
                // **畳むのは枠の窓のときだけである。**窓が 2 つになったので、どちらの
                // WM_DESTROY でも WM_QUIT を投げると、片方を壊した時点でループが抜け、
                // もう片方が壊されないまま残る
                if (hwnd == self._hwnd)
                {
                    PInvoke.PostQuitMessage(0);
                }
                return (LRESULT)0;
            case MsgUpdate:
                self.Redraw();
                return (LRESULT)0;
            case MsgHide:
                // 2 つとも隠す。枠だけ隠すとアイコンが宙に浮いて残る
                PInvoke.ShowWindow(self._hwnd, SHOW_WINDOW_CMD.SW_HIDE);
                PInvoke.ShowWindow(self._iconHwnd, SHOW_WINDOW_CMD.SW_HIDE);
                return (LRESULT)0;
            case MsgQuit:
                // アイコンを先に壊す。枠の WM_DESTROY が WM_QUIT を投げるので、
                // 逆順にするとアイコンが残ったままループが抜ける
                PInvoke.DestroyWindow(self._iconHwnd);
                PInvoke.DestroyWindow(self._hwnd);
                return (LRESULT)0;
            // WM_NCHITTEST はもう扱わない。枠は WS_EX_TRANSPARENT で**窓ごと**
            // ヒットテストから外れており、アイコンは全ピクセルが不透明なので既定の
            // HTCLIENT でよい。HTTRANSPARENT を返す形はレイヤードウィンドウでは
            // クリックスルーにならず、クリックが落ちるだけである (docs/DESIGN.md §10 の実測)
            case PInvoke.WM_LBUTTONDOWN:
                // 枠は WS_EX_TRANSPARENT でヒットテストから外れているので、ここへ来るのは
                // アイコンの窓だけである。出どころを見るのは、枠から来たとしたら
                // **それ自体がクリックスルーの壊れ**であり、確定させてはいけないからである
                if (hwnd == self._iconHwnd)
                {
                    self.ConfirmClicked?.Invoke();
                }
                return (LRESULT)0;
        }
        return PInvoke.DefWindowProc(hwnd, msg, wParam, lParam);
    }

    // ここで見てよいのは vkCode だけである。入力の出どころ (注入フラグ) で分岐してはいけない。
    //
    // 合成した入力には例外なく注入ビットが立つ (docs/DESIGN.md §11 の実測)。ここで分岐すると
    // T5 (tests/UiaTrigger.Input.Tests) と物理入力が必ず別の経路を通り、注入だけを通す向きなら
    // T5 は緑のまま実機が死ぬ — docs/TESTING.md §4 のホイールバグと同じ形が 1 層上で再現する。
    // HookPolicyTests が不変条件として固定している (docs/TESTING.md §3)。
    [UnmanagedCallersOnly(CallConvs = [typeof(System.Runtime.CompilerServices.CallConvStdcall)])]
    private static unsafe LRESULT HookProc(int code, WPARAM wParam, LPARAM lParam)
    {
        _instancesByHookThread.TryGetValue(Environment.CurrentManagedThreadId, out OverlayController? self);
        if (code >= 0 && self is { _hookEnabled: true } &&
            (wParam.Value == PInvoke.WM_KEYDOWN || wParam.Value == PInvoke.WM_SYSKEYDOWN))
        {
            var info = (KBDLLHOOKSTRUCT*)lParam.Value;
            if (info->vkCode is VK_LEFT or VK_RIGHT)
            {
                // 通知するだけでキー自体はパススルーする (他アプリの ←/→ を奪わない)
                self.ArrowKeyPressed?.Invoke(info->vkCode == VK_RIGHT);
            }
        }
        return PInvoke.CallNextHookEx(null, code, wParam, lParam);
    }

    /// <summary>1 枚の ARGB バッファを埋める。<c>Span</c> を渡すので専用のデリゲートが要る。</summary>
    private delegate void PaintTo(Span<uint> pixels);

    /// <summary>
    /// 枠とアイコンをそれぞれの窓へ描き、最前面を主張し直す (オーバーレイスレッド上)。
    /// </summary>
    private void Redraw()
    {
        ElementRect rect;
        lock (_lock)
        {
            rect = _pendingRect;
        }
        // 幾何とピクセルの生成は OverlayGeometry (純関数) が持つ。
        // ここは DIB を用意して UpdateLayeredWindow へ流すだけ。
        // DPI は 1 回だけ引いて使い回す — 途中で変わると枠とアイコンがずれる
        int dpi = _dpi.DpiFor(rect);

        (int frameWidth, int frameHeight) = OverlayGeometry.FrameSize(rect, dpi);
        (int frameX, int frameY) = OverlayGeometry.FrameOrigin(rect);
        (int iconX, int iconY, int iconSize) = OverlayGeometry.IconRect(rect, dpi);

        UpdateLayered(_hwnd, frameWidth, frameHeight, frameX, frameY, px => OverlayGeometry.PaintFrame(rect, dpi, px));
        UpdateLayered(_iconHwnd, iconSize, iconSize, iconX, iconY, px => OverlayGeometry.PaintIcon(dpi, px));

        // 最前面を**描くたびに主張し直す** (docs/DESIGN.md §10)。
        //
        // WS_EX_TOPMOST は「トップモーストの帯に入る」だけで、帯の中の順位は
        // 最後に主張したものが勝つ。既に可視なウィンドウへの ShowWindow は
        // Z を動かさないので、他のトップモーストウィンドウ (対象アプリが
        // TopMost だとまさにそれ) が一度でも順位を主張すると、
        // **オーバーレイは以後ずっとその下に居続ける**。
        //
        // 実測で再現した: 捕捉直後はアイコンの上で WindowFromPoint がオーバーレイを返すが、
        // 対象アプリを帯の先頭へ出し直すと対象アプリのボタンを返すようになる。
        // 枠は見えたまま、確定アイコンだけが押せなくなる — 例外も出ない。
        //
        // **順番に意味がある。**枠 → アイコンの順に主張するので、アイコンが上に来る。
        // 2 つが重なるのは枠の右上の角だけで、枠はそこにも枠線を描いているため、
        // 逆順にすると**アイコンの左下が枠線に隠れる**
        ClaimTopmost(_hwnd);
        ClaimTopmost(_iconHwnd);
    }

    /// <summary>DIB を 1 枚作って <paramref name="paint"/> に描かせ、窓へ流す。</summary>
    private static unsafe void UpdateLayered(
        HWND hwnd, int width, int height, int originX, int originY, PaintTo paint)
    {
        var bmi = new BITMAPINFO
        {
            bmiHeader = new BITMAPINFOHEADER
            {
                biSize = (uint)sizeof(BITMAPINFOHEADER),
                biWidth = width,
                biHeight = -height, // top-down
                biPlanes = 1,
                biBitCount = 32,
                biCompression = 0, // BI_RGB
            },
        };

        HDC screenDc = PInvoke.GetDC(default);
        HDC memDc = PInvoke.CreateCompatibleDC(screenDc);
        using var bmpHandle = PInvoke.CreateDIBSection(screenDc, &bmi, DIB_USAGE.DIB_RGB_COLORS, out void* bits, default, 0);
        HGDIOBJ oldBmp = PInvoke.SelectObject(memDc, new HGDIOBJ(bmpHandle.DangerousGetHandle()));
        try
        {
            paint(new Span<uint>(bits, width * height));

            var dst = new System.Drawing.Point(originX, originY);
            var size = new SIZE { cx = width, cy = height };
            var src = new System.Drawing.Point(0, 0);
            var blend = new BLENDFUNCTION
            {
                BlendOp = 0,              // AC_SRC_OVER
                SourceConstantAlpha = 255,
                AlphaFormat = 1,          // AC_SRC_ALPHA
            };
            _ = PInvoke.UpdateLayeredWindow(hwnd, screenDc, &dst, &size, memDc, &src,
                new COLORREF(0), &blend, UPDATE_LAYERED_WINDOW_FLAGS.ULW_ALPHA);
            PInvoke.ShowWindow(hwnd, SHOW_WINDOW_CMD.SW_SHOWNOACTIVATE);
        }
        finally
        {
            PInvoke.SelectObject(memDc, oldBmp);
            PInvoke.DeleteDC(memDc);
            _ = PInvoke.ReleaseDC(default, screenDc);
        }
    }

    /// <summary>
    /// トップモースト帯の先頭を主張し直す。
    /// </summary>
    /// <remarks>
    /// <c>SWP_NOACTIVATE</c> を付けるのは必須である。付けないとオーバーレイが
    /// フォーカスを奪い、ホバー中の相手アプリの状態が変わってしまう。
    /// </remarks>
    private static void ClaimTopmost(HWND hwnd)
        => PInvoke.SetWindowPos(
            hwnd,
            HWND.HWND_TOPMOST,
            0, 0, 0, 0,
            SET_WINDOW_POS_FLAGS.SWP_NOMOVE | SET_WINDOW_POS_FLAGS.SWP_NOSIZE |
            SET_WINDOW_POS_FLAGS.SWP_NOACTIVATE);
}

