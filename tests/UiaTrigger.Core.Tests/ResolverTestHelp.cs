using UiaTrigger.Models;
using UiaTrigger.Resolution;

namespace UiaTrigger.Tests;

/// <summary>
/// 解決の検査は「1 つの定義 = 1 つの要素」の形で書かれている。
///
/// <see cref="ElementResolver.Resolve"/> は (ウィンドウ, 経路) の組を受ける —
/// 1 つのトリガーが複数の要素にまたがれるので、解決の単位はトリガーではない (docs/DESIGN.md §4)。
/// 検査そのものはビーム探索の性質を見ており、そこは変わっていないので、
/// 組み立て直すだけの薄い橋をここに置く。
/// </summary>
internal static class ResolverTestHelp
{
    public static ResolvedTarget? ResolveWith(
        IElementTree tree, WindowCandidateCache windows, TriggerDefinition definition, ResolverOptions options) =>
        ElementResolver.Resolve(tree, windows, definition.Window, definition.Locator, options);
}
