using System.Diagnostics.CodeAnalysis;
using System.Runtime.InteropServices;
using UiaTrigger.Interop;
using Xunit;

namespace UiaTrigger.Tests;

/// <summary>
/// COMException の判別の回帰テスト (docs/DESIGN.md A11)。
///
/// UIA_E_ELEMENTNOTAVAILABLE だけを「要素消滅」として扱い、
/// 相手プロセスが落ちたときに返る RPC 系の HRESULT を「一時的なエラー」として無視すると、
/// 死んだ要素を掴んだまま Removed 判定も再解決も走らなくなる。
/// </summary>
public sealed class ComErrorsTests
{
    public static TheoryData<int, string> GoneHResults() => new()
    {
        { ComErrors.ElementNotAvailable, "UIA_E_ELEMENTNOTAVAILABLE" },
        { ComErrors.ObjectNotConnected, "CO_E_OBJNOTCONNECTED" },
        { ComErrors.RpcDisconnected, "RPC_E_DISCONNECTED" },
        { ComErrors.RpcServerUnavailable, "RPC_S_SERVER_UNAVAILABLE" },
        { ComErrors.RpcCallFailed, "RPC_S_CALL_FAILED" },
        { ComErrors.InvalidWindowHandle, "ERROR_INVALID_WINDOW_HANDLE" },
    };

    public static TheoryData<int, string> RecoverableHResults() => new()
    {
        { ComErrors.Timeout, "UIA_E_TIMEOUT (プロバイダーが遅いだけで要素は生きている)" },
        { ComErrors.NoClickablePoint, "UIA_E_NOCLICKABLEPOINT" },
        { unchecked((int)0x80004005), "E_FAIL" },
        { unchecked((int)0x80070005), "E_ACCESSDENIED" },
        { unchecked((int)0x80004003), "E_POINTER" },
        { 0, "S_OK" },
    };

    [Theory]
    [MemberData(nameof(GoneHResults))]
    public void IsElementGone_IsTrue_ForDisappearedElement(int hresult, string name)
    {
        Assert.True(ComErrors.IsElementGone(hresult), $"{name} は要素消滅として扱うこと");
        Assert.True(ComErrors.IsElementGone(MakeComException(hresult, name)));
    }

    [Theory]
    [MemberData(nameof(RecoverableHResults))]
    public void IsElementGone_IsFalse_ForRecoverableErrors(int hresult, string name)
    {
        Assert.False(ComErrors.IsElementGone(hresult), $"{name} で要素を手放してはならない");
        Assert.False(ComErrors.IsElementGone(MakeComException(hresult, name)));
    }

    /// <summary>
    /// 定数が UIAutomationClient.h / winerror.h の値と一致していること。
    /// ここを取り違えると判別が静かに効かなくなるため、値そのものを固定する。
    /// </summary>
    [Fact]
    public void Constants_MatchTheDocumentedHResultValues()
    {
        Assert.Equal(unchecked((int)0x80040201), ComErrors.ElementNotAvailable);
        Assert.Equal(unchecked((int)0x80040202), ComErrors.NoClickablePoint);
        Assert.Equal(unchecked((int)0x80131505), ComErrors.Timeout);
        Assert.Equal(unchecked((int)0x800401FD), ComErrors.ObjectNotConnected);
        Assert.Equal(unchecked((int)0x80010108), ComErrors.RpcDisconnected);
        Assert.Equal(unchecked((int)0x800706BA), ComErrors.RpcServerUnavailable);
        Assert.Equal(unchecked((int)0x800706BE), ComErrors.RpcCallFailed);
        Assert.Equal(unchecked((int)0x80070578), ComErrors.InvalidWindowHandle);
    }

    [SuppressMessage("Usage", "CA2201:Do not raise reserved exception types",
        Justification = "判別対象そのものを組み立てるテストのため、実物の COMException が必要。")]
    private static COMException MakeComException(int hresult, string name) => new(name, hresult);

    [Fact]
    public void IsElementGone_Throws_ForNullException()
        => Assert.Throws<ArgumentNullException>(() => ComErrors.IsElementGone((COMException)null!));
}
