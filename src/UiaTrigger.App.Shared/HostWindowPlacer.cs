// --place-windows が指定されたとき、ホストが**自分の窓を自分で置く** (docs/TESTING.md §1 T4)。
//
// ■ なぜホスト側に要るのか
//
// T4 は対象アプリの上の 1 点 (pick 点) を指してホバー捕捉を起こす。ホストの窓がその点を
// 覆うと、ピッカーは自プロセスを掴んで捕捉を飛ばし、しかも TriggerPickerPresenter.TickAsync は
// 「同じ点は再捕捉しない」ので**二度と捕捉しない**。症状は原因を名指ししないタイムアウトになる。
//
// ハーネス (tests/UiaTrigger.Picker.UiTests/PickerHostProcess.cs) は、これを
// 「窓が出たら 5ms ごとに退かす見張りスレッド」で凌いでいた。**その見張りには寿命がある** —
// ボタンを押す前後だけ立ててあり、finally で止まる。止まったあとに現れた窓や、
// フレームワークがあとから位置を当て直した窓は誰も退かさず、T4 はそれで実際に落ちた。
//
// ■ 幾何では直せない
//
// カスケードの位置は**セッション中に作られた窓の数で動く**。つまり「pick 点との余裕」を
// 測る相手になる定数が無い。対象アプリを寄せても、窓の隙間を広げても、原理的に塞がらない。
// だから位置を当てる側 (= 窓を持っているプロセス) で決める。
//
// ■ 何が良くなるのか — 正直に書く
//
// **速くなるのではない。寿命が無くなる。**フックはプロセスが生きているあいだ常に張られており、
// あとから現れた窓も、フレームワークが当て直した窓も置かれる。窓が現れてから置き終えるまでの
// 遅れそのものは、見張りスレッドのそれ (実測 WinUI 21ms / WPF 139ms) と同じ桁である。
// 滞留の 1000ms に対しては桁がひとつ違うので、どちらでも捕捉には間に合う。
// **観測された失敗形 (見張りが止まったあとに窓が居座る) が成立しなくなる**、というのが効能である。
//
// ■ この経路は通常の利用では動かない
//
// --place-windows を渡さなければフックは張られない。T4 のためだけに在る口である
// (--pick-at と同じ位置づけ — docs/DESIGN.md §12)。
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace UiaTrigger.App.Shared;

/// <summary>自プロセスの窓を、指定された矩形へ置き続ける。</summary>
internal static unsafe class HostWindowPlacer
{
    /// <summary>
    /// 同じ窓を置き直す回数の上限。
    /// </summary>
    /// <remarks>
    /// **無くてはならない。**<c>EVENT_OBJECT_LOCATIONCHANGE</c> の中で
    /// <c>SetWindowPos</c> を呼ぶので、フレームワークが配置を何度も当て直すと
    /// 置き合いになりうる。矩形が一致していれば触らない (下記) ので普通は止まるが、
    /// 止まらなかったときに**ホストの UI スレッドが固まる**のが最悪の形である —
    /// T4 からは「窓が実体化しなかった」という 30 秒タイムアウトに見え、
    /// まさにこの変更が消そうとしている読めない失敗になる。上限で降りて理由を残す。
    /// </remarks>
    private const int MaxPlacementsPerWindow = 64;

    /// <summary>同じ窓を続けて置き直すときの最短間隔 (ms)。</summary>
    /// <remarks>
    /// **撃つ速さを縛る。**フレームワークが配置を当て直すたびに撃ち返すと、
    /// 上限に当たるまでが一瞬になり (実測で 64 回 / 2.3 秒)、その間ホストの UI スレッドに
    /// <c>WM_WINDOWPOSCHANGING</c> を撒き続ける。滞留は 1000ms なので、
    /// 50ms 間隔でも捕捉には十分間に合う。
    /// </remarks>
    private const long PlacementCooldownMs = 50;

    private static WindowPlacement _target;
    private static Action<string> _log = _ => { };
    private static readonly Dictionary<nint, int> _placements = [];
    private static readonly Dictionary<nint, long> _lastPlacedAt = [];

    /// <summary>
    /// クールダウンで落とした置き直し要求。次のイベントで拾い直す (<see cref="RetryPending"/>)。
    /// </summary>
    private static readonly HashSet<nint> _pendingPlacement = [];

    /// <summary>同期的な再入の防止。</summary>
    /// <remarks>
    /// **置き合いを止めているのはこれではなく、下の「既にその場所に在れば触らない」である。**
    /// <c>WINEVENT_OUTOFCONTEXT</c> の通知はメッセージループ経由で**あとから**来るので、
    /// 自分の <c>SetWindowPos</c> が撒き返した分はここでは捕まらない。
    /// これは <c>SetWindowPos</c> が同じスレッドへ同期で送るものを踏んだときの保険である。
    /// </remarks>
    [ThreadStatic]
    private static bool _placing;

    /// <summary>
    /// 自プロセスの窓を <paramref name="target"/> へ置き続ける。**1 度だけ呼ぶ。**
    /// </summary>
    /// <remarks>
    /// <para>
    /// 呼ぶのは**ウィンドウを 1 つも作る前**である。あとから張ると、既に出ている窓を
    /// 取りこぼす (フックは「これから起きること」しか教えない)。
    /// </para>
    /// <para>
    /// フックの購読はこのスレッドのメッセージループで配られる。3 ホストとも
    /// UI スレッドから呼ぶので、<c>SetWindowPos</c> は窓を持っているスレッドで走る。
    /// </para>
    /// </remarks>
    public static void Start(WindowPlacement target, Action<string> log)
    {
        ArgumentNullException.ThrowIfNull(log);
        _target = target;
        _log = log;

        // SHOW と LOCATIONCHANGE の両方が要る。出た瞬間に置くだけでは、
        // フレームワークがあとから当て直したときに戻ってしまう
        nint show = NativeWindowPlacement.HookOwnProcess(
            NativeWindowPlacement.EVENT_OBJECT_SHOW,
            NativeWindowPlacement.EVENT_OBJECT_SHOW,
            &OnWindowEvent);
        nint moved = NativeWindowPlacement.HookOwnProcess(
            NativeWindowPlacement.EVENT_OBJECT_LOCATIONCHANGE,
            NativeWindowPlacement.EVENT_OBJECT_LOCATIONCHANGE,
            &OnWindowEvent);

        // **黙って通常動作に落ちない。**張れなければ窓は退かず、T4 からは
        // 「pick 点が覆われている」という別の顔で見えることになるので、理由を残す。
        // 片方だけ張れた場合は「退きません」が嘘になる (SHOW だけなら出た瞬間には退く) ので、
        // 実態どおりに言い分ける。成功した側は残す — 片方でも動くほうが、両方捨てるより良い
        if (show == 0 || moved == 0)
        {
            string effect = (show, moved) switch
            {
                (0, 0) => "Windows will not move out of the way.",
                (0, _) => "Windows will not move out of the way when they appear (moves are still followed).",
                _ => "Moves are not followed (windows still move out of the way when they appear).",
            };
            _log(FormattableString.Invariant(
                $"--place-windows: SetWinEventHook failed (show={show} moved={moved}). {effect}"));
            if (show == 0 && moved == 0)
            {
                return;
            }
        }

        _log(FormattableString.Invariant(
            $"--place-windows: placing windows at ({_target.Left},{_target.Top}) {_target.Width}x{_target.Height}."));
    }

    /// <summary>
    /// 窓のイベント。**デリゲートではなく関数ポインタである** (AOT で回収されないため)。
    /// </summary>
    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
    private static void OnWindowEvent(
        nint hook, uint eventType, nint hwnd, int idObject, int idChild, uint thread, uint time)
    {
        // EVENT_OBJECT_SHOW は子コントロールが出ても飛ぶ。ここで絞らないと殺到する
        if (idObject != NativeWindowPlacement.OBJID_WINDOW ||
            idChild != NativeWindowPlacement.CHILDID_SELF ||
            hwnd == 0)
        {
            return;
        }

        // 自分の SetWindowPos が撒き返した LOCATIONCHANGE で再入しない
        if (_placing)
        {
            return;
        }

        try
        {
            _placing = true;
            // クールダウンで落とした窓を先に拾い直す (下の Place の remarks)。
            // 死んだ窓の分はここで台帳ごと落とす — 残すと T4 の 1 実行で
            // 開いては閉じる窓のぶん、辞書が増える一方になる
            RetryPending();
            Place(hwnd);
        }
        catch (Exception ex)
        {
            // **コールバックから例外を投げない。**アンマネージドの呼び出し元へ抜けるので、
            // プロセスがその場で落ちる。理由だけ残して飲む
            _log($"--place-windows: could not place a window: {ex}");
        }
        finally
        {
            _placing = false;
        }
    }

    private static void Place(nint hwnd)
    {
        // 可視で最小化されていないトップレベルだけを置く (NativeWindowPlacement.IsPlaceable)
        if (!NativeWindowPlacement.IsPlaceable(hwnd))
        {
            return;
        }

        // **オーバーレイは動かさない。**選択された要素の上に出るのが仕事であり、
        // 退かしたら検査対象そのものを壊す
        if (NativeWindowPlacement.IsOverlay(hwnd))
        {
            return;
        }

        // **既にその場所に在れば触らないうえ、数え直しも解く。**
        // 解くのが肝である — 一度落ち着いた窓があとで動かされたとき、
        // 前の取り合いの回数を引きずって「もう触らない」に入ってしまう
        if (IsAtTarget(hwnd))
        {
            _ = _placements.Remove(hwnd);
            return;
        }

        // 撃つ速さを縛る。当て直されるたびに撃ち返すと上限まで一瞬で、
        // その間ホストの UI スレッドを塞ぐ
        long now = Environment.TickCount64;
        if (_lastPlacedAt.TryGetValue(hwnd, out long last) && now - last < PlacementCooldownMs)
        {
            // **落とした要求を覚えておく。**クールダウン中の最後の 1 通を捨て切りにすると、
            // そこで当て直しが止まったときに窓が目標から外れたまま残る (T4 では
            // 「pick 点が覆われている」という名指し失敗として現れる)。次にイベントが
            // 来たときに拾い直す — 窓のイベントはこの経路に頻繁に飛ぶ
            _pendingPlacement.Add(hwnd);
            return;
        }
        _ = _pendingPlacement.Remove(hwnd);

        if (_placements.TryGetValue(hwnd, out int count) && count >= MaxPlacementsPerWindow)
        {
            return;
        }

        _placements[hwnd] = count + 1;
        _lastPlacedAt[hwnd] = now;

        if (count + 1 == MaxPlacementsPerWindow)
        {
            // 降りる。**これは負けを認める側の記録である** — 目標に届かないまま
            // 上限まで来たということなので、この窓は動かせない (実測では最小化された窓が
            // これに当たっていた。いまは IsPlaceable が先に弾く)。
            // 覆っているかどうかは T4 のガードが名指しで落とす
            _log(FormattableString.Invariant(
                $"--place-windows: window 0x{hwnd:X} did not reach the target after {MaxPlacementsPerWindow} attempts; leaving it alone from now on."));
        }

        _ = NativeWindowPlacement.MoveTo(hwnd, _target.Left, _target.Top, _target.Width, _target.Height);
    }

    /// <summary>
    /// クールダウンで落とした窓を拾い直し、ついでに死んだ窓の台帳を捨てる。
    /// </summary>
    /// <remarks>
    /// 窓のイベントと同じスレッド (ホストの UI スレッド) からしか呼ばれないので、
    /// 辞書に排他は要らない。**別スレッドから呼ぶ形に変えないこと** — タイマーで
    /// 遅延再試行する形にすると、この 3 つの辞書が競合し、しかも T4 専用経路なので
    /// 壊れたことに気づく網が無い
    /// </remarks>
    private static void RetryPending()
    {
        if (_pendingPlacement.Count == 0)
        {
            return;
        }
        foreach (nint pending in _pendingPlacement.ToArray())
        {
            if (!NativeWindowPlacement.IsWindow(pending))
            {
                _ = _pendingPlacement.Remove(pending);
                _ = _placements.Remove(pending);
                _ = _lastPlacedAt.Remove(pending);
                continue;
            }
            Place(pending);
        }
    }

    /// <summary>その窓が既に目標の矩形に在るか。</summary>
    private static bool IsAtTarget(nint hwnd)
        => NativeWindowPlacement.RectOf(hwnd) is { } r &&
           r.Left == _target.Left && r.Top == _target.Top &&
           r.Right == _target.Left + _target.Width && r.Bottom == _target.Top + _target.Height;
}
