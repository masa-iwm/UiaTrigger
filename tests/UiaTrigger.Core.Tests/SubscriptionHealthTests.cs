using UiaTrigger.Monitoring;
using Xunit;

namespace UiaTrigger.Tests;

/// <summary>
/// 「購読を失った」の判定の回帰テスト (docs/DESIGN.md §8)。
///
/// <para>
/// この規則が**緩いほうへ**壊れると、まだ起動していないアプリのトリガーが常に
/// 「壊れている」と判定され、復旧の再試行が回りっぱなしになる。
/// **そうなっても機能は動いたままなので、他のテストは 1 件も落ちない** —
/// 「イベント駆動でポーリングしない」という設計の売りだけが静かに消える。
/// だから表で 4 つの場合すべてを固定する。
/// </para>
/// <para>
/// 逆に**厳しいほうへ**壊れると、購読を失ったトリガーが二度と拾われない
/// (docs/DESIGN.md §8 の閉路そのもの。CI で実際に観測された)。こちらは T3 でしか出ない。
/// </para>
/// </summary>
public sealed class SubscriptionHealthTests
{
    private const nint SomeWindow = 0x1234;

    /// <summary>4 つの場合。**孤立と言えるのは 1 つだけである。**</summary>
    public static TheoryData<string, int, nint, bool> Cases =>
    [
        ("解決済み・経路購読 (2 段)", 2, SomeWindow, false),
        ("解決済み・対象がウィンドウ自身 (経路が空)", 0, 0, false),
        ("未解決・アプリがまだ起動していない", 0, 0, false),
        ("未解決・ウィンドウはあるのに購読できなかった", 0, SomeWindow, true),
    ];

    [Theory]
    [MemberData(nameof(Cases))]
    public void IsOrphaned_OnlyWhenAWindowWasFoundButNothingCouldBeSubscribed(
        string because, int subscribedElements, nint windowHandle, bool expected)
    {
        Assert.Equal(expected, SubscriptionHealth.IsOrphaned(subscribedElements, windowHandle));
        Assert.NotEmpty(because);
    }

    /// <summary>
    /// 購読を 1 件でも持っていれば、ウィンドウの有無にかかわらず孤立ではないこと。
    ///
    /// 「0 件かどうか」が先に来る条件であることを、ハンドル側を動かして固定する。
    /// </summary>
    [Theory]
    [InlineData(1)]
    [InlineData(5)]
    public void IsOrphaned_IsNeverTrueWhileSomethingIsSubscribed(int subscribedElements)
    {
        Assert.False(SubscriptionHealth.IsOrphaned(subscribedElements, SomeWindow));
        Assert.False(SubscriptionHealth.IsOrphaned(subscribedElements, 0));
    }
}
