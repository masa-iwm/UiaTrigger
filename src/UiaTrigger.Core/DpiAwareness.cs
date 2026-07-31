// 座標を扱う UIA クライアントの前提条件 (docs/DESIGN.md A19 / C13)。
//
// ElementFromPoint / GetCursorPos に渡すのは物理スクリーン座標である。ところが
// DPI 非認識のプロセスでは Windows が座標を仮想化するため、渡した座標がスケール分だけ
// ずれた位置として解釈される。175% の画面では (174,149) が (304,260) として扱われ、
// 狙った子要素の外にある親要素が返る。
//
// 厄介なのは <b>例外にならない</b> ことで、「別の要素を記録した定義」が静かに出来上がる。
// これは docs/TESTING.md §4 のホイールバグ (座標系の不一致で入力がコンテンツ外へ落ちる) と
// 同じ壊れ方であり、実際にこの検査で record が対象でなく親要素を記録していたのを見つけている。
//
// public なのは、ホストが PerMonitorV2 を宣言していることがこのライブラリの前提であり、
// 前提を確かめる手段を internal に隠しておく理由が無いため (C13)。
using System.Runtime.InteropServices;
using UiaTrigger.Resources;

namespace UiaTrigger;

/// <summary>The host process's DPI awareness, which decides whether screen coordinates work.</summary>
/// <remarks>
/// <para>
/// Everything this library does with a point — <see cref="UiaSession.ElementFromPointAsync"/>,
/// <see cref="UiaSession.ElementFromCursorAsync"/>, recording a definition from a coordinate, an
/// overlay drawn around an element — uses <b>physical</b> screen coordinates. Windows virtualizes
/// coordinates for a process that is not per-monitor DPI aware, so on a scaled display those calls
/// land somewhere else and quietly return the wrong element. Nothing throws.
/// </para>
/// <para>
/// The fix belongs to the host: declare <c>dpiAwareness=PerMonitorV2</c> in the application
/// manifest. <see cref="TryEnablePerMonitorV2"/> is for hosts that cannot ship a manifest (a
/// console tool, a test harness) and must be called before any window exists.
/// </para>
/// </remarks>
public static partial class DpiAwareness
{
    // DPI_AWARENESS_CONTEXT は疑似ハンドル。値は winuser.h の定義そのもの
    private static readonly nint PerMonitorAwareV2 = -4;

    private const int DpiAwarenessInvalid = -1;
    private const int DpiAwarenessUnaware = 0;
    private const int DpiAwarenessSystemAware = 1;
    private const int DpiAwarenessPerMonitorAware = 2;

    /// <summary>
    /// Whether this process can pass physical screen coordinates to UI Automation as they are.
    /// </summary>
    /// <remarks>
    /// True only for the per-monitor modes. System awareness happens not to virtualize coordinates
    /// on a single-monitor machine, but breaks as soon as a second monitor has a different scale,
    /// so it does not count as safe.
    /// </remarks>
    public static bool IsCoordinateSafe() => CurrentAwareness() == DpiAwarenessPerMonitorAware;

    /// <summary>
    /// Declares this process per-monitor (V2) DPI aware. Call before creating any window.
    /// </summary>
    /// <returns>
    /// True when this call changed the mode. False when it could not — including the common case of
    /// the manifest having already declared it, where <see cref="IsCoordinateSafe"/> is true anyway.
    /// </returns>
    public static bool TryEnablePerMonitorV2() => SetProcessDpiAwarenessContext(PerMonitorAwareV2);

    /// <summary>
    /// A localized description of why coordinates cannot be trusted, or null when they can.
    /// </summary>
    /// <remarks>
    /// Report this rather than assuming: a host that gets this wrong records definitions that point
    /// at the wrong element, and those definitions look perfectly valid afterwards.
    /// </remarks>
    public static string? DescribeProblem()
    {
        int awareness = CurrentAwareness();
        return awareness == DpiAwarenessPerMonitorAware
            ? null
            : Message.Format(Strings.Error_DpiUnaware, NameOf(awareness));
    }

    /// <summary>診断メッセージに載せる安定名 (英語固定。ログを検索できるようにするため)。</summary>
    private static string NameOf(int awareness) => awareness switch
    {
        DpiAwarenessInvalid => "Invalid",
        DpiAwarenessUnaware => "Unaware",
        DpiAwarenessSystemAware => "System",
        DpiAwarenessPerMonitorAware => "PerMonitorV2",
        _ => "Unknown",
    };

    private static int CurrentAwareness() =>
        GetAwarenessFromDpiAwarenessContext(GetThreadDpiAwarenessContext());

    [LibraryImport("user32.dll")]
    private static partial nint GetThreadDpiAwarenessContext();

    [LibraryImport("user32.dll")]
    private static partial int GetAwarenessFromDpiAwarenessContext(nint value);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool SetProcessDpiAwarenessContext(nint value);
}
