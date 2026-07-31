// COM / UIA の HRESULT の判別 (docs/DESIGN.md A11)。
//
// UIA_E_ELEMENTNOTAVAILABLE だけを見て他の COMException を一括で握り潰してはいけない。
// 「相手プロセスが落ちた」「ウィンドウが破棄された」場合には RPC 系の HRESULT が返ることが
// あり、それを一時的なエラーとして無視すると、死んだ要素を掴んだまま Removed 判定も
// 再解決も走らなくなる。
using System.Runtime.InteropServices;

namespace UiaTrigger.Interop;

internal static class ComErrors
{
    /// <summary>UIA_E_ELEMENTNOTAVAILABLE — 要素が既に存在しない。</summary>
    public const int ElementNotAvailable = unchecked((int)0x80040201);

    /// <summary>UIA_E_NOCLICKABLEPOINT。</summary>
    public const int NoClickablePoint = unchecked((int)0x80040202);

    /// <summary>UIA_E_TIMEOUT — プロバイダーが応答しない (要素は生きている可能性がある)。</summary>
    public const int Timeout = unchecked((int)0x80131505);

    /// <summary>CO_E_OBJNOTCONNECTED — プロキシの接続先が消えた。</summary>
    public const int ObjectNotConnected = unchecked((int)0x800401FD);

    /// <summary>RPC_E_DISCONNECTED — 相手のオブジェクトが切断された。</summary>
    public const int RpcDisconnected = unchecked((int)0x80010108);

    /// <summary>HRESULT_FROM_WIN32(RPC_S_SERVER_UNAVAILABLE) — 相手プロセスが消えた。</summary>
    public const int RpcServerUnavailable = unchecked((int)0x800706BA);

    /// <summary>HRESULT_FROM_WIN32(RPC_S_CALL_FAILED) — 呼び出し中に相手が落ちた。</summary>
    public const int RpcCallFailed = unchecked((int)0x800706BE);

    /// <summary>HRESULT_FROM_WIN32(ERROR_INVALID_WINDOW_HANDLE) — HWND が既に無効。</summary>
    public const int InvalidWindowHandle = unchecked((int)0x80070578);

    /// <summary>
    /// 「対象の要素 (またはそれを提供するプロセス/ウィンドウ) が消滅した」ことを示す
    /// HRESULT かどうか。true なら再試行しても回復しないため、要素を手放して再解決に回す。
    /// </summary>
    public static bool IsElementGone(int hresult) => hresult switch
    {
        ElementNotAvailable => true,
        ObjectNotConnected => true,
        RpcDisconnected => true,
        RpcServerUnavailable => true,
        RpcCallFailed => true,
        InvalidWindowHandle => true,
        _ => false,
    };

    /// <inheritdoc cref="IsElementGone(int)"/>
    public static bool IsElementGone(COMException exception)
    {
        ArgumentNullException.ThrowIfNull(exception);
        return IsElementGone(exception.HResult);
    }
}
