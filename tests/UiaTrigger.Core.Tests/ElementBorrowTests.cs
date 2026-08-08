using System.Text.RegularExpressions;
using Xunit;

namespace UiaTrigger.Tests;

/// <summary>
/// 要素ハンドルの借用規律に関する回帰テスト (docs/DESIGN.md §7)。
///
/// <para>
/// この欠陥は実機でプロセスごと落とす。<c>FindAllBuildCache</c> の中でアクセス違反
/// (0xc0000005) になり、例外ではないのでアプリのハンドラーには何も届かない
/// (イベントログにしか残らない)。原因は <see cref="UiaElement"/> の
/// **use-after-free** である: 生の COM ポインターを取り出した後、JIT は持ち主を
/// 到達不能とみなしてよく、そうなるとファイナライザーが**別スレッドで** RCW を解放する。
/// </para>
/// <para>
/// **ここが源泉テストである理由**: 再現には GC のタイミングが要る。意図的に退行を入れて
/// 「確実に落ちるテスト」は書けない — 落ちるかどうかが GC 次第だからである。そこで
/// 「危険な書き方ができないこと」自体を固定する。
/// </para>
/// </summary>
public sealed class ElementBorrowTests
{
    private static string ReadSource(params string[] parts) => File.ReadAllText(RepoPaths.Combine(parts));

    /// <summary>
    /// 借用スコープが閉じるときに <c>GC.KeepAlive</c> していること。
    ///
    /// <para>
    /// この 1 行が**この仕組みの全部**である。消してもビルドは通り、テストも
    /// (これ以外は) 通り、ほとんどの場合は動く。GC がたまたま走ったときにだけ
    /// プロセスが落ちるようになる — つまり消えたことに気づく手段が他に無い。
    /// </para>
    /// </summary>
    [Fact]
    public void TheBorrowScopeKeepsItsOwnerAliveUntilItCloses()
    {
        string source = ReadSource("src", "UiaTrigger.Core", "Session", "UiaElement.cs");

        // EndBorrow → KeepAlive の順で両方あること。EndBorrow は遅延解放の解除 (B10) で、
        // KeepAlive は持ち主の生存固定 — どちらか片方だけでは元の欠陥 (§7 / B10) が戻る
        Match dispose = Regex.Match(
            source,
            @"public void Dispose\(\)\s*\{\s*_owner\.EndBorrow\(\);\s*GC\.KeepAlive\(_owner\);\s*\}",
            RegexOptions.CultureInvariant,
            TimeSpan.FromSeconds(1));

        Assert.True(
            dispose.Success,
            "借用スコープの Dispose が「_owner.EndBorrow(); GC.KeepAlive(_owner);」の形ではありません。" +
            "KeepAlive が無いと COM 呼び出しの最中にファイナライザーが要素を解放しえます " +
            "(アクセス違反でプロセスごと落ちる)。EndBorrow が無いと借用中に要求された解放が " +
            "永遠に実行されません (docs/DESIGN.md B10)。");
    }


    /// <summary>
    /// <see cref="UiaElement"/> が、生の COM 要素をスコープ無しで渡す口を持たないこと。
    ///
    /// <para>
    /// <c>Unwrap()</c> のような「取り出して使う」形である限り、
    /// 呼び出しのあいだ持ち主が生きている保証はどこにも無い。可視性 (private) でも塞げるが、
    /// **internal に戻すだけで元に戻る**ので、この型が生ポインターを返さないこと自体を見る。
    /// </para>
    /// <para>
    /// 見るのは <c>UiaElement.cs</c> だけである。解決層には別物の
    /// <c>UiaElementNode.Unwrap(IElementNode)</c> があり、そちらは寿命の管理が違う
    /// (解決ループが明示的に解放する)。名前だけで一律に禁じると、関係の無いものを巻き込む。
    /// **解決層が無防備なわけではない** — そちらの安全性は
    /// <see cref="TheResolutionLayerNodeHasNoAsynchronousReclaimer"/> が別の形で固定している
    /// (docs/DESIGN.md §7)。
    /// </para>
    /// </summary>
    [Fact]
    public void TheHandleNeverReturnsTheRawElement()
    {
        string source = ReadSource("src", "UiaTrigger.Core", "Session", "UiaElement.cs");

        // 借用スコープの中 (Borrowed.Element) だけが生ポインターを持ってよい。
        // UiaElement 自身のメンバーとして IUIAutomationElement を返すものがあってはならない
        MatchCollection returns = Regex.Matches(
            source,
            @"^\s{4}(?:internal|public|private)\s+IUIAutomationElement\s+\w+\s*\(",
            RegexOptions.CultureInvariant | RegexOptions.Multiline,
            TimeSpan.FromSeconds(1));

        Assert.True(
            returns.Count == 0,
            "UiaElement が生の COM 要素を返すメンバーを持っています " +
            $"({string.Join(", ", returns.Select(m => m.Value.Trim()))})。" +
            "呼び出しのあいだ持ち主が生きている保証が無くなります — Borrow() を使ってください。");
    }

    /// <summary>
    /// 借用が**必ず <c>using</c> スコープに入っている**こと。
    ///
    /// <para>
    /// 個数を数えるだけでは足りない。<c>a.Borrow().Element</c> のようにその場で使うと、
    /// 一時的な構造体は <c>Dispose</c> されないので <c>GC.KeepAlive</c> が走らず、
    /// **借用スコープを使っているように見えて元の欠陥のまま**になる。
    /// (個数だけを見る検査では、実際にこの書き方が素通りした。)
    /// </para>
    /// </summary>
    [Theory]
    [InlineData("UiaTrigger.Core", "Session", "UiaSession.cs")]
    public void EveryBorrowIsHeldByAUsingScope(string project, string folder, string file)
    {
        string source = ReadSource("src", project, folder, file);

        int borrows = Regex.Count(
            source, @"\.Borrow\(\)", RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1));
        int scoped = Regex.Count(
            source,
            @"using\s+UiaElement\.Borrowed\s+\w+\s*=\s*[^;]+\.Borrow\(\);",
            RegexOptions.CultureInvariant,
            TimeSpan.FromSeconds(1));

        Assert.True(borrows >= 9, $"{file}: 借用が {borrows} 箇所しかありません (要素を UIA へ渡す経路は 9 つある)。");
        Assert.True(
            borrows == scoped,
            $"{file}: {borrows} 箇所の借用のうち using スコープに入っているのは {scoped} 箇所です。" +
            "スコープの外で使うと Dispose されず、GC.KeepAlive が走りません " +
            "(借りているように見えて元の欠陥のままになります)。");
    }

    /// <summary>
    /// ピッカー側のラッパーが、包んでいるハンドルへ解放を**転送している**こと
    /// (docs/DESIGN.md §7)。
    ///
    /// <para>
    /// **ここも源泉テストである。**この 1 行を消すと、ピッカーの決定的解放 (掃き出し) は
    /// 本番で丸ごと無効になるのに **T1 は 488 件すべて緑のまま**になる —
    /// プレゼンターのテストが動かすのは <c>FakePickerElement</c> という別の実装であり、
    /// <c>UiaPickerElement</c> は T1 から作れない (<see cref="UiaElement"/> の
    /// コンストラクタが private で、引数が internal な COM 型だから)。
    /// </para>
    /// <para>
    /// つまり「掃き出しが動いていること」と「掃き出しが実際に COM 参照を手放していること」は
    /// 別の主張で、後者を見られるのはここだけである。上の 2 つと同じ形の穴である。
    /// </para>
    /// </summary>
    [Fact]
    public void ThePickerWrapperForwardsDisposalToTheHandleItWraps()
    {
        string source = ReadSource("src", "UiaTrigger.Picker.Core", "PickerServices.cs");

        Match forward = Regex.Match(
            source,
            @"public void Dispose\(\)\s*=>\s*Inner\.Dispose\(\);",
            RegexOptions.CultureInvariant,
            TimeSpan.FromSeconds(1));

        Assert.True(
            forward.Success,
            "UiaPickerElement.Dispose が Inner.Dispose へ転送していません。" +
            "プレゼンターの掃き出しは動いたまま、相手プロセスの provider は 1 つも解放されなくなります " +
            "(T1 は偽の要素で動くので全件緑のままです)。");
    }

    /// <summary>
    /// 解決層の <c>UiaElementNode</c> に、**非同期に要素を回収する主体が無い**こと
    /// (docs/DESIGN.md §7)。
    ///
    /// <para>
    /// **ここで固定しているのは「不在」そのものが安全性の根拠であることである。**
    /// この use-after-free (docs/DESIGN.md §7) は「GC のファイナライザースレッドが、UIA スレッドの
    /// 呼び出し中に RCW を解放する」ことで起きた。<c>UiaElementNode</c> は
    /// <c>Unwrap</c> で生ポインターを返す形が <c>UiaElement.Unwrap</c> と同じだが、
    /// ファイナライザーも <see cref="IDisposable"/> も持たないので**回収者が居ない** —
    /// 危険は同じでない。ファイナライザーか <see cref="IDisposable"/> を足した時点で
    /// この根拠は消え、<c>Unwrap</c> を借用スコープへ変える必要が生じる。
    /// </para>
    /// <para>
    /// **「無いこと」の主張なので、探し損ねても緑になる。**そこで先に宣言と
    /// <c>Release()</c> の存在を assert して、**実際にその型を読めていること**を固定する
    /// (<see cref="EveryBorrowIsHeldByAUsingScope"/> が借用の下限を先に見るのと同じ形)。
    /// </para>
    /// <para>
    /// **塞げていない穴を書いておく**: この検査は 2 ファイルしか読まない。
    /// 型が別ファイルへ移れば宣言の anchor が落ちるので気づけるが、
    /// <c>partial</c> にして別ファイルでファイナライザーを足す形は宣言行が変わるため
    /// これも落ちる。残るのは「<see cref="IDisposable"/> を <c>IElementNode</c> の
    /// さらに上位のインターフェイスへ足す」経路だけで、そこは今 1 段しかない。
    /// </para>
    /// </summary>
    [Fact]
    public void TheResolutionLayerNodeHasNoAsynchronousReclaimer()
    {
        string node = ReadUiaElementNode();

        Assert.True(
            node.Contains("public void Release()", StringComparison.Ordinal),
            "UiaElementNode の Release() が見つかりません。型を読めていないまま " +
            "「無いこと」を主張すると、この検査は何も守らずに緑になります。");

        Assert.False(
            node.Contains("~UiaElementNode", StringComparison.Ordinal),
            "UiaElementNode にファイナライザーが足されています。" +
            "これが無いことが Unwrap の安全性の根拠です (docs/DESIGN.md §7) — " +
            "足すなら Unwrap を借用スコープ (UiaElement.Borrow と同じ形) へ変えてください。" +
            "でないと UIA スレッドの呼び出し中に GC が RCW を解放しえます " +
            "(例外ではなくアクセス違反でプロセスごと落ちる)。");

        string declaration = node[..node.IndexOf('\n', StringComparison.Ordinal)];
        Assert.False(
            declaration.Contains("IDisposable", StringComparison.Ordinal),
            $"UiaElementNode が IDisposable を実装しています ({declaration.Trim()})。" +
            "ファイナライザーと同じ理由でこの型の安全性の根拠が消えます — 上のコメントを読んでください。");

        // IElementNode 側に足されると、UiaElementTree.cs は 1 文字も変わらないまま
        // UiaElementNode が disposable になる。インターフェイスの宣言も見る
        string tree = ReadSource("src", "UiaTrigger.Core", "Resolution", "ElementTree.cs");
        Match contract = Regex.Match(
            tree,
            @"^internal interface IElementNode\b[^\r\n]*",
            RegexOptions.CultureInvariant | RegexOptions.Multiline,
            TimeSpan.FromSeconds(1));

        Assert.True(contract.Success, "ElementTree.cs に IElementNode の宣言が見つかりません。");
        Assert.False(
            contract.Value.Contains("IDisposable", StringComparison.Ordinal),
            $"IElementNode が IDisposable を継承しています ({contract.Value.Trim()})。" +
            "UiaElementNode 自身は 1 文字も変わらずに disposable になります。");
    }

    /// <summary>
    /// <c>UiaElementNode.Release()</c> が**2 度呼ばれても 1 度しか解放しない**こと。
    ///
    /// <para>
    /// **これは実行時テストにできない。**<c>UiaElementNode</c> の構築には internal な
    /// COM 型 (<c>IUIAutomationElement</c>) が要り、手で書いた偽物を渡しても
    /// <c>UiaFactory.ReleaseUnique</c> は <c>rcw is ComObject</c> で弾くので
    /// **2 度目の解放に観測可能な差が出ない**。つまりこの不変条件を見られるのは
    /// ソースの形だけである — <see cref="ThePickerWrapperForwardsDisposalToTheHandleItWraps"/>
    /// と同じ壁で、退行確認もソース正規表現に留まる (docs/DESIGN.md §7)。
    /// </para>
    /// <para>
    /// なお冪等化は**保険**であって、二重解放の証拠があって入れたものではない。
    /// <c>ElementResolver.ReleaseLevels</c> の生き残り走査と
    /// <c>FakeElementTree.Release</c> の二重解放検出は、これが入っても**残す**。
    /// </para>
    /// </summary>
    [Fact]
    public void TheResolutionLayerNodeReleasesItsHandleAtMostOnce()
    {
        string node = ReadUiaElementNode();

        Match guarded = Regex.Match(
            node,
            @"if \(_releasable && Interlocked\.Exchange\(ref _released, 1\) == 0\)\s*\{\s*UiaFactory\.ReleaseUnique\(Element\);",
            RegexOptions.CultureInvariant,
            TimeSpan.FromSeconds(1));

        Assert.True(
            guarded.Success,
            "UiaElementNode.Release() が Interlocked による冪等化を失っています。" +
            "2 度呼ぶと FinalRelease が 2 度走り、まだ生きている参照を落とします " +
            "(そのとき初めて壊れるのは、解放とは無関係な別の呼び出し元です)。");
    }

    /// <summary>
    /// <c>UiaElementNode</c> の本体だけを切り出す。
    ///
    /// <para>
    /// <c>UiaElementTree.cs</c> には <c>UiaElementTree</c> も居る。切り出さずに
    /// ファイル全体へ掛けると、**別の型**に足されたファイナライザーで落ちて
    /// 失敗メッセージが嘘になる。
    /// </para>
    /// </summary>
    private static string ReadUiaElementNode()
    {
        string source = ReadSource("src", "UiaTrigger.Core", "Interop", "UiaElementTree.cs");

        Match declaration = Regex.Match(
            source,
            @"^internal sealed class UiaElementNode\b[^\r\n]*",
            RegexOptions.CultureInvariant | RegexOptions.Multiline,
            TimeSpan.FromSeconds(1));

        Assert.True(
            declaration.Success,
            "UiaElementTree.cs に UiaElementNode の宣言が見つかりません。" +
            "型が移動・改名されたのなら、docs/DESIGN.md §7 の不変条件も一緒に運んでください " +
            "(見つからないまま緑になると、守っているつもりのものが何も守られません)。");

        int start = declaration.Index;
        Match next = Regex.Match(
            source[(start + declaration.Length)..],
            @"^internal\b",
            RegexOptions.CultureInvariant | RegexOptions.Multiline,
            TimeSpan.FromSeconds(1));

        string body = next.Success ? source.Substring(start, declaration.Length + next.Index) : source[start..];

        // 終端の規約が変わると (file sealed class にする / 宣言の上に属性行を足す)
        // ^internal が空振りし、切り出しが黙って「ファイルの残り全部」になる。
        // そうなると UiaElementTree に足されたファイナライザーでこちらが落ち、
        // 失敗メッセージが嘘になる — この切り出しが防ぎたかったものそのものである
        Assert.False(
            body.Contains("class UiaElementTree", StringComparison.Ordinal),
            "UiaElementNode の切り出しが隣の型まで飲み込んでいます。" +
            "終端に使っている ^internal が空振りしています (宣言の書き方が変わった)。" +
            "このまま通すと、別の型に足されたファイナライザーで UiaElementNode の検査が " +
            "落ちるようになります。");

        return body;
    }
}
