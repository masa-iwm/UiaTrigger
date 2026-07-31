// T4: 区切りが<b>実際に分けたい 2 つの間に立っている</b>こと (docs/DESIGN.md §12)。
//
// <b>ここでしか見られないもの:</b> WinUI3 の GridSplitter の繋ぎ。
// WPF 側は T1 (TriggerPickerWpfWindowTests) が Grid.Column / ResizeDirection を直接見られるが、
// WinUI3 の View は x64 / WindowsAppSDK のため T1 から組み立てられない。つまり
// 「Grid.Column を取り違えた」「ResizeDirection の推測が外れた」は、<b>実物を起こして
// 位置を測る以外に見る手立てが無い</b>。どちらも例外にならず、置いたのに効かないだけである。
// ホストの区切り (ListSplitter) も同じ理由でここに在る — 加えて、ホストのリソースは
// S4 の対象外なので、読み上げ名が実際に解決することもここでしか見ていない。
//
// Windows Forms は入れない。SplitContainer は<b>それ自身が両側を含む入れ物</b>として
// UIA に出るので「間に立っている」という形にならず、代わりに T1 が
// SplitterDistance / Panel*MinSize / IsSplitterFixed を直接見ている
// (AllNames に WinForms が居ないのも同じ趣旨である)。
//
// 起動は StartWithoutATarget である。StartForLabels ではない —
// あちらは発行レイアウトを優先するので、XAML を直しても<b>発行し直すまで古い配置</b>を
// 通し続ける。発行物を見てよいのは、それを主張しているテスト (S4) だけである。
//
// <b>掴んで動かせるかどうかはここでは見ない。</b>ドラッグは合成入力であり T5 の担当で、
// キーボードでの寸法変更も打鍵が要る。MANUAL-CHECKS §4.3.1 に項目として置いた。
using System.Globalization;
using System.Windows;
using System.Windows.Automation;
using Xunit;

namespace UiaTrigger.Picker.UiTests;

public sealed class SplitterTests
{
    /// <summary>区切りとして通る幅の上限。掴み代なので細いはずである。</summary>
    /// <remarks>
    /// <b>実測 (100% / DPI 96)</b>: WPF が <b>6px</b>、WinUI が <b>12px</b>
    /// (「WinUI は Sizers の既定 (16px)」という推測は実測で外れている)。
    /// DPI でスケールするので上限には余裕を持たせる。
    /// これが緩いと「区切りのつもりが列そのもの」を通してしまうが、
    /// 位置 (ツリーと右側の間) のほうが本命の assert なので、ここは形の粗い確認でよい。
    /// </remarks>
    private const double MaxSplitterWidth = 60;

    /// <summary>
    /// 掴み代として成立する下限 (docs/DESIGN.md §12)。
    /// </summary>
    /// <remarks>
    /// <b>上限だけでは「見えているが掴めない」を通してしまう。</b><c>&gt; 0</c> だけだと
    /// 0.5px の区切りでも緑になる。「動かせない」という報告を受けたら、まず疑うのが
    /// この太さである (WinUI は実測 12px で、太さは原因ではないと分かっている)。
    /// 4px は 3 変種でいちばん細い Windows Forms の <c>SplitterWidth</c> の既定値である。
    /// </remarks>
    private const double MinSplitterWidth = 4;

    /// <summary>
    /// 区切りがツリーと右側の<b>間</b>に、縦いっぱいに立っていること。
    /// </summary>
    /// <remarks>
    /// <para>
    /// 列を取り違えて置くと、区切りはツリーの左や右端の外に出る。<b>それでも例外は出ず、
    /// 掴んでも隣り合っていない 2 列を押し合うだけ</b>なので、位置で見るのが直接的である。
    /// </para>
    /// <para>
    /// 退行: XAML の <c>Grid.Column</c> を 1 から 0 か 2 へ動かす → 位置の assert が落ちる。
    /// </para>
    /// </remarks>
    [Theory]
    [MemberData(nameof(PickerHostProfile.AllNames), MemberType = typeof(PickerHostProfile))]
    public void TheSplitterStandsBetweenTheTreeAndTheRightHandSide(string profileName)
    {
        PickerHostProfile profile = PickerHostProfile.ByName(profileName);
        using PickerHostProcess host = PickerHostProcess.StartWithoutATarget(profile, "en-US");
        host.OpenPicker();

        AutomationElement picker = host.PickerWindow();
        Rect tree = picker.RequireById(profile.TreeAutomationId).Current.BoundingRectangle;
        Rect props = picker.RequireById("PropsList").Current.BoundingRectangle;
        Rect splitter = picker.RequireByIdEventually("TreeSplitter", host.Diagnostics)
            .Current.BoundingRectangle;

        string measured = string.Create(
            CultureInfo.InvariantCulture,
            $"ツリー={tree} 区切り={splitter} プロパティ一覧={props}");

        Assert.True(
            splitter.Width is >= MinSplitterWidth and <= MaxSplitterWidth,
            $"区切りの幅が掴み代の範囲にありません。{measured}");

        // 左右の順に並んでいること。矩形は隣接するので境界は緩めに見る
        Assert.True(splitter.Left >= tree.Right - 1, $"区切りがツリーの右側にありません。{measured}");
        Assert.True(splitter.Right <= props.Left + 1, $"区切りがプロパティ一覧の左側にありません。{measured}");

        // 縦は両側と同じくらい伸びていること (Stretch を落とすと点のような掴み代になる)
        Assert.True(
            splitter.Height >= tree.Height * 0.8,
            $"区切りが縦に伸びていません。{measured}");
    }

    /// <summary>
    /// プロパティ一覧と条件欄の区切りが、その<b>間</b>に横いっぱいに立っていること。
    /// </summary>
    /// <remarks>
    /// 上下の分割なので、取り違えの向きも上下である。行を間違えれば条件欄の下や
    /// プロパティ一覧の上に出るが、<b>やはり例外にはならない</b>。
    /// </remarks>
    [Theory]
    [MemberData(nameof(PickerHostProfile.AllNames), MemberType = typeof(PickerHostProfile))]
    public void TheSplitterStandsBetweenThePropertiesAndTheConditionFields(string profileName)
    {
        PickerHostProfile profile = PickerHostProfile.ByName(profileName);
        using PickerHostProcess host = PickerHostProcess.StartWithoutATarget(profile, "en-US");
        host.OpenPicker();

        AutomationElement picker = host.PickerWindow();
        Rect props = picker.RequireById("PropsList").Current.BoundingRectangle;
        Rect heading = picker.RequireById("ConditionHeading").Current.BoundingRectangle;
        Rect splitter = picker.RequireByIdEventually("ConditionSplitter", host.Diagnostics)
            .Current.BoundingRectangle;

        string measured = string.Create(
            CultureInfo.InvariantCulture,
            $"プロパティ一覧={props} 区切り={splitter} 条件欄の見出し={heading}");

        Assert.True(
            splitter.Height is >= MinSplitterWidth and <= MaxSplitterWidth,
            $"区切りの高さが掴み代の範囲にありません。{measured}");

        Assert.True(splitter.Top >= props.Bottom - 1, $"区切りがプロパティ一覧の下にありません。{measured}");
        Assert.True(splitter.Bottom <= heading.Top + 1, $"区切りが条件欄の上にありません。{measured}");

        // 横は両側と同じくらい伸びていること
        Assert.True(
            splitter.Width >= props.Width * 0.8,
            $"区切りが横に伸びていません。{measured}");
    }

    /// <summary>
    /// <c>App.WinUI</c> ホストの 2 つの一覧の間にも区切りが立っていること。
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>ここだけホストの窓を見る。</b>ログ一覧を持つのは D9 の非対称で <c>App.WinUI</c> だけであり
    /// (docs/DESIGN.md §12)、割る境界そのものが他の 2 ホストには無い。
    /// ピッカーを開く必要も無い — 一覧は最初から窓に在る。
    /// </para>
    /// <para>
    /// これが無いと、ホスト側の区切りは<b>どのテストにも見られない</b>状態になる。
    /// T1 は WinUI を組み立てられず、<c>HostStringTests</c> が見るのはキーの集合だけで
    /// 実行時の解決も配置も見ていない。
    /// </para>
    /// </remarks>
    [Fact]
    public void TheHostSplitterStandsBetweenTheTwoLists()
    {
        using PickerHostProcess host = PickerHostProcess.StartWithoutATarget(PickerHostProfile.WinUI, "en-US");

        // 3 つとも「出てくるまで待つ」で引く。窓は開いていても、中の要素が UIA に
        // 出そろうまでには間がある (Ui.RequireByIdEventually の remarks)
        AutomationElement main = host.MainWindow;
        Rect triggers = main.RequireByIdEventually("TriggerList", host.Diagnostics)
            .Current.BoundingRectangle;
        Rect log = main.RequireByIdEventually("MonitorLogList", host.Diagnostics)
            .Current.BoundingRectangle;
        Rect splitter = main.RequireByIdEventually("ListSplitter", host.Diagnostics)
            .Current.BoundingRectangle;

        string measured = string.Create(
            CultureInfo.InvariantCulture,
            $"トリガー一覧={triggers} 区切り={splitter} ログ一覧={log}");

        Assert.True(
            splitter.Height is >= MinSplitterWidth and <= MaxSplitterWidth,
            $"区切りの高さが掴み代の範囲にありません。{measured}");

        Assert.True(splitter.Top >= triggers.Bottom - 1, $"区切りがトリガー一覧の下にありません。{measured}");
        Assert.True(splitter.Bottom <= log.Top + 1, $"区切りがログ一覧の上にありません。{measured}");
        Assert.True(splitter.Width >= triggers.Width * 0.8, $"区切りが横に伸びていません。{measured}");

        // 読み上げ名が解決していること。ピッカー側は S4 が発行レイアウトで見るが、
        // ホストのリソースは S4 の対象外なので、ここで見ないと誰も見ない
        string name = main.RequireByIdEventually("ListSplitter", host.Diagnostics).Current.Name;
        Assert.False(string.IsNullOrEmpty(name), "区切りに読み上げ名がありません。");
        Assert.NotEqual("ListSplitter", name);
    }
}
