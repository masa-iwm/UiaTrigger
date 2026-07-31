// オーバーレイの幾何とピクセル生成 (docs/TESTING.md §1 T1)。
//
// ここには GDI も WinUI も出てこない。純粋に「矩形 + DPI → ARGB バッファ」の関数であり、
// OverlayController はこの結果を UpdateLayeredWindow に流すだけである。
//
// 分けた理由: 確定アイコンの位置 (枠外へのはみ出し 1/5) のような調整値は、描画に
// 埋め込むと目視でしか確認できない。純関数にしておけばピクセル単位で固定できる。
//
// dpi は引数である (docs/DESIGN.md §9)。寸法を物理ピクセルの定数にすると、要素の矩形
// だけが DPI で伸びるため 175% では枠とアイコンが相対的に細く小さくなる。
// DPI を中で引きにいかないのは意図である — 引きにいくと T1 が固定できなくなり、
// T4 / T5 の期待矩形も計算できなくなる。DPI の出どころは IDpiSource が持つ。
//
// <b>絵は 2 枚である (docs/DESIGN.md §10)。</b>1 枚のビットマップに枠とアイコンを描いて
// 1 つのウィンドウへ流す形では、<b>クリックスルーが原理的に成立しない</b> —
// レイヤードウィンドウのヒットテストはピクセルごとのアルファで決まるので、
// 不透明な枠線は必ずそのウィンドウのものになる (実測)。
// 枠とアイコンは別のウィンドウで、
//   ・枠   … WS_EX_TRANSPARENT (窓ごとヒットテストから外れる) → PaintFrame
//   ・アイコン … 全ピクセル不透明で、窓の矩形がそのまま当たり判定 → PaintIcon
// である。<b>2 枚を 1 枚に戻すとクリックスルーが壊れる。</b>
//
// テストは tests/UiaTrigger.Core.Tests/OverlayGeometryTests.cs にある。
using UiaTrigger.Models;

namespace UiaTrigger.Picker;

/// <summary>選択枠と確定アイコンの幾何・描画 (副作用なし)。</summary>
internal static class OverlayGeometry
{
    /// <summary>下の定数がどの DPI での値かを表す。</summary>
    public const int ReferenceDpi = 96;

    /// <summary>枠線の太さ (96 DPI での値)。</summary>
    public const int FrameThicknessAt96 = 3;

    /// <summary>アイコンの半分 (96 DPI での値)。<b>これが寸法の基本単位である</b> — 下記の不変条件を参照。</summary>
    public const int IconHalfAt96 = 10;

    /// <summary>確定アイコンの一辺 (96 DPI での値)。</summary>
    public const int IconSizeAt96 = IconHalfAt96 * 2;

    /// <summary>アイコン背景の四角のうち、枠の外側にはみ出す量 (外側 1/5、96 DPI での値)。</summary>
    public const int IconOutsideAt96 = IconSizeAt96 / 5;

    /// <summary>アイコン中心を枠の右上隅から内側へずらす量 (96 DPI での値)。</summary>
    public const int IconInsetAt96 = IconHalfAt96 - IconOutsideAt96;

    // 事前乗算 ARGB
    public const uint Transparent = 0x00000000;
    public const uint FrameColor = 0xFFFF4040;  // 赤
    public const uint IconBackColor = 0xFF2E9E4F;  // 緑
    public const uint IconMarkColor = 0xFFFFFFFF;  // 白

    // チェックマークの折れ線。アイコンの一辺を 1 とした正規化座標で持つ
    // (20px 決め打ちの手描きだと DPI でスケールできない — docs/DESIGN.md §9)。
    // 20px 箱に当てると (4,10)→(8,14)→(15,6) になり、96 DPI での従来の設計値と一致する。
    private const double MarkStartX = 0.20, MarkStartY = 0.50;
    private const double MarkBendX = 0.40, MarkBendY = 0.70;
    private const double MarkEndX = 0.75, MarkEndY = 0.30;

    /// <summary>チェックマークの太さ (一辺に対する比)。20px で 3px になる。</summary>
    private const double MarkThicknessRatio = 0.15;

    /// <summary>ある DPI での実寸。</summary>
    /// <remarks>
    /// <b>4 つを別々にスケールしてはいけない。</b>丸めが独立に効くので中途半端な DPI でずれる
    /// (dpi=110 なら <c>IconInset</c> を直接スケールすると 6、<c>IconHalf - IconOutside</c> なら 7)。
    /// <see cref="MetricsFor"/> は <c>IconHalf</c> と <c>IconOutside</c> だけをスケールし、
    /// 残りは導出する。
    /// </remarks>
    internal readonly record struct Metrics(
        int FrameThickness,
        int IconSize,
        int IconHalf,
        int IconOutside,
        int IconInset);

    /// <summary>
    /// <paramref name="dpi"/> での実寸を出す。96 では従来の値 (3 / 20 / 10 / 4 / 6) に一致する。
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>不変条件 1: <c>IconSize == IconHalf * 2</c>。</b>アイコンの窓は一辺 <c>IconSize</c> で、
    /// その矩形がそのまま当たり判定 (<see cref="IsInIconZone"/>) になる。
    /// <see cref="IconRect"/> は中心から <c>IconHalf</c> で広がる式から導いているので、
    /// 一辺が偶数でないと絵と当たり判定が 1px ずれる。だから <c>IconHalf</c> を先にスケールし、
    /// <c>IconSize</c> は 2 倍で<b>導出する</b> — <c>IconSize</c> を直接スケールすると
    /// 175% でちょうど 35 (奇数) になる。
    /// </para>
    /// <para>
    /// <b>不変条件 2: <c>IconInset == IconHalf - IconOutside</c> かつ 1 以上。</b>
    /// アイコンが枠の外へ完全に出てしまうのを防ぐ。
    /// </para>
    /// <para>枠線は最低 1px を保証する (低い DPI で消えないように)。</para>
    /// </remarks>
    public static Metrics MetricsFor(int dpi)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(dpi);

        // IconHalf が基本単位。2 未満にすると IconOutside の下限 1 と衝突する
        int iconHalf = Math.Max(2, Scale(IconHalfAt96, dpi));
        int iconOutside = Math.Clamp(Scale(IconOutsideAt96, dpi), 1, iconHalf - 1);
        return new Metrics(
            FrameThickness: Math.Max(1, Scale(FrameThicknessAt96, dpi)),
            IconSize: iconHalf * 2,
            IconHalf: iconHalf,
            IconOutside: iconOutside,
            IconInset: iconHalf - iconOutside);
    }

    /// <summary>96 DPI での値を <paramref name="dpi"/> へ直す (四捨五入)。</summary>
    private static int Scale(int valueAt96, int dpi)
        => ((valueAt96 * dpi) + (ReferenceDpi / 2)) / ReferenceDpi;

    /// <summary>
    /// 枠の窓の大きさ。<b>アイコンのはみ出しは含まない</b> (アイコンは別の窓である)。
    /// 枠自体は最低でもアイコン 1 個分は確保する (極小要素でアイコンが枠から溢れないように)。
    /// </summary>
    public static (int Width, int Height) FrameSize(ElementRect rect, int dpi)
    {
        Metrics m = MetricsFor(dpi);
        return (Math.Max(rect.Width, m.IconSize), Math.Max(rect.Height, m.IconSize));
    }

    /// <summary>枠の窓を置くスクリーン座標の左上 = 要素の左上そのもの。</summary>
    public static (int X, int Y) FrameOrigin(ElementRect rect) => (rect.Left, rect.Top);

    /// <summary>
    /// 確定アイコンの窓 (スクリーン座標)。<b>この矩形がそのまま当たり判定である。</b>
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>座標と寸法は別の話であり、DPI の効き方が違う。</b>
    /// <paramref name="rect"/> は物理ピクセルであり、ホストが PerMonitorV2 を宣言している
    /// 限りスケールが何であっても要素の矩形自体が物理ピクセルで来るため、
    /// <b>座標系には</b>換算が要らない
    /// — 逆に言えば、ホストが DPI 非認識だとこの位置は静かにずれる (docs/DESIGN.md A19)。
    /// </para>
    /// <para>
    /// 一方<b>寸法</b> (アイコンの一辺) は要素と一緒に伸ばさなければならない。
    /// 物理ピクセルの定数のままだと 175% では要素に対して小さくなる。
    /// この 2 つを 1 文に混ぜて語らないこと (docs/DESIGN.md §9)。
    /// </para>
    /// <para>
    /// <b>基準は要素の右端ではなく「広げたあとの枠」の右端である</b> (docs/DESIGN.md §10)。
    /// <see cref="FrameSize"/> は枠を最低アイコン 1 個分まで広げ、アイコンは
    /// <b>広げたあとの</b>枠の右上に来る。ここで <c>rect.Right</c> を見ると、
    /// アイコンより小さい要素で<b>見えているのに押せない帯</b>ができる (96 DPI でも同じ)。
    /// 縦は枠の高さに依らずつねに上端なので、<c>rect.Top</c> を基準にしてよい。
    /// </para>
    /// <para>
    /// <b>この矩形は「絵の中の一部分」ではなく窓そのものである。</b>だから
    /// 「見えているアイコン」と「押せるアイコン」がずれる余地が構造から消えている
    /// (docs/DESIGN.md §10)。
    /// </para>
    /// </remarks>
    public static (int X, int Y, int Size) IconRect(ElementRect rect, int dpi)
    {
        Metrics m = MetricsFor(dpi);
        int frameRight = rect.Left + Math.Max(rect.Width, m.IconSize);
        // 中心は「枠の右上隅から内側へ IconInset」。そこから IconHalf で広がるので、
        // 左上は frameRight - IconInset - IconHalf = frameRight - IconSize + IconOutside になる
        return (frameRight - m.IconSize + m.IconOutside, rect.Top - m.IconOutside, m.IconSize);
    }

    /// <summary>
    /// 確定アイコンの当たり判定 (スクリーン座標) = <see cref="IconRect"/> の中かどうか。
    /// </summary>
    /// <remarks>
    /// アイコンの窓が受け取るクリックとは別に、<b>ピッカー側の除外判定</b>がこれを使う
    /// (確定アイコンへ向かう途中で選択が変わらないように)。<c>IconRect</c> から導くので、
    /// 窓の位置と除外領域が食い違うことはない。
    /// </remarks>
    public static bool IsInIconZone(ElementRect r, int dpi, int screenX, int screenY)
    {
        (int x, int y, int size) = IconRect(r, dpi);
        return screenX >= x && screenX < x + size && screenY >= y && screenY < y + size;
    }

    /// <summary>
    /// 枠線だけを ARGB バッファへ書く。<paramref name="pixels"/> は
    /// <see cref="FrameSize"/> の Width × Height 個であること。
    /// </summary>
    /// <remarks>
    /// <b>ここにアイコンを描いてはいけない。</b>この絵は WS_EX_TRANSPARENT の窓へ流れるので、
    /// ここに不透明な絵を足しても<b>押せない</b> (窓ごとヒットテストから外れている)。
    /// 押せる絵は <see cref="PaintIcon"/> のほうにだけ置く (docs/DESIGN.md §10)。
    /// </remarks>
    public static void PaintFrame(ElementRect rect, int dpi, Span<uint> pixels)
    {
        Metrics m = MetricsFor(dpi);
        (int width, int height) = FrameSize(rect, dpi);
        ArgumentOutOfRangeException.ThrowIfLessThan(pixels.Length, width * height);

        pixels[..(width * height)].Clear(); // 完全透過

        for (int y = 0; y < height; y++)
        {
            bool edgeRow = y < m.FrameThickness || y >= height - m.FrameThickness;
            int rowBase = y * width;
            for (int x = 0; x < width; x++)
            {
                if (edgeRow || x < m.FrameThickness || x >= width - m.FrameThickness)
                {
                    pixels[rowBase + x] = FrameColor;
                }
            }
        }
    }

    /// <summary>
    /// 確定アイコンだけを ARGB バッファへ書く (緑の四角 + 白いチェックマーク)。
    /// <paramref name="pixels"/> は <c>IconSize</c> × <c>IconSize</c> 個であること。
    /// </summary>
    /// <remarks>
    /// <b>全ピクセルが不透明であることが、この窓が押せる理由である。</b>
    /// レイヤードウィンドウのヒットテストはピクセルごとのアルファで決まるので、
    /// ここに透過を混ぜるとその点だけ押せなくなる (docs/DESIGN.md §10)。
    /// 矩形は要素に依らないので、引数は DPI だけでよい。
    /// </remarks>
    public static void PaintIcon(int dpi, Span<uint> pixels)
    {
        int size = MetricsFor(dpi).IconSize;
        ArgumentOutOfRangeException.ThrowIfLessThan(pixels.Length, size * size);

        pixels[..(size * size)].Fill(IconBackColor);

        int half = Math.Max(0, ((int)Math.Round(size * MarkThicknessRatio) - 1) / 2);
        DrawSegment(pixels, size, half, MarkStartX, MarkStartY, MarkBendX, MarkBendY);
        DrawSegment(pixels, size, half, MarkBendX, MarkBendY, MarkEndX, MarkEndY);
    }

    /// <summary>正規化座標の線分を、太さ <c>2*half+1</c> の四角い筆で打つ。</summary>
    private static void DrawSegment(
        Span<uint> pixels, int iconSize, int half,
        double nx0, double ny0, double nx1, double ny1)
    {
        double x0 = nx0 * iconSize, y0 = ny0 * iconSize;
        double x1 = nx1 * iconSize, y1 = ny1 * iconSize;
        // 1px あたり 2 打つ (間が空かないように)
        int steps = (int)Math.Ceiling(Math.Max(Math.Abs(x1 - x0), Math.Abs(y1 - y0))) * 2;
        for (int i = 0; i <= steps; i++)
        {
            double t = steps == 0 ? 0.0 : (double)i / steps;
            int cx = (int)Math.Round(x0 + ((x1 - x0) * t));
            int cy = (int)Math.Round(y0 + ((y1 - y0) * t));
            Plot(pixels, iconSize, cx, cy, half);
        }
    }

    private static void Plot(Span<uint> pixels, int iconSize, int cx, int cy, int half)
    {
        for (int dy = -half; dy <= half; dy++)
        {
            for (int dx = -half; dx <= half; dx++)
            {
                int x = cx + dx;
                int y = cy + dy;
                if (x >= 0 && x < iconSize && y >= 0 && y < iconSize)
                {
                    pixels[(y * iconSize) + x] = IconMarkColor;
                }
            }
        }
    }
}
