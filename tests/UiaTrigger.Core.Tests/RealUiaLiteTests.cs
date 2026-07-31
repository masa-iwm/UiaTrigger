using Xunit;

namespace UiaTrigger.Tests;

/// <summary>
/// T1 のうち <b>実際に UI Automation を生成する</b> テストをまとめる。
///
/// これらは可視のデスクトップも対象アプリも要らない (定義の検証・トリガーの増減・診断だけを見る)
/// ため T3 には置かない。ただし <c>CUIAutomation8</c> の生成と <c>GetRootElement</c> は
/// 並列に何本も走らせると単発で <c>COMException</c> になることがある — 実測で
/// <c>GetRootElement</c> が 1 回だけ失敗した。
///
/// 「ときどき落ちるテスト」は検出力を失ったテストと同じなので、この一群は直列に走らせる。
/// 並列実行を止めるのはこの collection の中だけで、T1 全体の並列性には影響しない。
/// </summary>
[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class RealUiaLiteTests
{
    public const string Name = "real-uia-lite";
}
