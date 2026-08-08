// 公開 API から見た「UIA 要素 1 個」(docs/DESIGN.md §3)。
//
// Picker が Core の internal な IUIAutomationElement を直接掴む形だと
// InternalsVisibleTo が要り、第三者は自前のピッカー / インスペクタを作れない。
// COM 型は公開せず、この不透明ハンドルだけを渡す。
//
// 識別・表示に使うプロパティは生成時に 1 往復でまとめて読んである (docs/DESIGN.md B1) ので、
// メンバーの参照はクロスプロセス呼び出しを起こさない。
using UiaTrigger.Interop;
using UiaTrigger.Models;

namespace UiaTrigger;

/// <summary>A handle to one UI Automation element, valid for the lifetime of its session.</summary>
/// <remarks>
/// <para>
/// Obtained from a <see cref="UiaSession"/>. The values on this type were all read in a single
/// cross-process call when the handle was created, so reading them is free — but they are a
/// **snapshot**: an element whose name changes afterwards still reports the old one. Call
/// <see cref="UiaSession.ReadSnapshotAsync"/> for current values.
/// </para>
/// <para>
/// Dispose a handle once you are done with it. Until then it keeps the target application's
/// provider object alive; the finalizer releases it eventually, but on a picker that walks a large
/// tree "eventually" means thousands of live cross-process references.
/// </para>
/// <para>
/// Handles are not comparable with <c>==</c>: UI Automation returns a different object every time
/// it is asked for the same element. Use <see cref="UiaSession.AreSameAsync"/>.
/// </para>
/// </remarks>
public sealed class UiaElement : IDisposable
{
    /// <summary>
    /// <see cref="_state"/> の解放要求ビット。下位ビットはアクティブな借用数。
    /// </summary>
    /// <remarks>
    /// 解放 (FinalRelease) は「フラグが立っている かつ 借用 0」でしか起きない (docs/DESIGN.md B10)。
    /// 借用中の明示 Dispose は解放を**借用終了まで遅延**する — 即時に解放すると、
    /// ディスパッチャスレッドが進行中のクロスプロセス呼び出しの真下で RCW を失い、
    /// 例外ではなくアクセス違反でプロセスごと落ちる (§7 の use-after-free と同じ壊れ方)。
    /// </remarks>
    private const int DisposedFlag = 1 << 30;

    private readonly bool _releasable;
    private IUIAutomationElement? _element;
    private int _state;

    private UiaElement(IUIAutomationElement element, bool releasable)
    {
        _element = element;
        _releasable = releasable;

        // すべて cached。UiaContext.ElementCacheRequest に入れたプロパティなので
        // ここで追加のクロスプロセス呼び出しは発生しない (docs/DESIGN.md B1)
        element.get_CachedProcessId(out int processId);
        element.get_CachedControlType(out int controlType);
        element.get_CachedLocalizedControlType(out string localizedControlType);
        element.get_CachedAutomationId(out string automationId);
        element.get_CachedName(out string name);
        element.get_CachedClassName(out string className);
        element.get_CachedBoundingRectangle(out UiaRect rect);
        element.get_CachedNativeWindowHandle(out nint hwnd);
        element.get_CachedIsOffscreen(out bool isOffscreen);

        ProcessId = processId;
        ControlType = controlType;
        // プロバイダーが返さないことがある。そのときだけ安定名で埋める (docs/DESIGN.md L6)
        LocalizedControlType = string.IsNullOrEmpty(localizedControlType)
            ? UiaControlTypeNames.GetName(controlType)
            : localizedControlType;
        AutomationId = automationId ?? string.Empty;
        Name = name ?? string.Empty;
        ClassName = className ?? string.Empty;
        BoundingRectangle = new ElementRect(rect.Left, rect.Top, rect.Right, rect.Bottom);
        NativeWindowHandle = hwnd;
        IsOffscreen = isOffscreen;
    }

    /// <summary>
    /// <see cref="UiaContext.ElementCacheRequest"/> でキャッシュ済みの要素を包む。
    /// <paramref name="releasable"/> は一意 RCW かどうか (共有 RCW を FinalRelease してはならない)。
    /// </summary>
    internal static UiaElement FromCached(IUIAutomationElement element, bool releasable) => new(element, releasable);

    /// <summary>Id of the process that owns the element.</summary>
    public int ProcessId { get; }

    /// <summary>UI Automation control type id, or 0 when it could not be read.</summary>
    public int ControlType { get; }

    /// <summary>Stable English name of <see cref="ControlType"/>, for example <c>Button</c>.</summary>
    /// <remarks>
    /// This is the identity form: it never changes with a culture, and it is what gets persisted.
    /// To show a control type to a user, use <see cref="LocalizedControlType"/>.
    /// </remarks>
    public string ControlTypeName => UiaControlTypeNames.GetName(ControlType);

    /// <summary>The control type as UI Automation localizes it, for example <c>button</c>.</summary>
    /// <remarks>
    /// The display form, and never an identity: the provider supplies it in **the target
    /// application's** language, not the caller's, and the same control can report a different
    /// string after the target is restarted in another language. Falls back to
    /// <see cref="ControlTypeName"/> when the provider supplies nothing. Never persist this — see
    /// <see cref="ControlTypeName"/>.
    /// </remarks>
    public string LocalizedControlType { get; }

    /// <summary>Automation id, or an empty string when the element has none.</summary>
    public string AutomationId { get; }

    /// <summary>Name at the time the handle was created.</summary>
    public string Name { get; }

    /// <summary>Class name, or an empty string. Very stable for Win32 controls.</summary>
    public string ClassName { get; }

    /// <summary>Bounds in physical screen coordinates.</summary>
    /// <remarks>
    /// Physical, not scaled: a process that is not per-monitor DPI aware cannot compare these with
    /// its own window coordinates. See <see cref="DpiAwareness"/>.
    /// </remarks>
    public ElementRect BoundingRectangle { get; }

    /// <summary>Window handle of the element, or 0 when it is not a window in its own right.</summary>
    public nint NativeWindowHandle { get; }

    /// <summary>Whether the element was off-screen when the handle was created.</summary>
    public bool IsOffscreen { get; }

    /// <summary>True once <see cref="Dispose"/> has run; the handle can no longer be used.</summary>
    public bool IsDisposed => (Volatile.Read(ref _state) & DisposedFlag) != 0;

    /// <summary>
    /// A label built from the element's stable attributes, for trees and diagnostics.
    /// </summary>
    /// <remarks>
    /// Deliberately built from <see cref="ControlTypeName"/> rather than UI Automation's localized
    /// control type: this string is used to identify an element across runs, not to present it.
    /// </remarks>
    public string Label => AutomationId.Length > 0
        ? $"{ControlTypeName} \"{Name}\" [{AutomationId}]"
        : $"{ControlTypeName} \"{Name}\"";

    /// <summary>
    /// The same label as <see cref="Label"/>, but naming the control type the way the target
    /// application does. For showing in a UI.
    /// </summary>
    /// <remarks>
    /// Not interchangeable with <see cref="Label"/>: this one changes with the target's language,
    /// so it must not be compared against a stored string or written to a file.
    /// </remarks>
    public string DisplayLabel => AutomationId.Length > 0
        ? $"{LocalizedControlType} \"{Name}\" [{AutomationId}]"
        : $"{LocalizedControlType} \"{Name}\"";

    /// <summary>Returns <see cref="Label"/> — the stable form, because this is for diagnostics.</summary>
    public override string ToString() => Label;

    /// <summary>Releases the element's cross-process reference. Safe to call more than once.</summary>
    /// <remarks>
    /// Safe to call from any thread: when the session's automation thread is mid-call on this
    /// element, the release is deferred until that call returns instead of pulling the COM object
    /// out from under it.
    /// </remarks>
    public void Dispose()
    {
        Release();
        GC.SuppressFinalize(this);
    }

    /// <summary>
    /// 一意 RCW は ComWrappers の同一性テーブルに載らないため、Dispose 忘れは
    /// RCW 自身のファイナライザ任せになる。ここでも解放しておくと
    /// 「GC まで相手プロセスの provider を掴んだまま」の窓が短くなる。
    /// 借用中はそもそも到達可能 (Borrowed が持ち主を根に留める) なので、ここへ来た時点で
    /// 借用数は必ず 0 である。
    /// </summary>
    ~UiaElement() => Release();

    /// <summary>
    /// 解放を**要求**する。借用が残っていれば実際の解放は最後の借用終了まで遅延する (B10)。
    /// </summary>
    private void Release()
    {
        int state = Volatile.Read(ref _state);
        while (true)
        {
            if ((state & DisposedFlag) != 0)
            {
                return; // 既に要求済み (Dispose の多重呼び出し / Dispose とファイナライザー)
            }
            int seen = Interlocked.CompareExchange(ref _state, state | DisposedFlag, state);
            if (seen == state)
            {
                break;
            }
            state = seen;
        }
        if (state == 0)
        {
            ReleaseNow(); // 借用なしでフラグを確定できたのは自分だけ — 解放も自分の仕事
        }
    }

    /// <summary>最後の借用が閉じた。解放が遅延されていたならここで実行する。</summary>
    private void EndBorrow()
    {
        if (Interlocked.Decrement(ref _state) == DisposedFlag)
        {
            ReleaseNow();
        }
    }

    private void ReleaseNow()
    {
        // Exchange は「Release とEndBorrow が同時に条件を満たす」形が現れたときの最後の保険。
        // 上の遷移規則上は 1 経路しか届かないが、二重 FinalRelease は生きている参照を落とす
        // 種類の事故なので、ここでも冪等にしておく
        IUIAutomationElement? element = Interlocked.Exchange(ref _element, null);
        if (element is not null && _releasable)
        {
            UiaFactory.ReleaseUnique(element);
        }
    }

    /// <summary>
    /// COM 要素を「使っているあいだ」借りる (Core 内部のみ)。
    /// </summary>
    /// <remarks>
    /// <para>
    /// **生のポインターを裸で持ち出してはならない。**取り出しただけでは、その直後から
    /// <see cref="UiaElement"/> 自身が到達不能とみなされうる — JIT は「最後に使った後」で
    /// 生存を打ち切ってよく、そうなると <see cref="Finalize"/> が
    /// **GC のファイナライザースレッド**で RCW を解放する。呼び出しているのは UIA スレッドなので、
    /// 解放済みの COM オブジェクトを呼ぶことになり、例外ではなく
    /// **アクセス違反でプロセスごと落ちる**。
    /// </para>
    /// <para>
    /// 実際に落ちた実績がある (<c>FindAllBuildCache</c> の中で 0xc0000005)。
    /// スコープを閉じるときに <see cref="GC.KeepAlive"/> するので、
    /// <c>using</c> の範囲内では解放されないことが保証される。
    /// </para>
    /// <para>
    /// 借用の成立は解放と排他である (docs/DESIGN.md B10): フラグ無しの状態で数を増やせたなら
    /// FinalRelease はまだ走っておらず、この借用が閉じるまで走らない。**別スレッドの明示
    /// Dispose** (presenter の掃き出しが典型) が進行中の呼び出しを壊さないのはこのためである。
    /// </para>
    /// </remarks>
    internal Borrowed Borrow()
    {
        int state = Volatile.Read(ref _state);
        while (true)
        {
            ObjectDisposedException.ThrowIf((state & DisposedFlag) != 0, this);
            int seen = Interlocked.CompareExchange(ref _state, state + 1, state);
            if (seen == state)
            {
                return new Borrowed(this);
            }
            state = seen;
        }
    }

    /// <summary>COM 要素の借用スコープ。閉じるまで持ち主は回収されず、解放も遅延される。</summary>
    internal readonly ref struct Borrowed
    {
        private readonly UiaElement _owner;

        internal Borrowed(UiaElement owner)
        {
            _owner = owner;
            // Borrow() が借用数を増やしてから来るので、ここで _element が null になる遷移は無い
            // (解放は「フラグ確定 かつ 借用 0」でしか起きない)
            Element = owner._element!;
        }

        /// <summary>借りている COM 要素。スコープの外へ持ち出さないこと。</summary>
        public IUIAutomationElement Element { get; }

        /// <summary>
        /// 持ち主の生存をここまで引き延ばし、遅延された解放があれば実行する。
        /// </summary>
        /// <remarks>
        /// <see cref="GC.KeepAlive"/> が**この型の存在理由そのもの**である。スタック上の構造体が
        /// 持ち主への参照を持つので GC のルートになるが、それも「最後に使うまで」でしかない。
        /// ここで明示的に触ることで、その最後をスコープの終わりに固定する。
        /// </remarks>
        public void Dispose()
        {
            _owner.EndBorrow();
            GC.KeepAlive(_owner);
        }
    }
}
