// T5 の唯一の入力口 (docs/TESTING.md §1 の T5 / §3)。
//
// ここだけが最下層へ入る。ハードウェア入力と同じコードが処理するので、
// WH_KEYBOARD_LL / WH_MOUSE_LL・フォアグラウンド規則・WM_NCHITTEST・DPI 依存の
// ヒットテストを<b>すべて通る</b>。物理入力と違うのは 3 点だけである:
//   ・注入ビットが立つ (実測で例外なく立つ — docs/DESIGN.md §11。だから HookProc がそこで
//     分岐しないことを HookPolicyTests が不変条件として固定してある)
//   ・UIPI — より高い整合性レベルのプロセスへは撃てない (昇格プロセスは手動のまま)
//   ・デバイスとドライバの層、および実モニターの DPI を通らない
//
// <b>捕捉 (ホバー滞留) にはここを使わない。</b>実カーソルを動かすと捕捉の経路に
// 座標の円環が戻り (docs/TESTING.md §4)、しかも滞留 1000ms はわずかな移動でリセットされる。
// 捕捉は --pick-at / FixedCursorSource のままである。
// ここを使ってよいのは<b>検査対象そのもの</b> (キー・クリック) だけ。
using System.Globalization;
using System.Runtime.InteropServices;
using Xunit;

namespace UiaTrigger.Input.Tests;

/// <summary>
/// 最下層へ入る合成入力。<b>撃てなかったことと、届かなかったことを区別する。</b>
/// </summary>
/// <remarks>
/// <para>
/// どのメソッドも、OS が受け付けた本数が期待と違えば<b>その場で落ちる</b>。
/// これは docs/TESTING.md §4 の規律である — 「入力が届かなかった」と
/// 「<b>ハーネスが撃てなかった</b>」を混ぜると、対象の不具合とハーネスの不具合が
/// 同じ赤に見える。
/// </para>
/// <para>
/// CsWin32 は入れていない。T4 の <c>NativeWindows</c> と同じ方針で、
/// P/Invoke はここだけなので生成器を持ち込むのは釣り合わない。
/// </para>
/// </remarks>
internal static partial class SyntheticInput
{
    /// <summary>← キー。</summary>
    public const ushort VkLeft = 0x25;

    /// <summary>→ キー。</summary>
    public const ushort VkRight = 0x27;

    /// <summary>
    /// キーを 1 つ押して離す。
    /// </summary>
    /// <remarks>
    /// <para>
    /// down と up を<b>1 回の呼び出しで</b>入れる。2 回に分けると、あいだに他の入力
    /// (人が触った実キーボード) が割り込みうる。
    /// </para>
    /// <para>
    /// <b>フォーカスは要らない。</b>低レベルフックはグローバルで、フォアグラウンドが
    /// どこであっても発火する。フォーカスが要るのは「対象アプリのコントロールに
    /// 届くこと」を見るときだけで、そちらは撃つ前に <c>HasKeyboardFocus</c> を検算する。
    /// </para>
    /// </remarks>
    public static void TapKey(ushort virtualKey)
    {
        INPUT[] events =
        [
            KeyEvent(virtualKey, up: false),
            KeyEvent(virtualKey, up: true),
        ];
        Send(events, FormattableString.Invariant($"キー 0x{virtualKey:X2} の押下と解放"));
    }

    /// <summary>
    /// いまカーソルが在る位置で左ボタンを押して離す。
    /// </summary>
    /// <remarks>
    /// <b>座標はここでは指定しない。</b>狙いの検算 (期待矩形を 2 つの独立した情報源から
    /// 出して一致を見る) を済ませた呼び出し側が <see cref="CursorGuard.MoveTo"/> で
    /// カーソルを置き、ここは「その点を押す」だけを行う。
    /// 座標と押下を 1 つの呼び出しに混ぜると、検算を飛ばして撃つ経路ができてしまう。
    /// </remarks>
    public static void TapLeftButton()
    {
        INPUT[] events =
        [
            MouseEvent(MouseeventfLeftdown),
            MouseEvent(MouseeventfLeftup),
        ];
        Send(events, "左ボタンの押下と解放");
    }

    private static void Send(INPUT[] events, string what)
    {
        uint sent = SendInput((uint)events.Length, events, Marshal.SizeOf<INPUT>());
        Assert.True(
            sent == (uint)events.Length,
            FormattableString.Invariant(
                $"{what} を OS が受け付けませんでした ({sent}/{events.Length} 本、GetLastError={Marshal.GetLastWin32Error()})。") +
            "これは「入力が届かなかった」ではなく「ハーネスが撃てなかった」です — " +
            "入力デスクトップに居ないか、UIPI でより高い整合性レベルのプロセスに阻まれています " +
            "(docs/TESTING.md §3)。");
    }

    private static INPUT KeyEvent(ushort virtualKey, bool up) => new()
    {
        type = InputKeyboard,
        U = new InputUnion
        {
            ki = new KEYBDINPUT
            {
                wVk = virtualKey,
                dwFlags = up ? KeyeventfKeyup : 0,
            },
        },
    };

    private static INPUT MouseEvent(uint flags) => new()
    {
        type = InputMouse,
        U = new InputUnion { mi = new MOUSEINPUT { dwFlags = flags } },
    };

    /// <summary>
    /// カーソル位置を保存し、<c>Dispose</c> で必ず戻す。
    /// </summary>
    /// <remarks>
    /// <b>必須の作法である</b> (docs/TESTING.md §3)。開発機で走らせるとマウスが取られるので、
    /// テストが終わったら人が置いていた場所へ返す。失敗して抜けるときも通るように
    /// <c>using</c> で使うこと。
    /// </remarks>
    public sealed class CursorGuard : IDisposable
    {
        private readonly POINT _saved;
        private readonly bool _restorable;

        private CursorGuard(POINT saved, bool restorable)
        {
            _saved = saved;
            _restorable = restorable;
        }

        public static CursorGuard Save()
        {
            bool ok = GetCursorPos(out POINT p);
            return new CursorGuard(p, ok);
        }

        /// <summary>
        /// カーソルを置く。<b>置けたことを確かめてから返る。</b>
        /// </summary>
        /// <remarks>
        /// <c>SetCursorPos</c> は成功を返しても、別のフックやリモートデスクトップの
        /// カーソル制御で<b>実際には動かないことがある</b>。動いていないまま撃つと
        /// 「クリックが効かない」に見えるが、実際には狙っていない点を押している。
        /// </remarks>
        public static void MoveTo(int x, int y)
        {
            Assert.True(
                SetCursorPos(x, y),
                FormattableString.Invariant(
                    $"カーソルを ({x},{y}) へ動かせませんでした (GetLastError={Marshal.GetLastWin32Error()})。"));
            Assert.True(
                GetCursorPos(out POINT now) && now.x == x && now.y == y,
                string.Create(
                    CultureInfo.InvariantCulture,
                    $"カーソルを ({x},{y}) へ置いたはずが ({now.x},{now.y}) に居ます。") +
                "この状態で撃つと、狙っていない点を押します — " +
                "「クリックが効かない」ではなく「ハーネスが狙いを立てられなかった」です。");
        }

        public void Dispose()
        {
            if (_restorable)
            {
                _ = SetCursorPos(_saved.x, _saved.y);
            }
        }
    }

    private const uint InputMouse = 0;
    private const uint InputKeyboard = 1;
    private const uint KeyeventfKeyup = 0x0002;
    private const uint MouseeventfLeftdown = 0x0002;
    private const uint MouseeventfLeftup = 0x0004;

    [StructLayout(LayoutKind.Sequential)]
    internal struct POINT
    {
        public int x;
        public int y;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct INPUT
    {
        public uint type;
        public InputUnion U;
    }

    /// <summary>
    /// <c>INPUT</c> の共用体。
    /// </summary>
    /// <remarks>
    /// <b><c>MOUSEINPUT</c> は使わなくても書いておくこと。</b>共用体の大きさは最大のメンバーで
    /// 決まり、<c>INPUT</c> の大きさは <c>cbSize</c> として OS へ渡る。
    /// キーボードのぶんだけ書くと 64bit で 40 ではなく 32 になり、
    /// <c>SendInput</c> は<b>1 本も入れずに 0 を返す</b> — 「届かなかった」に見える形で
    /// ハーネスが壊れる。
    /// </remarks>
    [StructLayout(LayoutKind.Explicit)]
    private struct InputUnion
    {
        [FieldOffset(0)]
        public MOUSEINPUT mi;

        [FieldOffset(0)]
        public KEYBDINPUT ki;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MOUSEINPUT
    {
        public int dx;
        public int dy;
        public uint mouseData;
        public uint dwFlags;
        public uint time;
        public nuint dwExtraInfo;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct KEYBDINPUT
    {
        public ushort wVk;
        public ushort wScan;
        public uint dwFlags;
        public uint time;
        public nuint dwExtraInfo;
    }

    [LibraryImport("user32.dll", SetLastError = true)]
    private static partial uint SendInput(uint cInputs, INPUT[] pInputs, int cbSize);

    [LibraryImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool GetCursorPos(out POINT lpPoint);

    [LibraryImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool SetCursorPos(int X, int Y);
}
