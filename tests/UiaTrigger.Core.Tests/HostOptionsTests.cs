using UiaTrigger.App.Shared;
using Xunit;

namespace UiaTrigger.Tests;

/// <summary>
/// サンプルホスト 3 つが共有するコマンドライン解析 (docs/DESIGN.md §12)。
///
/// <para>
/// 解析は <c>HostCommandLine.Parse</c> という状態を持たない関数に出してあるので、
/// **プロセスの状態に触らずに**確かめられる。<c>HostOptions</c> の static な置き場を
/// 直接叩くテストは書かない — あれはプロセスに 1 つしかなく、T1 は並列で走る。
/// </para>
/// <para>
/// 「<c>Initialize</c> の呼び忘れ」はソースの形で見る
/// (<see cref="EveryHostInitializesBeforeItCreatesAWindow"/>)。呼び忘れると
/// <c>--triggers</c> が効かないまま**開発機の実トリガーファイルを書き換える**ので、
/// 静かな既定値に落ちる形にはしていない。
/// </para>
/// </summary>
public sealed class HostOptionsTests
{
    private static readonly Action<string> Ignore = _ => { };

    /// <summary><c>--pick-at</c> は繰り返せる。**n 枚目が n 番目**を受け取る前提である。</summary>
    [Fact]
    public void ItReadsEveryPickAtInOrder()
    {
        HostCommandLine parsed = HostCommandLine.Parse(
            ["host.exe", "--pick-at", "10,20", "--pick-at", "30,40"],
            Ignore);

        Assert.Equal(2, parsed.Cursors.Count);
        Assert.True(parsed.Cursors[0].TryGetPosition(out int x0, out int y0));
        Assert.Equal((10, 20), (x0, y0));
        Assert.True(parsed.Cursors[1].TryGetPosition(out int x1, out int y1));
        Assert.Equal((30, 40), (x1, y1));
    }

    /// <summary>
    /// 座標は**必ず invariant で解釈する** (docs/LOCALIZATION.md §3 の 2 番)。
    /// </summary>
    /// <remarks>
    /// 表示ではなく引数の解釈なので、機械の地域設定で意味が変わってはならない。
    /// ここが現在のカルチャに従うと、小数点にカンマを使う地域で
    /// <c>--pick-at 10,20</c> が「10.20」という 1 つの数に見えうる。
    /// </remarks>
    [Theory]
    [InlineData("en-US")]
    [InlineData("de-DE")]
    public void ItReadsCoordinatesTheSameWayInEveryCulture(string culture)
    {
        using var _ = new CultureScope(culture);

        HostCommandLine parsed = HostCommandLine.Parse(["host.exe", "--pick-at", "1670,676"], Ignore);

        Assert.True(Assert.Single(parsed.Cursors).TryGetPosition(out int x, out int y));
        Assert.Equal((1670, 676), (x, y));
    }

    /// <summary>
    /// 壊れた <c>--pick-at</c> は**理由をログに残して**飛ばすこと。
    /// </summary>
    /// <remarks>
    /// 黙って通常動作 (実マウスに追随) に落ちると、症状は「捕捉が起きない」だけになり、
    /// 原因が読めない。ログの出口を <c>Parse</c> の**引数**にしてあるのは、
    /// 共有版で「ログの配線を忘れる」形を作らないためである。
    /// </remarks>
    [Theory]
    [InlineData("10")]
    [InlineData("10,20,30")]
    [InlineData("x,20")]
    [InlineData("")]
    public void ItLogsWhyItSkippedABrokenPickAt(string value)
    {
        var logged = new List<string>();

        HostCommandLine parsed = HostCommandLine.Parse(["host.exe", "--pick-at", value], logged.Add);

        Assert.Empty(parsed.Cursors);
        Assert.Contains(logged, line => line.Contains("--pick-at", StringComparison.Ordinal));
    }

    /// <summary><c>--triggers</c> と <c>--culture</c> は値をそのまま渡す。</summary>
    [Fact]
    public void ItReadsTheTriggerFileAndTheCulture()
    {
        HostCommandLine parsed = HostCommandLine.Parse(
            ["host.exe", "--triggers", @"C:\tmp\t.json", "--culture", "ja-JP"],
            Ignore);

        Assert.Equal(@"C:\tmp\t.json", parsed.TriggerFile);
        Assert.Equal("ja-JP", parsed.Culture);
    }

    /// <summary>指定が無ければ null / 空である (= 通常動作)。</summary>
    [Fact]
    public void ItFallsBackToTheNormalBehaviourWhenNothingIsGiven()
    {
        HostCommandLine parsed = HostCommandLine.Parse(["host.exe"], Ignore);

        Assert.Empty(parsed.Cursors);
        Assert.Null(parsed.TriggerFile);
        Assert.Null(parsed.Culture);
        Assert.Null(parsed.Placement);
    }

    /// <summary><c>--place-windows</c> は 4 つの整数を**物理ピクセル**として読む。</summary>
    [Fact]
    public void ItReadsThePlacementRectangle()
    {
        HostCommandLine parsed = HostCommandLine.Parse(
            ["host.exe", "--place-windows", "0,0,1516,980"],
            Ignore);

        Assert.Equal(new WindowPlacement(0, 0, 1516, 980), parsed.Placement);
    }

    /// <summary>
    /// 壊れた <c>--place-windows</c> は**理由をログに残して**無指定に落ちること。
    /// </summary>
    /// <remarks>
    /// 黙って落ちると症状は「窓が退かない」だけになり、T4 からは pick 点の被覆として
    /// 見えることになる。原因が 2 段離れるので、ここで名指ししておく。
    /// 幅と高さが 0 以下のものも弾く — <c>SetWindowPos</c> に渡すと窓が潰れ、
    /// 「窓が消えた」という読めない症状になる。
    /// </remarks>
    [Theory]
    [InlineData("0,0,1516")]
    [InlineData("0,0,1516,980,7")]
    [InlineData("0,0,x,980")]
    [InlineData("0,0,0,980")]
    [InlineData("0,0,1516,-1")]
    public void ItLogsWhyItSkippedABrokenPlacement(string value)
    {
        var logged = new List<string>();

        HostCommandLine parsed = HostCommandLine.Parse(["host.exe", "--place-windows", value], logged.Add);

        Assert.Null(parsed.Placement);
        Assert.Contains(logged, line => line.Contains("--place-windows", StringComparison.Ordinal));
    }

    /// <summary>矩形も**必ず invariant で解釈する**。</summary>
    [Theory]
    [InlineData("en-US")]
    [InlineData("de-DE")]
    public void ItReadsThePlacementTheSameWayInEveryCulture(string culture)
    {
        using var _ = new CultureScope(culture);

        HostCommandLine parsed = HostCommandLine.Parse(
            ["host.exe", "--place-windows", "1,2,1516,980"],
            Ignore);

        Assert.Equal(new WindowPlacement(1, 2, 1516, 980), parsed.Placement);
    }

    /// <summary>値の無い <c>--triggers</c> は指定されなかったものとして扱う。</summary>
    /// <remarks>
    /// **既定の実ファイルへ落ちる**ので、ここを取り違えると自動テストが開発機の
    /// トリガーを書き換える。末尾に来た場合と空文字の場合の両方を見る。
    /// </remarks>
    [Fact]
    public void ItTreatsAValuelessTriggersOptionAsAbsent()
    {
        // 末尾に来て値が無い
        Assert.Null(HostCommandLine.Parse(["host.exe", "--triggers"], Ignore).TriggerFile);
        // 値が空文字
        Assert.Null(HostCommandLine.Parse(["host.exe", "--triggers", ""], Ignore).TriggerFile);
    }

    /// <summary>
    /// どのホストも、**ウィンドウを 1 つも作る前に** <c>HostOptions.Initialize</c> を呼ぶこと。
    /// </summary>
    /// <remarks>
    /// <para>
    /// 3 ホストが <c>HostOptions.cs</c> を写しで持っていたのをやめて共有プロジェクトへ
    /// 移したとき、写しを保っていた側の言い分は「共有版は <c>Initialize()</c> の呼び忘れを
    /// 新しく作る」だった。**その失敗形をここで塞ぐ。**
    /// </para>
    /// <para>
    /// 呼び忘れると <c>HostOptions</c> は例外で落ちるが、それは実行しないと分からない。
    /// <c>HostMonitorTests</c> と同じくソースの形で固定するのは、ホストの実行ファイルが
    /// T1 から参照できないためである。
    /// </para>
    /// </remarks>
    [Theory]
    [InlineData("src/UiaTrigger.App.WinUI/App.xaml.cs")]
    [InlineData("src/UiaTrigger.App.Wpf/App.xaml.cs")]
    [InlineData("src/UiaTrigger.App.WinForms/Program.cs")]
    public void EveryHostInitializesBeforeItCreatesAWindow(string source)
    {
        string path = RepoPaths.Combine(source.Split('/'));
        Assert.True(File.Exists(path), $"ファイルがありません: {path}");
        string text = File.ReadAllText(path);

        int initialize = text.IndexOf("HostOptions.Initialize(", StringComparison.Ordinal);
        Assert.True(
            initialize >= 0,
            $"{source} が HostOptions.Initialize を呼んでいません。" +
            "呼ばないと --triggers が効かず、自動テストが開発機の実トリガーファイルを書き換えます。");

        // カルチャの上書きは「どのウィンドウを作るより前」でなければ効かない
        // (MrtPickerStrings.Loader は一度読んだら決着している — docs/DESIGN.md §12)。
        // Initialize はその ApplyCulture より前に来る必要がある
        int applyCulture = text.IndexOf("ApplyCulture()", StringComparison.Ordinal);
        Assert.True(
            applyCulture > initialize,
            $"{source} で HostOptions.Initialize が ApplyCulture より後に来ています。" +
            "解析より先にカルチャを当てようとしても、読む値がまだありません。");
    }

    /// <summary>現在のカルチャを一時的に差し替える。</summary>
    private sealed class CultureScope : IDisposable
    {
        private readonly System.Globalization.CultureInfo _previous =
            System.Globalization.CultureInfo.CurrentCulture;

        public CultureScope(string name)
            => System.Globalization.CultureInfo.CurrentCulture = new System.Globalization.CultureInfo(name);

        public void Dispose() => System.Globalization.CultureInfo.CurrentCulture = _previous;
    }
}
