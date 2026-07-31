using System.Runtime.InteropServices;
using System.Runtime.InteropServices.Marshalling;

namespace UiaTrigger.Interop;

/// <summary>
/// CUIAutomation8 の生成と、生の IUnknown ポインタから RCW への変換を担う。
///
/// RCW には 2 系統ある (docs/DESIGN.md B6/B7):
///
/// - <b>共有 RCW</b> (<see cref="WrapShared{T}"/>) — ComWrappers の同一性テーブルに載る。
///   UiaFactory が独自の <see cref="StrategyBasedComWrappers"/> を持つと、生成された
///   marshalling stub は別のインスタンスを使うため同一性テーブルが 2 系統に分裂する。
///   <see cref="ComInterfaceMarshaller{T}"/> 経由にすることで stub と同じ
///   インスタンスに揃う (B7)。
/// - <b>一意 RCW</b> (<see cref="WrapUnique{T}"/>) — 同一性テーブルに載らないので
///   <see cref="ReleaseUnique"/> で決定的に解放できる (B6)。解決ループが 1 段あたり数百作る
///   要素はこちらにして、GC を待たずに相手プロセスの provider を手放す。
///   <b>共有 RCW を <see cref="ReleaseUnique"/> に渡してはならない</b> — テーブルに解放済みの
///   エントリが残り、同じアドレスが再利用されたときに死んだ RCW が返ってくる。
/// </summary>
internal static partial class UiaFactory
{
    private static readonly Guid ClsidCUIAutomation8 = new("E22AD333-B25F-460C-83D0-0581107395C9");
    private static readonly Guid IidIUIAutomation = new("30CBE57D-D9D0-452A-AB13-7AC5AC4825EE");

    /// <summary>一意インスタンス専用。同一性テーブルを使わないので stub と分裂しない。</summary>
    private static readonly StrategyBasedComWrappers UniqueWrappers = new();

    [LibraryImport("ole32.dll")]
    private static partial int CoCreateInstance(in Guid rclsid, nint pUnkOuter, uint dwClsContext, in Guid riid, out nint ppv);

    public static IUIAutomation CreateAutomation()
    {
        const uint CLSCTX_INPROC_SERVER = 0x1;
        Marshal.ThrowExceptionForHR(CoCreateInstance(ClsidCUIAutomation8, 0, CLSCTX_INPROC_SERVER, IidIUIAutomation, out nint ptr));
        return WrapShared<IUIAutomation>(ptr)
            ?? throw new InvalidOperationException("CoCreateInstance(CUIAutomation8) returned a null interface pointer.");
    }

    /// <summary>
    /// AddRef 済みの IUnknown ポインタを共有 RCW にして、渡された参照を解放する。
    /// 生成された marshalling stub と同じ ComWrappers インスタンスを使う。
    /// </summary>
    public static unsafe T? WrapShared<T>(nint punk) where T : class
    {
        if (punk == 0)
        {
            return null;
        }
        try
        {
            // ConvertToManaged は内部で AddRef するため、こちらの参照は手放してよい
            return ComInterfaceMarshaller<T>.ConvertToManaged((void*)punk);
        }
        finally
        {
            Marshal.Release(punk);
        }
    }

    /// <summary>
    /// AddRef 済みの IUnknown ポインタを一意 RCW にして、渡された参照を解放する。
    /// 戻り値は <see cref="ReleaseUnique"/> で決定的に解放できる。
    /// </summary>
    public static T? WrapUnique<T>(nint punk) where T : class
    {
        if (punk == 0)
        {
            return null;
        }
        try
        {
            return (T)UniqueWrappers.GetOrCreateObjectForComInstance(punk, CreateObjectFlags.UniqueInstance);
        }
        finally
        {
            Marshal.Release(punk);
        }
    }

    /// <summary>
    /// 「成功したら必ず値が返る」COM 呼び出し用の <see cref="WrapUnique{T}"/>。
    /// それでも 0 が返ったら要素が消えたものとして扱い、呼び出し側が既に扱えている
    /// COMException の経路へ合流させる。
    /// </summary>
    public static T WrapUniqueRequired<T>(nint punk) where T : class
        => WrapUnique<T>(punk) ?? throw Marshal.GetExceptionForHR(ComErrors.ElementNotAvailable)!;

    /// <summary>
    /// <see cref="WrapUnique{T}"/> が返した RCW を決定的に解放する。
    /// 解放後のオブジェクトは二度と使えない。共有 RCW には使わないこと。
    /// </summary>
    public static void ReleaseUnique(object? rcw)
    {
        if (rcw is ComObject com)
        {
            com.FinalRelease();
        }
    }
}
