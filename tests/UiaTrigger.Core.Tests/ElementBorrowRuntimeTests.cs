using System.Reflection;
using Xunit;

namespace UiaTrigger.Tests;

/// <summary>
/// 借用と解放の排他 (docs/DESIGN.md B10) の実行時テスト。
///
/// <para>
/// <see cref="ElementBorrowTests"/> の源泉テスト群 (ソースの形の固定) と対を成す。
/// 「借用中の明示 Dispose が FinalRelease を借用終了まで遅延する」は決定的に再現できる
/// 数少ない断面なので、ここだけは実行時に見る。GC タイミング依存の断面 (ファイナライザーとの
/// 競合) は依然テスト不能であり、そちらは源泉テストが縛る。
/// </para>
/// <para>
/// 実 UIA (セッション + ルート要素) を生成するため real-uia-lite で直列化する
/// (docs/TESTING.md §5 — 実 UIA を触る T1 の括り)。
/// </para>
/// </summary>
[Collection(RealUiaLiteTests.Name)]
public sealed class ElementBorrowRuntimeTests
{
    /// <summary>
    /// 借用中の明示 <c>Dispose</c> が FinalRelease を借用終了まで遅延すること (B10)。
    ///
    /// <para>
    /// 旧実装 (Dispose が無条件に即時 FinalRelease) では、借用スコープの中の
    /// <c>get_Cached*</c> が解放済み RCW への呼び出しになって落ちる — presenter の掃き出し
    /// (別スレッドの Dispose) がディスパッチャの進行中呼び出しと重なったときの壊れ方の、
    /// 決定的に再現できる最小形である。
    /// </para>
    /// </summary>
    [Fact]
    public async Task DisposeDuringABorrow_DefersTheComReleaseUntilTheBorrowEnds()
    {
        await using var session = new UiaSession();
        UiaElement element = await session.GetRootAsync();

        BorrowThenDisposeThenUse(element);

        // 借用が閉じた時点で、遅延されていた解放が実行されている (COM 参照は手放されている)
        object? raw = typeof(UiaElement)
            .GetField("_element", BindingFlags.NonPublic | BindingFlags.Instance)!
            .GetValue(element);
        Assert.Null(raw);
        Assert.Throws<ObjectDisposedException>(() => element.Borrow());

        static void BorrowThenDisposeThenUse(UiaElement element)
        {
            using UiaElement.Borrowed borrowed = element.Borrow();
            element.Dispose();
            Assert.True(element.IsDisposed);
            // 借用中は COM 要素がまだ生きていること。旧実装はここで
            // 解放済み RCW への呼び出しになり例外 (最悪はアクセス違反) になる
            borrowed.Element.get_CachedProcessId(out int processId);
            Assert.True(processId >= 0);
        }
    }

    /// <summary>解放要求後の新規借用は断られること (遅延解放の裏面)。</summary>
    [Fact]
    public async Task BorrowAfterDispose_Throws()
    {
        await using var session = new UiaSession();
        UiaElement element = await session.GetRootAsync();

        element.Dispose();

        Assert.Throws<ObjectDisposedException>(() => element.Borrow());
    }
}
