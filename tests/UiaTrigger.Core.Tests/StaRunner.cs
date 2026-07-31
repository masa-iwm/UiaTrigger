// WPF / WinForms のコントロールを組み立てるための STA スレッド (docs/TESTING.md §1 T1)。
//
// xunit v3 (3.0.0) に [STAFact] は無いので自前で立てる。テスト本体を丸ごと STA スレッドで
// 走らせ、例外は呼び出し側へ運び直す。
//
// 「T1 から参照できない」の原因は WinUI3 / x64 / WindowsAppSDK であって
// 「View だから」ではない (docs/DESIGN.md §12)。
// net10.0-windows / AnyCPU の WPF・WinForms View は**普通にプロジェクト参照できる**。
using Xunit;

namespace UiaTrigger.Tests;

internal static class Sta
{
    /// <summary>STA スレッドで実行し、終わるまで待つ。中で出た例外はそのまま伝える。</summary>
    public static void Run(Action action)
    {
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            try
            {
                action();
            }
            catch (Exception ex)
            {
                failure = ex;
            }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.IsBackground = true;
        thread.Start();

        // 固まったまま CI を止めないよう上限を切る。GUI を出さない範囲なので本来は一瞬で終わる
        Assert.True(thread.Join(TimeSpan.FromSeconds(60)), "STA スレッドが 60 秒で終わりませんでした。");
        if (failure is not null)
        {
            throw new InvalidOperationException($"STA スレッドで例外が出ました: {failure}", failure);
        }
    }
}
