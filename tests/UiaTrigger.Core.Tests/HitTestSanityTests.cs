// ヒットテストの整合性 — docs/DESIGN.md §9。
//
// <b>これは実測から出た検査である。</b>デスクトップのアイコンの上で
// UIA のヒットテストが<b>その点を含まない要素</b>を返すことを実測した
// (点 (150,47) に対して (0,5)-(100,70) の「ごみ箱」)。ピッカーはそれを忠実に表示するので、
// カーソルを隣のアイコンへ動かしても選択が変わらない。
//
// <b>UiaSession の側は T1 から回せない</b> (実 UIA が要る)。ここで固定できるのは
// 「矛盾をどう判定するか」= 矩形が点を含むかどうかの規則だけである。
// 実際に引き直す経路そのものは T3 / 実機の担当で、判断は docs/DESIGN.md §9 にある。
using UiaTrigger.Models;
using Xunit;

namespace UiaTrigger.Tests;

public sealed class HitTestSanityTests
{
    /// <summary>
    /// 矩形は<b>左上を含み、右下を含まない</b> (Win32 の半開区間)。
    /// </summary>
    /// <remarks>
    /// <b>辺の扱いがここで効く。</b>デスクトップのアイコンは実測で
    /// (0,5)-(100,70) と (100,5)-(200,90) のように<b>辺を共有して並ぶ</b>。
    /// 閉区間にすると x=100 を両方が「含む」と答え、
    /// 「返ってきた要素はその点を含んでいるか」という問い自体が答えを失う。
    /// </remarks>
    [Theory]
    // 中
    [InlineData(50, 40, true)]
    // 左上の角は含む / 右下の角は含まない
    [InlineData(0, 5, true)]
    [InlineData(100, 70, false)]
    // 辺を共有する隣の矩形の側
    [InlineData(100, 40, false)]
    [InlineData(99, 40, true)]
    // 実測した嘘の答え: 「ごみ箱」の矩形は隣のアイコンの中心を含まない
    [InlineData(150, 47, false)]
    public void TheRecycleBinRectangle_ContainsOnlyItsOwnPoints(int x, int y, bool expected)
    {
        var recycleBin = new ElementRect(0, 5, 100, 70);

        Assert.Equal(expected, recycleBin.Contains(x, y));
    }

    /// <summary>
    /// 隣のアイコンの矩形は、その中心を含む。
    /// </summary>
    /// <remarks>
    /// 上の検査と<b>対で意味を持つ</b>。「ごみ箱が含まない」だけでは、
    /// 引き直す先が正しいことを何も言っていない。
    /// </remarks>
    [Fact]
    public void TheNeighbourRectangle_ContainsThePointTheHitTestWasAskedAbout()
    {
        var neighbour = new ElementRect(100, 5, 200, 90);

        Assert.True(neighbour.Contains(150, 47));
    }
}
