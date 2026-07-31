using System.Text.RegularExpressions;
using System.Xml.Linq;
using Xunit;

namespace UiaTrigger.Tests;

/// <summary>
/// D9 — サンプルホストが「ピッカー → 監視」の E2E を実際に配線していることの回帰
/// (docs/DESIGN.md D9 / §12)。
///
/// <para>
/// **ソースの形でしか見られない。**<c>UiaTrigger.App.WinUI</c> は WinUI3 / x64 の
/// 実行可能プロジェクトなので、このテストプロジェクトから参照できない
/// (<see cref="XamlLocalizationTests"/> の冒頭と同じ理由)。実際に起動して発火まで見るのは
/// T4 の仕事であり、そちらは対話デスクトップが要る。
/// </para>
/// <para>
/// ここで塞げるのは**腐る側**である。監視の配線は消しても**ビルドが通り、
/// 他のテストが 1 件も落ちない** — 画面から機能が消えるだけで、誰も気づかない。
/// <c>HostStringTests.EveryHostOffersToOpenASecondPicker</c> を置いたのと同じ理由である。
/// </para>
/// </summary>
public sealed class HostMonitorTests
{
    private const string WinUi = "src/UiaTrigger.App.WinUI";
    private const string Wpf = "src/UiaTrigger.App.Wpf";
    private const string WinForms = "src/UiaTrigger.App.WinForms";

    private static string Read(string relativePath)
    {
        string path = RepoPaths.Combine(relativePath.Split('/'));
        Assert.True(File.Exists(path), $"ファイルがありません: {path}");
        return File.ReadAllText(path);
    }

    /// <summary>
    /// どのホストも、もう 1 つピッカーを開く前に**既に開いているピッカーの自動選択を切る**こと
    /// (docs/DESIGN.md §12)。
    ///
    /// <para>
    /// カーソル位置はプロセスで 1 つなので、切らないと開いているピッカー全部が同じ点を捕捉する。
    /// **例外も警告も出ない** — 先に開いていたほうが新しいピッカーのマウスに黙って追随し、
    /// 出していた要素を失うだけである (確定済みの条件は無事)。呼び出しを消しても
    /// **ビルドは通り、他のテストは 1 件も落ちない**ので、ここに置く。
    /// </para>
    /// <para>
    /// **ソースの形しか見ていない。**「2 枚目を開くと 1 枚目が止まる」を実際に見るのは
    /// 人の担当である (docs/MANUAL-CHECKS.md §6)。
    /// </para>
    /// </summary>
    [Theory]
    [InlineData($"{WinUi}/MainWindow.xaml.cs")]
    [InlineData($"{Wpf}/MainWindow.xaml.cs")]
    [InlineData($"{WinForms}/MainForm.cs")]
    public void EveryHostStopsAutoSelectOnThePickersAlreadyOpen(string source)
    {
        Assert.Contains("StopAutoSelect()", Read(source), StringComparison.Ordinal);
    }

    /// <summary>WinUI ホストが監視を組み立て、3 つのイベントすべてを受けること。</summary>
    /// <remarks>
    /// <c>UnhandledException</c> を含めるのが肝である。ライブラリ自身の doc が
    /// 「トリガーが発火しない理由」として昇格アプリのような**他に出口の無いもの**を
    /// 挙げているので、そこを購読しないサンプルは**見えない失敗形を隠して実演する**ことになる。
    /// </remarks>
    [Theory]
    [InlineData("new TriggerMonitor(")]
    [InlineData("TriggerFired += ")]
    [InlineData("ResolutionChanged += ")]
    [InlineData("UnhandledException += ")]
    [InlineData("StartAsync(")]
    public void TheWinUiHostWiresMonitoring(string fragment)
    {
        Assert.Contains(fragment, Read($"{WinUi}/MainWindow.xaml.cs"), StringComparison.Ordinal);
    }

    /// <summary>
    /// WinUI ホストが複合条件を組み立てられること (docs/DESIGN.md §4)。
    ///
    /// <para>
    /// 上と同じ理由でここに置く。複合条件を作る口を消してもビルドは通り、他のテストは
    /// 1 件も落ちない — **ライブラリが持つ機能のうち、画面から作れるものだけが実演される**ので、
    /// 消えたことに気づくのは公開後の利用者になる。
    /// </para>
    /// <para>
    /// 組む規則そのものは <c>TriggerComposer</c> (Core) に在り、
    /// 挙動は <see cref="TriggerComposerTests"/> が直接固定する。ここに残る仕事は 2 つ:
    /// **ホストがその口へ委譲していること** (規則を写経し直したら落ちる)、そして
    /// **まとめた 1 件が走っている監視へ入ること** (ショーケースの E2E の後半)。
    /// </para>
    /// </summary>
    [Theory]
    [InlineData("TriggerComposer.Compose(")]
    [InlineData("_ = ReregisterAsync(composite)")]
    public void TheWinUiHostBuildsCompositeConditions(string fragment)
    {
        Assert.Contains(fragment, Read($"{WinUi}/MainWindow.xaml.cs"), StringComparison.Ordinal);
    }

    /// <summary>
    /// 監視のイベントが UI スレッドへ渡し直されること。
    ///
    /// <para>
    /// 発火・解決状態・例外は 3 つとも**単一のバックグラウンドワーカー**上で配られる
    /// (<c>TriggerMonitor.TriggerFired</c> の remarks)。そこから直接 UI に触るのが
    /// このサンプルで最も写されやすい間違いなので、<c>TryEnqueue</c> の存在を固定する。
    /// </para>
    /// </summary>
    [Fact]
    public void TheWinUiHostMarshalsMonitorEventsOntoTheUiThread()
    {
        string source = Read($"{WinUi}/MainWindow.xaml.cs");

        // ログを積む唯一の口が TryEnqueue を通ること
        Match appendLog = Regex.Match(
            source,
            @"private\s+void\s+AppendLog\([^)]*\)[\s\S]{0,200}?DispatcherQueue\.TryEnqueue",
            RegexOptions.CultureInvariant,
            TimeSpan.FromSeconds(5));

        Assert.True(
            appendLog.Success,
            "AppendLog が DispatcherQueue.TryEnqueue を通っていません。" +
            "監視のイベントはバックグラウンドのワーカー上で来るので、直接 UI に触ると壊れます。");
    }

    /// <summary>
    /// 走らせたまま 1 件を入れ替えるとき、**外してから足す**こと。
    ///
    /// <para>
    /// ピッカーの確定は「同じ id を録り直す」経路を普通に通り、<c>AddAsync</c> は
    /// id が重複すると <see cref="ArgumentException"/> を投げる。順序を逆にすると
    /// **普通の操作で必ず例外になる**のに、ここは <c>async</c> の投げっぱなしなので
    /// 誰も観測しない faulted Task で終わる。
    /// </para>
    /// </summary>
    [Fact]
    public void TheWinUiHostRemovesBeforeItAddsWhenReregistering()
    {
        string source = Read($"{WinUi}/MainWindow.xaml.cs");

        Match ordered = Regex.Match(
            source,
            @"RemoveAsync\(definition\.Id\)[\s\S]{0,200}?AddAsync\(definition\)",
            RegexOptions.CultureInvariant,
            TimeSpan.FromSeconds(5));

        Assert.True(
            ordered.Success,
            "登録し直しが RemoveAsync → AddAsync の順になっていません。" +
            "同じ id を録り直すのは普通の操作なので、逆順だと AddAsync が必ず投げます。");
    }

    /// <summary>監視の操作口とログが XAML に在ること (名前は T4 が後で使う)。</summary>
    [Theory]
    [InlineData("StartMonitorButton")]
    [InlineData("StopMonitorButton")]
    [InlineData("MonitorLogList")]
    public void TheWinUiHostShowsTheMonitoringControls(string name)
    {
        XNamespace x = "http://schemas.microsoft.com/winfx/2006/xaml";
        string path = RepoPaths.Combine($"{WinUi}/MainWindow.xaml".Split('/'));

        HashSet<string> names = [.. XDocument.Load(path).Descendants()
            .Select(e => e.Attribute(x + "Name")?.Value)
            .Where(v => !string.IsNullOrEmpty(v))
            .Select(v => v!)];

        Assert.Contains(name, names);
    }

    /// <summary>
    /// WPF / Windows Forms のホストは監視を持たない — **意図された非対称である**。
    ///
    /// <para>
    /// README の "Two asymmetries are deliberate" が
    /// 「<c>App.WinUI</c> だけがショーケースを兼ねる」と書いており、
    /// **その README は NuGet パッケージに同梱されて nuget.org で読まれる**。
    /// 3 ホストへ広げるのは構わないが、そのときは README (英語・日本語) と
    /// 3 つの csproj のコメントを**一緒に**直さなければ、文書だけが古くなる。
    /// </para>
    /// </summary>
    [Theory]
    [InlineData($"{Wpf}/MainWindow.xaml.cs")]
    [InlineData($"{WinForms}/MainForm.cs")]
    public void TheOtherHostsDeliberatelyStopAtThePicker(string source)
    {
        Assert.DoesNotContain("new TriggerMonitor(", Read(source), StringComparison.Ordinal);
    }
}
