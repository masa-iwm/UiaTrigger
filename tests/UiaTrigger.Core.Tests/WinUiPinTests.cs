using System.Text.RegularExpressions;
using Xunit;

namespace UiaTrigger.Tests;

/// <summary>
/// WinUI 3 の 2 つの pin が、2 つの csproj でずれていないこと (docs/DESIGN.md §12 / A23)。
///
/// <para>
/// <c>WindowsSdkPackageVersion</c> は <c>CsWinRTAotWarningLevel</c> を効かせるための前提で、
/// 後者は「AOT で壊れる WinRT の使い方」をビルドで止めるためのものである
/// (A23 = WinUI の GridSplitter が発行物で動かない、がその実例)。
/// **片方の csproj だけ更新すると、更新し忘れた側で検出が黙って外れる** — 症状は
/// 「発行して人が掴んで初めて分かる」ので、ずれたことに気づく手掛かりが他に無い。
/// </para>
/// <para>
/// 一元化できないので値の一致で縛る: <c>Directory.Build.props</c> は csproj 本体より先に
/// 評価されるため <c>UseWinUI</c> の条件が常に偽になり、<c>Directory.Build.targets</c> は
/// restore の段に間に合わない (props 側にその記録がある)。
/// </para>
/// </summary>
public sealed class WinUiPinTests
{
    /// <summary>WinUI 3 を使う 2 つのプロジェクト。**片方だけを載せると検査の意味が消える。**</summary>
    public static TheoryData<string, string> WinUiProjects =>
    [
        ("UiaTrigger.Picker.WinUI", "UiaTrigger.Picker.WinUI.csproj"),
        ("UiaTrigger.App.WinUI", "UiaTrigger.App.WinUI.csproj"),
    ];

    private static string Read(string project, string file)
        => File.ReadAllText(RepoPaths.Combine("src", project, file));

    private static string? ValueOf(string xml, string property)
    {
        Match match = Regex.Match(
            xml,
            $@"<{Regex.Escape(property)}>(?<value>[^<]+)</{Regex.Escape(property)}>",
            RegexOptions.CultureInvariant,
            TimeSpan.FromSeconds(1));
        return match.Success ? match.Groups["value"].Value.Trim() : null;
    }

    /// <summary>
    /// 両方のプロジェクトが 2 つの pin を持っていること。
    /// **「無いこと」で緑にならないように、まず在ることを固定する** — 消えた側は
    /// SDK 既定 (10.0.19041.57) に落ち、そこの CsWinRT は警告レベルを知らないので、
    /// 「設定はあるのに何も出ない」状態になる。
    /// </summary>
    [Theory]
    [MemberData(nameof(WinUiProjects))]
    public void EveryWinUiProjectPinsTheSdkAndTheAotWarningLevel(string project, string file)
    {
        string xml = Read(project, file);

        Assert.NotNull(ValueOf(xml, "WindowsSdkPackageVersion"));
        Assert.NotNull(ValueOf(xml, "CsWinRTAotWarningLevel"));
    }

    /// <summary>2 つのプロジェクトで pin の値が一致すること (この検査の本体)。</summary>
    [Fact]
    public void TheTwoWinUiProjectsPinTheSameValues()
    {
        string picker = Read("UiaTrigger.Picker.WinUI", "UiaTrigger.Picker.WinUI.csproj");
        string app = Read("UiaTrigger.App.WinUI", "UiaTrigger.App.WinUI.csproj");

        Assert.Equal(
            ValueOf(picker, "WindowsSdkPackageVersion"),
            ValueOf(app, "WindowsSdkPackageVersion"));
        Assert.Equal(
            ValueOf(picker, "CsWinRTAotWarningLevel"),
            ValueOf(app, "CsWinRTAotWarningLevel"));
    }

    /// <summary>
    /// 警告レベルが 3 (最も厳しい) であること。下げると A23 型の不具合がビルドを通り抜ける。
    /// </summary>
    [Theory]
    [MemberData(nameof(WinUiProjects))]
    public void TheAotWarningLevelIsTheStrictestOne(string project, string file)
    {
        Assert.Equal("3", ValueOf(Read(project, file), "CsWinRTAotWarningLevel"));
    }
}
