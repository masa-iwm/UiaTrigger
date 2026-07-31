using UiaTrigger.Models;
using UiaTrigger.Picker;
using Xunit;

namespace UiaTrigger.Tests;

/// <summary>
/// オーバーレイの幾何と描画をピクセル単位で固定する (docs/TESTING.md §1 T1)。
///
/// 確定アイコンの位置は目視で決めた値である。ここで固定しておかないと、
/// 「動くが位置がずれている」という、コンパイルもテストも通ってしまう壊れ方に戻る。
///
/// 寸法は DPI でスケールする (docs/DESIGN.md §9)。
/// **この一式は dpi を明示的に渡す** — 走っている機械の表示スケールを見にいくと、
/// 96 の機械では緑・175% の機械では赤 (逆もある) という再現しないテストになる。
///
/// **絵は 2 枚である (docs/DESIGN.md §10)。**枠 (<c>PaintFrame</c>) とアイコン (<c>PaintIcon</c>) は
/// 別のウィンドウへ流れる。枠の窓は <c>WS_EX_TRANSPARENT</c> でヒットテストから外れており、
/// アイコンの窓は全ピクセルが不透明でその矩形がそのまま当たり判定である。
/// **この一式が守るのはその 2 つの性質である** — 枠の絵にアイコンの色が混じったら
/// 「見えているのに押せない」に戻るし、アイコンの絵に透過が混じったらそこだけ押せなくなる。
/// </summary>
public sealed class OverlayGeometryTests
{
    /// <summary>この開発機に依らない基準 DPI。基準の見た目がここに固定されている。</summary>
    private const int Dpi96 = 96;

    /// <summary>175% (実機の 4K 環境)。96 * 1.75 = 168。</summary>
    private const int Dpi175 = 168;

    private static uint[] PaintFrame(ElementRect rect, int dpi)
    {
        (int width, int height) = OverlayGeometry.FrameSize(rect, dpi);
        var pixels = new uint[width * height];
        OverlayGeometry.PaintFrame(rect, dpi, pixels);
        return pixels;
    }

    private static uint[] PaintIcon(int dpi)
    {
        int size = OverlayGeometry.MetricsFor(dpi).IconSize;
        var pixels = new uint[size * size];
        OverlayGeometry.PaintIcon(dpi, pixels);
        return pixels;
    }

    /// <summary>枠の絵の 1 ピクセル。座標は**枠の窓の中** (要素の左上が原点)。</summary>
    private static uint FrameAt(ElementRect rect, int dpi, uint[] pixels, int x, int y)
    {
        (int width, _) = OverlayGeometry.FrameSize(rect, dpi);
        return pixels[(y * width) + x];
    }

    // ---- 実寸と、それを縛る不変条件 ----

    /// <summary>
    /// 実寸を絶対値で固定する。
    ///
    /// これらは目視で決めた値である。
    /// **他のテストをすべて定数からの相対で書くと、定数を変えてもテストが一緒に動いてしまい
    /// 何も守れない**。ここだけは数値そのものを書く。
    /// 意図して見た目を変えるときは、この期待値を明示的に更新すること。
    ///
    /// **96 DPI でこの基準値に一致することが、DPI スケーリング (docs/DESIGN.md §9) が 96 DPI の見た目を変えないことの証明である。**
    /// </summary>
    [Fact]
    public void Constants_AreTheValuesSettledOnByEye()
    {
        Assert.Equal(3, OverlayGeometry.FrameThicknessAt96);
        Assert.Equal(20, OverlayGeometry.IconSizeAt96);
        Assert.Equal(10, OverlayGeometry.IconHalfAt96);
        Assert.Equal(4, OverlayGeometry.IconOutsideAt96);
        Assert.Equal(6, OverlayGeometry.IconInsetAt96);

        // 96 DPI での実寸は、上の定数そのもの
        OverlayGeometry.Metrics m = OverlayGeometry.MetricsFor(Dpi96);
        Assert.Equal(3, m.FrameThickness);
        Assert.Equal(20, m.IconSize);
        Assert.Equal(10, m.IconHalf);
        Assert.Equal(4, m.IconOutside);
        Assert.Equal(6, m.IconInset);
    }

    /// <summary>175% での実寸を絶対値で固定する (実機がこのスケールである)。</summary>
    [Fact]
    public void Metrics_At175Percent_AreTheScaledValues()
    {
        OverlayGeometry.Metrics m = OverlayGeometry.MetricsFor(Dpi175);

        Assert.Equal(5, m.FrameThickness);   // 3 * 1.75 = 5.25
        Assert.Equal(36, m.IconSize);        // IconHalf(18) * 2。20 を直接スケールした 35 は通らない
        Assert.Equal(18, m.IconHalf);        // 10 * 1.75 = 17.5 → 18
        Assert.Equal(7, m.IconOutside);      // 4 * 1.75 = 7
        Assert.Equal(11, m.IconInset);       // 18 - 7
    }

    /// <summary>
    /// **当たり判定と描画を結び付けている不変条件。**
    /// </summary>
    /// <remarks>
    /// <para>
    /// アイコンの窓は一辺 <c>IconSize</c> で、その矩形がそのまま当たり判定になる。
    /// <c>IconRect</c> は「中心から <c>IconHalf</c> で広がる」式から導いているので、
    /// 一辺が偶数 (<c>IconSize == IconHalf * 2</c>) でないと絵と当たり判定が 1px ずれる。
    /// **4 つの定数を別々にスケールすると、これが中途半端な DPI で崩れる** —
    /// 175% なら 20 * 1.75 = 35 (奇数) になる。
    /// </para>
    /// <para>
    /// 中途半端な DPI (110 / 125 / 137) をわざと混ぜてある。丸めが独立に効く実装だと
    /// ここで落ちる (dpi=110: <c>IconInset</c> を直接スケールすると 6、導出すると 7)。
    /// </para>
    /// </remarks>
    [Theory]
    [InlineData(96)]    // 100%
    [InlineData(110)]   // 端数
    [InlineData(120)]   // 125%
    [InlineData(125)]   // 端数
    [InlineData(137)]   // 端数
    [InlineData(144)]   // 150%
    [InlineData(168)]   // 175%
    [InlineData(192)]   // 200%
    [InlineData(240)]   // 250%
    [InlineData(384)]   // 400%
    public void Metrics_KeepTheInvariantsThatTieTheHitZoneToTheDrawing(int dpi)
    {
        OverlayGeometry.Metrics m = OverlayGeometry.MetricsFor(dpi);

        // 不変条件 1: 一辺は必ず偶数 (半分の 2 倍)
        Assert.Equal(m.IconHalf * 2, m.IconSize);
        // 不変条件 2: インセットは導出であり、独立に丸めない
        Assert.Equal(m.IconHalf - m.IconOutside, m.IconInset);
        // アイコンが枠から完全に外れない / 枠線が消えない
        Assert.True(m.IconInset >= 1, $"IconInset={m.IconInset} (dpi={dpi})");
        Assert.True(m.IconOutside >= 1, $"IconOutside={m.IconOutside} (dpi={dpi})");
        Assert.True(m.FrameThickness >= 1, $"FrameThickness={m.FrameThickness} (dpi={dpi})");
    }

    /// <summary>
    /// **スケールしていること自体**を固定する。これが無いと「dpi を受け取るが無視する」
    /// 実装 (= 元の不具合) が緑のまま通る。
    /// </summary>
    [Fact]
    public void Metrics_GrowWithTheDpi()
    {
        OverlayGeometry.Metrics at96 = OverlayGeometry.MetricsFor(Dpi96);
        OverlayGeometry.Metrics at175 = OverlayGeometry.MetricsFor(Dpi175);
        OverlayGeometry.Metrics at200 = OverlayGeometry.MetricsFor(192);

        Assert.True(at175.IconSize > at96.IconSize);
        Assert.True(at175.FrameThickness > at96.FrameThickness);
        // 200% はちょうど 2 倍 (丸めが挟まらない)
        Assert.Equal(at96.IconSize * 2, at200.IconSize);
        Assert.Equal(at96.FrameThickness * 2, at200.FrameThickness);
        Assert.Equal(at96.IconOutside * 2, at200.IconOutside);
    }

    [Fact]
    public void MetricsFor_WithANonPositiveDpi_Throws()
        => Assert.Throws<ArgumentOutOfRangeException>(() => OverlayGeometry.MetricsFor(0));

    // ---- 幾何: 2 つの窓 ----

    /// <summary>
    /// 2 つの窓の絶対座標 (200x120 の要素、96 DPI)。
    /// </summary>
    /// <remarks>
    /// 枠 200x120 を (0,0) に、アイコン 20x20 を (184,-4) に置く。
    /// 2 枚を合わせた外周は、1 枚のビットマップ 204x124 を (0,-4) に置いた形と同じ —
    /// 窓を 2 枚に割っても**見た目は変わらない** (docs/DESIGN.md §10)。
    /// </remarks>
    [Fact]
    public void TheTwoWindows_SitAtExactPixelPositions()
    {
        var rect = new ElementRect(0, 0, 200, 120);

        Assert.Equal((200, 120), OverlayGeometry.FrameSize(rect, Dpi96));
        Assert.Equal((0, 0), OverlayGeometry.FrameOrigin(rect));
        // アイコンは枠の右上。右へ 4px、上へ 4px はみ出す
        Assert.Equal((184, -4, 20), OverlayGeometry.IconRect(rect, Dpi96));
    }

    /// <summary>同じ要素を 175% で。**絶対値で書く** — 相対で書くと何も守れない。</summary>
    [Fact]
    public void TheTwoWindows_At175Percent_SitAtExactPixelPositions()
    {
        var rect = new ElementRect(0, 0, 200, 120);

        // 枠は要素そのものなので DPI で変わらない
        Assert.Equal((200, 120), OverlayGeometry.FrameSize(rect, Dpi175));
        Assert.Equal((0, 0), OverlayGeometry.FrameOrigin(rect));
        // はみ出しが 4 → 7、一辺が 20 → 36
        Assert.Equal((171, -7, 36), OverlayGeometry.IconRect(rect, Dpi175));
    }

    /// <summary>当たり判定の絶対座標 (96 DPI)。中心は枠の右上隅から内側へ 6px。</summary>
    [Fact]
    public void IsInIconZone_CoversAnExactScreenRectangle()
    {
        var rect = new ElementRect(100, 100, 400, 300);

        // 中心 (394, 106) を中心とした 20x20 = x:384..403 / y:96..115
        Assert.True(OverlayGeometry.IsInIconZone(rect, Dpi96, 394, 106));
        Assert.True(OverlayGeometry.IsInIconZone(rect, Dpi96, 384, 96));
        Assert.True(OverlayGeometry.IsInIconZone(rect, Dpi96, 403, 115));
        Assert.False(OverlayGeometry.IsInIconZone(rect, Dpi96, 383, 106));
        Assert.False(OverlayGeometry.IsInIconZone(rect, Dpi96, 404, 106));
        Assert.False(OverlayGeometry.IsInIconZone(rect, Dpi96, 394, 95));
        Assert.False(OverlayGeometry.IsInIconZone(rect, Dpi96, 394, 116));

        // 当たり判定はアイコンの窓の矩形そのものである
        Assert.Equal((384, 96, 20), OverlayGeometry.IconRect(rect, Dpi96));
    }

    /// <summary>
    /// 同じ矩形を 175% で。**当たり判定は要素の右上に貼り付いたまま、領域だけが広がる。**
    /// </summary>
    [Fact]
    public void IsInIconZone_At175Percent_CoversTheLargerRectangle()
    {
        var rect = new ElementRect(100, 100, 400, 300);

        // 中心 (389, 111) / 36x36 = x:371..406 / y:93..128
        Assert.True(OverlayGeometry.IsInIconZone(rect, Dpi175, 389, 111));
        Assert.True(OverlayGeometry.IsInIconZone(rect, Dpi175, 371, 93));
        Assert.True(OverlayGeometry.IsInIconZone(rect, Dpi175, 406, 128));
        Assert.False(OverlayGeometry.IsInIconZone(rect, Dpi175, 370, 111));
        Assert.False(OverlayGeometry.IsInIconZone(rect, Dpi175, 407, 111));
        Assert.False(OverlayGeometry.IsInIconZone(rect, Dpi175, 389, 92));
        Assert.False(OverlayGeometry.IsInIconZone(rect, Dpi175, 389, 129));

        // 96 DPI では入っていた点が、175% では絵が大きいぶん**まだ**入っている /
        // 96 DPI で外れていた点が入るようになる — スケールが効いている証拠
        Assert.False(OverlayGeometry.IsInIconZone(rect, Dpi96, 371, 93));
        Assert.True(OverlayGeometry.IsInIconZone(rect, Dpi175, 371, 93));
    }

    /// <summary>
    /// **見えているアイコンと、押せるアイコンが 1 ピクセルも食い違わないこと。**
    /// </summary>
    /// <remarks>
    /// <para>
    /// これがこの一式で最も強い検査である。**3 つを同時に主張する**:
    /// (a) アイコンの絵は 1 ピクセルも欠けずに不透明である
    /// — レイヤードウィンドウのヒットテストはピクセルごとのアルファで決まるので、
    /// 透過が 1 点でも混じればそこは押せない。
    /// (b) アイコンの窓が占めるスクリーン座標は、ちょうど当たり判定の内側である
    /// (窓の外へ 1px 出れば当たり判定の外)。
    /// (c) **枠の絵にはアイコンの色が 1 ピクセルも無い。**
    /// </para>
    /// <para>
    /// (c) が 2 枚構造 (docs/DESIGN.md §10) の要である。枠の窓は <c>WS_EX_TRANSPARENT</c> で**窓ごと**
    /// ヒットテストから外れているので、そちらに描いた絵は**どんなに不透明でも押せない**。
    /// 2 枚を 1 枚に戻す変更 (= 元の設計) はここで落ちる。
    /// </para>
    /// <para>
    /// 端数の DPI を混ぜてあるのは、丸めのずれがここに出るからである。
    /// <c>IconSize</c> を直接スケールして奇数になった実装は、この検査で必ず落ちる。
    /// </para>
    /// <para>
    /// **アイコンより小さい要素も見る。**枠は最低アイコン 1 個分まで広げられ、
    /// アイコンは**広げたあとの**枠の右上に来る。当たり判定が**広げる前の**
    /// <c>rect.Right</c> を見る実装は、ここで右側の帯が
    /// 「描かれているのに押せない」になって落ちる (docs/DESIGN.md §9)。
    /// 96 DPI でも落ちるので、DPI とは独立の検査である。
    /// </para>
    /// </remarks>
    [Theory]
    // 要素がアイコンより大きい (通常)
    [InlineData(96, 300, 200)]
    [InlineData(110, 300, 200)]
    [InlineData(120, 300, 200)]
    [InlineData(125, 300, 200)]
    [InlineData(137, 300, 200)]
    [InlineData(144, 300, 200)]
    [InlineData(168, 300, 200)]
    [InlineData(192, 300, 200)]
    [InlineData(240, 300, 200)]
    // 要素がアイコンより小さい (枠が広げられる)
    [InlineData(96, 10, 10)]
    [InlineData(96, 1, 1)]
    [InlineData(168, 10, 10)]
    [InlineData(168, 30, 8)]    // 175% の IconSize(36) より狭く、高さだけ極端に低い
    [InlineData(240, 4, 40)]    // 幅だけ足りない
    public void TheIconThatIsDrawn_IsExactlyTheIconThatCanBeClicked(int dpi, int width, int height)
    {
        var rect = new ElementRect(100, 100, 100 + width, 100 + height);
        uint[] icon = PaintIcon(dpi);
        (int iconX, int iconY, int size) = OverlayGeometry.IconRect(rect, dpi);

        // (a)(b) 窓の中は全ピクセル不透明で、すべて当たり判定の内側である
        for (int y = 0; y < size; y++)
        {
            for (int x = 0; x < size; x++)
            {
                uint pixel = icon[(y * size) + x];
                Assert.True(
                    pixel == OverlayGeometry.IconBackColor || pixel == OverlayGeometry.IconMarkColor,
                    $"dpi={dpi} アイコン({x},{y}) が不透明ではありません (0x{pixel:X8})。" +
                    "レイヤードウィンドウではその点だけ押せなくなります。");
                Assert.True(
                    OverlayGeometry.IsInIconZone(rect, dpi, iconX + x, iconY + y),
                    $"dpi={dpi} 要素={width}x{height} アイコン({x},{y}) " +
                    $"スクリーン({iconX + x},{iconY + y}): 描かれているのに当たり判定の外です。");
            }
        }

        // (b) 窓の外側 1 ピクセルの縁は当たり判定の外である
        for (int y = -1; y <= size; y++)
        {
            for (int x = -1; x <= size; x++)
            {
                if (x >= 0 && x < size && y >= 0 && y < size)
                {
                    continue;
                }
                Assert.False(
                    OverlayGeometry.IsInIconZone(rect, dpi, iconX + x, iconY + y),
                    $"dpi={dpi} 要素={width}x{height} " +
                    $"スクリーン({iconX + x},{iconY + y}): 窓の外なのに当たり判定の内側です。");
            }
        }

        // (c) 枠の絵にはアイコンの色が 1 ピクセルも無い
        uint[] frame = PaintFrame(rect, dpi);
        Assert.DoesNotContain(OverlayGeometry.IconBackColor, frame);
        Assert.DoesNotContain(OverlayGeometry.IconMarkColor, frame);
    }

    [Fact]
    public void FrameSize_IsTheElementItself()
    {
        var rect = new ElementRect(100, 200, 400, 300);

        Assert.Equal((300, 100), OverlayGeometry.FrameSize(rect, Dpi96));
        Assert.Equal((100, 200), OverlayGeometry.FrameOrigin(rect));
    }

    /// <summary>要素がアイコンより小さくても、枠はアイコン 1 個分を確保すること。</summary>
    [Theory]
    [InlineData(96)]
    [InlineData(168)]
    public void FrameSize_ForATinyElement_IsAtLeastOneIcon(int dpi)
    {
        var rect = new ElementRect(0, 0, 4, 4);
        int iconSize = OverlayGeometry.MetricsFor(dpi).IconSize;

        Assert.Equal((iconSize, iconSize), OverlayGeometry.FrameSize(rect, dpi));
    }

    /// <summary>アイコンは枠の右上から、右と上へ <c>IconOutside</c> だけはみ出すこと。</summary>
    [Theory]
    [InlineData(96)]
    [InlineData(168)]
    public void IconRect_OverhangsTheFrameCornerByTheOverhang(int dpi)
    {
        var rect = new ElementRect(100, 200, 400, 300);
        OverlayGeometry.Metrics m = OverlayGeometry.MetricsFor(dpi);
        (int frameWidth, _) = OverlayGeometry.FrameSize(rect, dpi);
        (int x, int y, int size) = OverlayGeometry.IconRect(rect, dpi);

        // 右端は枠の右端より IconOutside だけ外
        Assert.Equal(rect.Left + frameWidth + m.IconOutside, x + size);
        // 上端は要素の上端より IconOutside だけ上
        Assert.Equal(rect.Top - m.IconOutside, y);
        // はみ出しは一辺の 1/5 前後に収まっている (目視で 2 度調整した比である)
        Assert.InRange(m.IconOutside, (size / 5) - 1, (size / 5) + 1);
    }

    // ---- 描画 ----

    [Theory]
    [InlineData(96)]
    [InlineData(168)]
    public void PaintFrame_DrawsTheFrameEdgesAndLeavesTheInteriorTransparent(int dpi)
    {
        var rect = new ElementRect(0, 0, 200, 120);
        uint[] pixels = PaintFrame(rect, dpi);
        OverlayGeometry.Metrics m = OverlayGeometry.MetricsFor(dpi);

        // 4 辺。枠の窓の原点は要素の左上そのものなので、ずらす量は無い
        Assert.Equal(OverlayGeometry.FrameColor, FrameAt(rect, dpi, pixels, 0, 0));
        Assert.Equal(OverlayGeometry.FrameColor, FrameAt(rect, dpi, pixels, 0, 119));
        Assert.Equal(OverlayGeometry.FrameColor, FrameAt(rect, dpi, pixels, 199, 60));
        Assert.Equal(OverlayGeometry.FrameColor, FrameAt(rect, dpi, pixels, 100, 119));
        // 太さちょうど
        Assert.Equal(OverlayGeometry.FrameColor, FrameAt(rect, dpi, pixels, m.FrameThickness - 1, 60));
        Assert.Equal(OverlayGeometry.Transparent, FrameAt(rect, dpi, pixels, m.FrameThickness, 60));
        // 内側は透過 (アルファでクリックが下へ抜ける — docs/DESIGN.md §10)
        Assert.Equal(OverlayGeometry.Transparent, FrameAt(rect, dpi, pixels, 100, 60));
    }

    /// <summary>
    /// **枠の絵は枠線と透過だけでできていること。**
    /// </summary>
    /// <remarks>
    /// アイコンを枠側へ描き戻す変更 (= 1 枚に戻す設計 — docs/DESIGN.md §10) はここで落ちる。
    /// 枠の窓は <c>WS_EX_TRANSPARENT</c> なので、そこに描いた確定アイコンは**押せない**。
    /// </remarks>
    [Theory]
    [InlineData(96)]
    [InlineData(168)]
    public void PaintFrame_UsesNoColourOtherThanTheFrameLine(int dpi)
    {
        var rect = new ElementRect(0, 0, 200, 120);

        uint[] pixels = PaintFrame(rect, dpi);

        Assert.All(
            pixels,
            p => Assert.True(
                p == OverlayGeometry.FrameColor || p == OverlayGeometry.Transparent,
                $"枠の絵に枠線でも透過でもない色 (0x{p:X8}) があります。"));
    }

    /// <summary>
    /// **アイコンの絵に透過が 1 ピクセルも無いこと。**
    /// </summary>
    /// <remarks>
    /// これがアイコンの窓が押せる理由そのものである。レイヤードウィンドウのヒットテストは
    /// ピクセルごとのアルファで決まるので、透過を混ぜるとその点だけクリックが下へ抜ける
    /// (docs/DESIGN.md §10 の実測)。
    /// </remarks>
    [Theory]
    [InlineData(96)]
    [InlineData(168)]
    public void PaintIcon_LeavesNoTransparentPixel(int dpi)
    {
        uint[] pixels = PaintIcon(dpi);

        Assert.All(
            pixels,
            p => Assert.True(
                p == OverlayGeometry.IconBackColor || p == OverlayGeometry.IconMarkColor,
                $"アイコンの絵に不透明でない色 (0x{p:X8}) があります = そこは押せません。"));
    }

    [Theory]
    [InlineData(96)]
    [InlineData(168)]
    public void PaintIcon_DrawsAVisibleCheckMark(int dpi)
    {
        int size = OverlayGeometry.MetricsFor(dpi).IconSize;
        uint[] pixels = PaintIcon(dpi);

        int marks = 0;
        foreach (uint p in pixels)
        {
            if (p == OverlayGeometry.IconMarkColor)
            {
                marks++;
            }
        }

        // 折れ線 2 本。少なすぎれば見えず、多すぎればアイコンを潰している。
        // 下限を一辺に比例させてあるので、96 DPI では 40 である
        Assert.InRange(marks, size * 2, size * size / 2);
        // チェックの折れ点 (一辺の 0.4, 0.7) は必ず白
        int bendX = (int)Math.Round(size * 0.40);
        int bendY = (int)Math.Round(size * 0.70);
        Assert.Equal(OverlayGeometry.IconMarkColor, pixels[(bendY * size) + bendX]);
    }

    /// <summary>四隅は背景色 (チェックマークは中央付近にしか無い)。</summary>
    [Theory]
    [InlineData(96)]
    [InlineData(168)]
    public void PaintIcon_KeepsTheCornersAsBackground(int dpi)
    {
        int size = OverlayGeometry.MetricsFor(dpi).IconSize;
        uint[] pixels = PaintIcon(dpi);

        Assert.Equal(OverlayGeometry.IconBackColor, pixels[0]);
        Assert.Equal(OverlayGeometry.IconBackColor, pixels[size - 1]);
        Assert.Equal(OverlayGeometry.IconBackColor, pixels[(size - 1) * size]);
        Assert.Equal(OverlayGeometry.IconBackColor, pixels[(size * size) - 1]);
    }

    [Fact]
    public void PaintFrame_WithATooSmallBuffer_Throws()
        => Assert.Throws<ArgumentOutOfRangeException>(
            () => OverlayGeometry.PaintFrame(new ElementRect(0, 0, 200, 120), Dpi96, new uint[10]));

    [Fact]
    public void PaintIcon_WithATooSmallBuffer_Throws()
        => Assert.Throws<ArgumentOutOfRangeException>(
            () => OverlayGeometry.PaintIcon(Dpi96, new uint[10]));

    // ---- 当たり判定 ----

    /// <summary>
    /// 表示スケールごとの表駆動。
    /// </summary>
    /// <remarks>
    /// <para>
    /// **座標と寸法で DPI の効き方が違う** (docs/DESIGN.md §9)。座標はすべて物理ピクセルなので
    /// 判定式に換算は要らない — ホストが PerMonitorV2 を宣言している限り、要素の矩形自体が
    /// 物理ピクセルで来るからである。ここに**座標の**換算を持ち込む変更が入れば、
    /// それはホストが DPI 非認識であるという別の問題を隠している (docs/DESIGN.md A19 / docs/TESTING.md §4)。
    /// </para>
    /// <para>
    /// 一方**寸法** (アイコンの一辺) はスケールする。だから各行は「その表示スケールでの
    /// 要素の大きさ」と「その DPI」を対にして渡す。この区別が無いと、
    /// 要素だけが伸びてアイコンが取り残される。
    /// </para>
    /// </remarks>
    [Theory]
    [InlineData(96, 100, 100, 260, 160)]    // 100%: 160x60
    [InlineData(144, 150, 150, 390, 240)]   // 150%
    [InlineData(168, 175, 175, 455, 280)]   // 175% (実機の RDP 4K 環境)
    [InlineData(192, 200, 200, 520, 320)]   // 200%
    public void IsInIconZone_HitsTheCenterOfTheIconAtEveryScale(int dpi, int left, int top, int right, int bottom)
    {
        var rect = new ElementRect(left, top, right, bottom);
        OverlayGeometry.Metrics m = OverlayGeometry.MetricsFor(dpi);
        int centerX = rect.Right - m.IconInset;
        int centerY = rect.Top + m.IconInset;

        Assert.True(OverlayGeometry.IsInIconZone(rect, dpi, centerX, centerY));
        // 隅ぎりぎりの内外
        Assert.True(OverlayGeometry.IsInIconZone(rect, dpi, centerX - m.IconHalf, centerY - m.IconHalf));
        Assert.True(OverlayGeometry.IsInIconZone(rect, dpi, centerX + m.IconHalf - 1, centerY + m.IconHalf - 1));
        Assert.False(OverlayGeometry.IsInIconZone(rect, dpi, centerX - m.IconHalf - 1, centerY));
        Assert.False(OverlayGeometry.IsInIconZone(rect, dpi, centerX + m.IconHalf, centerY));
        Assert.False(OverlayGeometry.IsInIconZone(rect, dpi, centerX, centerY - m.IconHalf - 1));
        Assert.False(OverlayGeometry.IsInIconZone(rect, dpi, centerX, centerY + m.IconHalf));
    }

    /// <summary>
    /// ネガティブコントロール: 枠の中央や他の 3 隅はアイコン領域ではないこと。
    /// ここが true になる実装だと、枠のどこを押しても確定してしまう。
    /// </summary>
    [Theory]
    [InlineData(0.5, 0.5)]   // 中央
    [InlineData(0.0, 0.0)]   // 左上
    [InlineData(0.0, 1.0)]   // 左下
    [InlineData(1.0, 1.0)]   // 右下
    public void IsInIconZone_RejectsEverywhereElseOnTheFrame(double fx, double fy)
    {
        var rect = new ElementRect(100, 100, 400, 300);
        int x = rect.Left + (int)((rect.Width - 1) * fx);
        int y = rect.Top + (int)((rect.Height - 1) * fy);

        Assert.False(OverlayGeometry.IsInIconZone(rect, Dpi96, x, y));
        Assert.False(OverlayGeometry.IsInIconZone(rect, Dpi175, x, y));
    }
}
