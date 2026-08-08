using System.Globalization;
using UiaTrigger.Models;
using UiaTrigger.Monitoring;
using UiaTrigger.Resolution;
using UiaTrigger.Resources;
using Xunit;

namespace UiaTrigger.Tests;

/// <summary>
/// A10: 昇格プロセスを参照できなかったことが、未解決の理由として呼び出し元に届くこと。
///
/// <c>OpenProcess</c> の失敗を静かに握り潰して候補から落とすと、症状は
/// 「トリガーが永久に解決しない」だけになり、ユーザーにも呼び出し元にも手掛かりが残らない。
/// </summary>
public sealed class ResolutionDiagnosticsTests
{
    private static readonly ResolverOptions Options = new();

    /// <summary>参照できなかったプロセスを数えていること。</summary>
    [Fact]
    public void Candidates_CountTheProcessesItCouldNotInspect()
    {
        var source = new FakeWindowSource(
                new WindowInfo(1, 10, "ClassA", "A"),
                new WindowInfo(2, 20, "ClassB", "B"),
                new WindowInfo(3, 30, "ClassC", "C"))
            .WithProcess(10, @"C:\apps\wanted.exe");
        // 20 / 30 はパスを登録しない = OpenProcess が失敗した昇格プロセス相当
        var cache = new WindowCandidateCache(source, Options);

        IReadOnlyList<nint> candidates = cache.GetCandidates(new WindowIdentity { ProcessName = "wanted.exe" });

        Assert.Equal([(nint)1], candidates);
        Assert.Equal(2, cache.InaccessibleProcessCount);
    }

    /// <summary>すべて参照できたときは 0 のままであること (数え方が常に増えるだけでないこと)。</summary>
    [Fact]
    public void Candidates_CountNothingWhenEveryProcessIsReadable()
    {
        var source = new FakeWindowSource(
                new WindowInfo(1, 10, "ClassA", "A"),
                new WindowInfo(2, 20, "ClassB", "B"))
            .WithProcess(10, @"C:\apps\wanted.exe")
            .WithProcess(20, @"C:\apps\other.exe");
        var cache = new WindowCandidateCache(source, Options);

        cache.GetCandidates(new WindowIdentity { ProcessName = "wanted.exe" });

        Assert.Equal(0, cache.InaccessibleProcessCount);
    }

    /// <summary>
    /// 参照できないプロセスがあった場合だけ、理由付きのメッセージになること。
    /// 文面そのものを見る — 「昇格しているのでは」という手掛かりが落ちたら気づけるように。
    /// </summary>
    [Fact]
    public void DescribeUnresolved_MentionsTheSkippedProcesses()
    {
        // 文面そのものを見るので、実行環境の表示言語に左右されないよう neutral に固定する
        using (CultureScope.Enter("en-US"))
        {
            string plain = ResolutionDiagnostics.DescribeUnresolved(0);
            string withElevated = ResolutionDiagnostics.DescribeUnresolved(3);

            Assert.Equal(Strings.Status_InitialResolveFailed, plain);
            Assert.NotEqual(plain, withElevated);
            Assert.Contains("3", withElevated, StringComparison.Ordinal);
            Assert.Contains("elevated", withElevated, StringComparison.OrdinalIgnoreCase);
        }
    }

    /// <summary>診断メッセージはユーザー向け表示なのでローカライズされること (docs/LOCALIZATION.md §3)。</summary>
    [Fact]
    public void DescribeUnresolved_IsLocalized()
    {
        string japanese;
        using (CultureScope.Enter("ja-JP"))
        {
            japanese = ResolutionDiagnostics.DescribeUnresolved(2);
        }

        string english;
        using (CultureScope.Enter("en-US"))
        {
            english = ResolutionDiagnostics.DescribeUnresolved(2);
        }

        Assert.NotEqual(english, japanese);
        Assert.Contains("2", japanese, StringComparison.Ordinal);
    }
}
