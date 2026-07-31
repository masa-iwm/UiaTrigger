// 「なぜ解決できないのか」をユーザー向けに組み立てる (docs/DESIGN.md A10)。
//
// 昇格したプロセスは非昇格クライアントから OpenProcess できないため、候補から静かに落ちる。
// 症状としては「トリガーが永久に解決しない」だけになり、ユーザーには手掛かりが無い。
// 数え上げは WindowCandidateCache が持っているので、文面の組み立てだけをここに切り出して
// 単体テストできるようにする (メッセージの組み立てに実 UIA は要らない)。
using UiaTrigger.Resources;

namespace UiaTrigger.Monitoring;

internal static class ResolutionDiagnostics
{
    /// <summary>
    /// 未解決の理由。参照できなかったプロセスがあればそれを添える。
    /// </summary>
    /// <param name="inaccessibleProcessCount">
    /// 実行ファイルパスを読めなかったプロセスの数
    /// (<see cref="Resolution.WindowCandidateCache.InaccessibleProcessCount"/>)。
    /// </param>
    public static string DescribeUnresolved(int inaccessibleProcessCount) =>
        inaccessibleProcessCount > 0
            ? Message.Format(Strings.Status_ResolveFailedInaccessibleProcesses, inaccessibleProcessCount)
            : Strings.Status_InitialResolveFailed;
}
