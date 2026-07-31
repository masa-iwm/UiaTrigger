using System.Text.RegularExpressions;
using Xunit;

namespace UiaTrigger.Tests;

/// <summary>
/// 低レベルキーボードフックが**入力の出どころで分岐しない**ことの不変条件
/// (docs/DESIGN.md §11 / docs/TESTING.md §3)。
///
/// <para>
/// 合成入力を使うテスト層 (T5) がある (docs/TESTING.md §3)。実測では、
/// 合成した入力には**例外なく**注入ビットが立つ (キー 2/2・マウス 1/1)。つまり
/// <c>HookProc</c> がそのビットで分岐した瞬間、**T5 と物理入力は必ず別の経路を通る**。
/// </para>
/// <para>
/// どちらへ壊れるかは分岐の向きで決まる。注入を**捨てる**分岐なら T5 が赤くなる
/// (見える壊れ方)。注入だけを**通す**分岐なら **T5 は緑のまま実機が死ぬ** —
/// docs/TESTING.md §4 のホイールバグ (ハーネスが被験体の欠陥を共有していた) と**まったく同じ形**が
/// 1 層上で再現する。この不変条件は向きに依らず両方を塞ぐ。
/// </para>
/// <para>
/// **綴りではなく参照そのものを見る。**<c>NativeMethods.txt</c> は
/// <c>KBDLLHOOKSTRUCT</c> しか挙げておらず、注入ビットの定数は生成されていない可能性がある。
/// 定数名で探す正規表現は**空振りして緑になる** — 塞ぎたかったのと同じ失敗形である。
/// </para>
/// <para>
/// **塞げていない穴を書いておく**: 見るのは <c>OverlayController.cs</c> だけである。
/// 構造体そのもの (あるいはそのコピー) を別ファイルのヘルパーへ渡し、向こうで読む形は
/// この検査を素通りする。今そういうヘルパーは無く、<c>KBDLLHOOKSTRUCT*</c> は
/// <c>HookProc</c> の中でしか作られないので、足すには新しい経路を書くことになる。
/// </para>
/// </summary>
public sealed class HookPolicyTests
{
    /// <summary>
    /// <c>HookProc</c> が仮想キーコード以外を見ていないこと。
    ///
    /// <para>
    /// **「無いこと」の主張なので、探し損ねても緑になる。**そこで先にメソッドを
    /// 切り出せたこと・構造体を実際に読んでいることを assert してから不在を見る
    /// (<c>ElementBorrowTests.TheResolutionLayerNodeHasNoAsynchronousReclaimer</c> と同じ形)。
    /// </para>
    /// </summary>
    [Fact]
    public void TheLowLevelHookDoesNotBranchOnWhereTheInputCameFrom()
    {
        string source = ReadOverlayController();
        string body = ReadHookProc(source);

        // 先に「読めていること」を固定する。ここが空振りしたまま不在を主張すると、
        // この検査は何も守らずに緑になる
        Assert.Contains("(KBDLLHOOKSTRUCT*)lParam.Value", body, StringComparison.Ordinal);
        Assert.Contains("->vkCode", body, StringComparison.Ordinal);

        // 綴りを 1 つ禁じるのではなく、識別子として現れないことを見る。
        // info->flags だけを禁じると「ローカルへコピーしてから読む」形が素通りする
        MatchCollection reads = Regex.Matches(
            body,
            @"\bflags\b",
            RegexOptions.CultureInvariant | RegexOptions.IgnoreCase,
            TimeSpan.FromSeconds(1));

        Assert.True(
            reads.Count == 0,
            $"HookProc が入力の出どころ (注入フラグ) を読んでいます ({reads.Count} 箇所)。" +
            "合成した入力には必ず注入ビットが立つので、これを足すと T5 と物理入力が " +
            "別の経路を通ります。注入だけを通す向きなら T5 は緑のまま実機が死にます — " +
            "docs/TESTING.md §3 を読んでください。");
    }

    /// <summary>
    /// 同じファイルの**どこか**で構造体のフラグが読まれていないこと。
    ///
    /// <para>
    /// 上の検査は <c>HookProc</c> の本体しか見ない。同じファイルのヘルパーへ
    /// ポインターを渡す形を塞ぐために、ファイル全体にも掛ける。
    /// </para>
    /// </summary>
    [Fact]
    public void NothingInTheOverlayControllerReadsTheFlagsOfAHookStruct()
    {
        string source = ReadOverlayController();

        Assert.False(
            Regex.IsMatch(
                source,
                @"->\s*flags\b",
                RegexOptions.CultureInvariant | RegexOptions.IgnoreCase,
                TimeSpan.FromSeconds(1)),
            "OverlayController.cs のどこかでフック構造体のフラグが読まれています。" +
            "HookProc の中でなくても結論は同じです — 入力の出どころで分岐した時点で、" +
            "T5 の緑は実機について何も言わなくなります (docs/TESTING.md §3)。");
    }

    private static string ReadOverlayController()
        => File.ReadAllText(RepoPaths.Combine("src", "UiaTrigger.Picker.Core", "OverlayController.cs"));

    /// <summary>
    /// <c>HookProc</c> の本体だけを切り出す。
    ///
    /// <para>
    /// ファイル全体へ <c>flags</c> の禁止を掛けることはできない — ウィンドウのスタイルなど、
    /// 正当に <c>flags</c> と綴られうるものが同居する型だからである。
    /// </para>
    /// </summary>
    private static string ReadHookProc(string source)
    {
        Match declaration = Regex.Match(
            source,
            @"^    private static unsafe LRESULT HookProc\([^\r\n]*",
            RegexOptions.CultureInvariant | RegexOptions.Multiline,
            TimeSpan.FromSeconds(1));

        Assert.True(
            declaration.Success,
            "OverlayController.cs に HookProc の宣言が見つかりません。" +
            "改名・移動したのなら、docs/DESIGN.md §11 の不変条件も一緒に運んでください " +
            "(見つからないまま緑になると、守っているつもりのものが何も守られません)。");

        int start = declaration.Index;
        Match next = Regex.Match(
            source[(start + declaration.Length)..],
            @"^    (?:private|internal|public|\[)",
            RegexOptions.CultureInvariant | RegexOptions.Multiline,
            TimeSpan.FromSeconds(1));

        string body = next.Success ? source.Substring(start, declaration.Length + next.Index) : source[start..];

        // 終端の規約が変わると (メンバーの並びが変わる / 属性行が付く) ^    が空振りし、
        // 切り出しが黙って「ファイルの残り全部」になる。そうなると隣のメンバーの
        // flags でこちらが落ち、失敗メッセージが嘘になる。
        //
        // **目印は「HookProc より後ろに在り、かつ flags を綴るメンバー」へ向けること。**
        // 目印にしたメンバーがリファクタリングで消えると**この検査は何も守らなくなる**
        // (在りもしない文字列を探して必ず false = 常に通る — 実際に一度そうなった)。
        // 目印にしたメンバーを消すときは、ここも一緒に動かすこと。
        // いまは Redraw を使う — その先の UpdateLayered / ClaimTopmost が
        // UPDATE_LAYERED_WINDOW_FLAGS と SET_WINDOW_POS_FLAGS を綴っており、
        // 飲み込むとまさに嘘の失敗が起きる位置である
        Assert.False(
            body.Contains("private void Redraw()", StringComparison.Ordinal),
            "HookProc の切り出しが隣のメンバーまで飲み込んでいます。" +
            "終端に使っているインデント規約が空振りしています。" +
            "このまま通すと、HookProc と無関係な flags でこの検査が落ちるようになります。");

        return body;
    }
}
