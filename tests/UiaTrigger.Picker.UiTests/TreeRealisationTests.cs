// T4: 行のコントロールが生成されるのを待つ経路 (docs/TESTING.md §1 T4 / docs/DESIGN.md §12)。
//
// ここでしか見られないもの:
//   ・WinUI の ExpandThenSelect / SelectDeferred (ContainerFromItem に 40 回 / 20 回リトライ)
//   ・WPF の同経路 (ItemContainerGenerator) と IsExpanded の TwoWay 束縛
// どちらも<b>ウィンドウを画面に出さないと成立しない</b>ことを実測している
// (Measure/Arrange では ItemContainerGenerator が何も返さない — docs/DESIGN.md §12)。
// 可視のデスクトップを要する検査は T1 の担当ではない (docs/TESTING.md §2)。
//
// Windows Forms は対象外である。理由は PickerHostProfile を参照。
using System.Globalization;
using System.Windows.Automation;
using UiaTrigger.RealUia.Tests;
using Xunit;

namespace UiaTrigger.Picker.UiTests;

public sealed class TreeRealisationTests
{
    /// <summary>捕捉 (滞留 1 秒 + UIA の往復 + 実体化のリトライ) に与える猶予。</summary>
    private static readonly TimeSpan CaptureTimeout = TimeSpan.FromSeconds(20);

    /// <summary>「起きないこと」を確かめる窓。滞留 1 秒より十分に長くとる。</summary>
    private static readonly TimeSpan QuietWindow = TimeSpan.FromSeconds(8);

    /// <summary>対象アプリに並べるボタン。狙うのは<b>真ん中</b>である (理由は下記)。</summary>
    private static readonly string[] Buttons = ["btn-one", "btn-two", "btn-three", "btn-four", "btn-five"];

    /// <summary>
    /// 捕捉させる要素。端 (最初/最後) ではなく真ん中にしてあるのは、
    /// 「いつも先頭を選ぶ」「いつも末尾を選ぶ」実装でも通ってしまうのを避けるためである。
    /// </summary>
    private const string Target = "btn-three";

    /// <summary>行の表示は <c>ControlType "Name" [AutomationId]</c>。角括弧の部分だけは言語に依らない。</summary>
    private static string Tag(string automationId) => $"[{automationId}]";

    [Theory]
    [MemberData(nameof(PickerHostProfile.AllNames), MemberType = typeof(PickerHostProfile))]
    public void Capture_ExpandsTheChainAndSelectsTheElementUnderTheCursor(string profileName)
    {
        using var scenario = Scenario.AimedAtTheTargetButton(profileName);

        string selected = scenario.WaitForSelectedRow();

        // ハーネス自身の検証 (docs/TESTING.md §4 の教訓 (b))。「何かが選ばれた」ではなく
        // 「狙った要素が選ばれた」を見る — 座標がずれていれば別の要素が静かに選ばれる
        Assert.Contains(Tag(Target), selected, StringComparison.Ordinal);

        // 選択された行が実体化しているということは、その祖先も順に開かれたということである
        // (閉じた段のコンテナは生成されないので、実体化待ちが働かなければここへ到達できない)
        Assert.True(
            scenario.Rows().Count >= 3,
            $"チェーンが展開されていません:{Environment.NewLine}{scenario.Rows().Describe()}");
    }

    /// <summary>
    /// 捕捉直後のツリーは<b>チェーンだけ</b>で、各段の兄弟は出ていないこと。
    /// </summary>
    /// <remarks>
    /// 表示のための展開 (<c>ExpandForDisplay</c>) と、ユーザーが開いたこと (<c>IsExpanded</c>) を
    /// 分けてあることの確認である。presenter が <c>node.IsExpanded</c> を直接書く実装に戻すと
    /// TwoWay 束縛の書き戻しが「ユーザーが開いた」と解釈され、<b>ホバーのたびに経路上の全段が
    /// 兄弟を取りに行く</b> (docs/DESIGN.md §12)。例外は出ず、ツリーが重くなるだけである。
    /// </remarks>
    [Theory]
    [MemberData(nameof(PickerHostProfile.AllNames), MemberType = typeof(PickerHostProfile))]
    public void Capture_ShowsTheChainOnly_WithoutEnumeratingTheSiblingsOfEachStep(string profileName)
    {
        using var scenario = Scenario.AimedAtTheTargetButton(profileName);
        scenario.WaitForSelectedRow();

        IReadOnlyList<string> rows = scenario.Rows();
        string[] buttonRows = [.. rows.Where(r => r.Contains("[btn-", StringComparison.Ordinal))];

        Assert.True(
            buttonRows.Length == 1 && buttonRows[0].Contains(Tag(Target), StringComparison.Ordinal),
            $"チェーン上のボタンは {Target} の 1 行だけであるべきですが {buttonRows.Length} 行あります:" +
            $"{Environment.NewLine}{rows.Describe()}");
    }

    /// <summary>
    /// ネガティブコントロール: 同じ窓の<b>ボタンの無い場所</b>を指すと、ボタンは選ばれない。
    /// </summary>
    /// <remarks>
    /// これが無いと、上の 2 件は「座標に関係なくボタンを選ぶ」実装でも通る。
    /// ツリー自体は出る (窓や領域が捕捉される) ので、「捕捉が起きたか」ではなく
    /// 「<b>何が</b>捕捉されたか」に検出力があることの証明になっている。
    /// </remarks>
    [Theory]
    [MemberData(nameof(PickerHostProfile.AllNames), MemberType = typeof(PickerHostProfile))]
    public void Capture_AtAPointBesideTheButtons_SelectsSomethingElse(string profileName)
    {
        using var scenario = Scenario.AimedBesideTheButtons(profileName);

        string selected = scenario.WaitForSelectedRow();

        Assert.DoesNotContain("[btn-", selected, StringComparison.Ordinal);
        Ui.Never(
            () => scenario.Rows().Any(r => r.Contains("[btn-", StringComparison.Ordinal)),
            QuietWindow,
            "ボタンの行がツリーに現れる",
            scenario.Diagnostics);
    }

    /// <summary>
    /// 行を開くと<b>その行の子が全列挙</b>され、しかも既にあるチェーン子は二重にならないこと。
    /// </summary>
    /// <remarks>
    /// <para>
    /// WPF ではここが <c>IsExpanded</c> の TwoWay 束縛 → <c>ExpandedByUser</c> →
    /// <c>LoadChildrenAsync</c> の経路そのものである (MANUAL-CHECKS §4.3.3)。
    /// 束縛が黙って外れると、開いても子が増えないだけで例外は出ない。
    /// </para>
    /// <para>
    /// 「ちょうど 1 回ずつ・対象アプリと同じ順」で並ぶことまで見る。<c>LoadChildrenAsync</c> は
    /// 既にツリーに在るチェーン子 (ここでは <c>btn-three</c>) を<b>作り直さず</b>、その周りに
    /// 兄弟を差し込む。作り直すと部分木と選択が失われ、単に足すと同じ行が 2 つ並ぶ。
    /// </para>
    /// <para>
    /// <b>選択が残ることは見ない。</b> 行を閉じると、その配下に在った選択は閉じた行へ移る —
    /// これはユーザー操作として正しい挙動であり (実測)、押さえるべきは列挙のほうである。
    /// </para>
    /// </remarks>
    [Theory]
    [MemberData(nameof(PickerHostProfile.AllNames), MemberType = typeof(PickerHostProfile))]
    public void OpeningARow_EnumeratesEveryChildExactlyOnceAndInOrder(string profileName)
    {
        using var scenario = Scenario.AimedAtTheTargetButton(profileName);
        scenario.WaitForSelectedRow();

        scenario.ReopenTheRowAboveTheTarget();

        IReadOnlyList<string> rows = Ui.Until(
            () =>
            {
                IReadOnlyList<string> current = scenario.Rows();
                return Buttons.All(b => current.Any(r => r.Contains(Tag(b), StringComparison.Ordinal)))
                    ? current
                    : null;
            },
            CaptureTimeout,
            "開いた行の子が全列挙される",
            scenario.Diagnostics);

        string[] buttonRows = [.. rows.Where(r => r.Contains("[btn-", StringComparison.Ordinal))];
        Assert.Equal(
            Buttons,
            buttonRows.Select(r => Buttons.First(b => r.Contains(Tag(b), StringComparison.Ordinal))).ToArray());
    }

    /// <summary>
    /// 対象アプリ 1 つ + ホスト 1 つ + 開いたピッカー。テストごとに作り直す。
    /// </summary>
    private sealed class Scenario : IDisposable
    {
        private readonly TestTargetProcess _target;
        private readonly PickerHostProcess _host;

        private Scenario(TestTargetProcess target, PickerHostProcess host)
        {
            _target = target;
            _host = host;
        }

        public static Scenario AimedAtTheTargetButton(string profileName)
            => Create(profileName, target => target.CenterOf(Target));

        /// <summary>
        /// 同じ窓の中で、ボタンが 1 つも置かれていない場所を指す。
        /// </summary>
        /// <remarks>
        /// 子は左端から幅 <c>ItemWidth</c> で縦一列に並ぶので、右端寄りは必ず空いている
        /// (tests/UiaTrigger.TestTarget/TargetApp.cs の <c>Layout</c>)。
        /// 画面の外や別のアプリを指すのでは「対象アプリを捕捉できていないだけ」と区別がつかない。
        /// </remarks>
        public static Scenario AimedBesideTheButtons(string profileName)
            => Create(profileName, target =>
            {
                (int left, int top, int right, int _) = target.RectOf("root");
                (int _, int buttonTop, int buttonRight, int buttonBottom) = target.RectOf(Buttons[0]);
                Assert.True(
                    buttonRight < right - 8,
                    FormattableString.Invariant(
                        $"ボタンの右 ({buttonRight}) と窓の右 ({right}) のあいだに空きがありません。") +
                    "この対照実験は「同じ窓の中でボタンの無い場所」を指せることに依存しています。");
                _ = left;
                _ = top;
                return ((buttonRight + right) / 2, (buttonTop + buttonBottom) / 2);
            });

        private static Scenario Create(string profileName, Func<TestTargetProcess, (int X, int Y)> pickPoint)
        {
            PickerHostProfile profile = PickerHostProfile.ByName(profileName);
            TestTargetProcess target = TestTargetProcess.Start(TargetProfile.WinForms);
            try
            {
                // ホストの窓とぶつからない場所へ置き直す。ホスト側は PickerHostProcess が
                // 窓の出た瞬間に退かすので、割り付けは DesktopLayout が 1 箇所で決めている
                target.Send(DesktopLayout.PlaceTargetCommand);
                foreach (string button in Buttons)
                {
                    target.Send("add-button " + button);
                }

                (int x, int y) = pickPoint(target);
                RequireThePointBelongsTo(target, x, y, "ホストを起動する前");

                PickerHostProcess host = PickerHostProcess.Start(profile, x, y);
                try
                {
                    host.OpenPicker();
                    // 捕捉は「そこに何が在るか」で決まる。ピッカーの窓に覆われていたら、
                    // 20 秒待ってから「選択されない」と言うのではなく<b>ここで理由付きで落とす</b>
                    RequireThePointBelongsTo(target, x, y, "ピッカーを開いた後");
                    return new Scenario(target, host);
                }
                catch
                {
                    host.Dispose();
                    throw;
                }
            }
            catch
            {
                target.Dispose();
                throw;
            }
        }

        /// <summary>
        /// 指した座標に居るのが対象アプリであることを確かめる。
        /// </summary>
        /// <remarks>
        /// ハーネス自身の検証である (docs/TESTING.md §4 の教訓 (b))。座標が別の窓に覆われていると
        /// ピッカーは<b>例外も診断も出さずに何もしない</b> (自プロセスの要素は捕捉しない仕様)。
        /// それを 20 秒のタイムアウトとして受け取ると、原因が「実体化しなかった」に見えてしまう。
        /// </remarks>
        private static void RequireThePointBelongsTo(TestTargetProcess target, int x, int y, string when)
        {
            AutomationElement? at = AutomationElement.FromPoint(new System.Windows.Point(x, y));
            int expected = target.ProcessId;
            string found = at is null
                ? "(要素なし)"
                : string.Create(
                    CultureInfo.InvariantCulture,
                    $"pid {at.Current.ProcessId} '{at.Current.Name}' class='{at.Current.ClassName}'");
            Assert.True(
                at is not null && at.Current.ProcessId == expected,
                string.Create(CultureInfo.InvariantCulture, $"{when}、座標 ({x},{y}) に居るのは対象アプリ (pid {expected}) ではありません: {found}。この状態ではホバー捕捉が起きず、テストの結果に意味がありません。"));
        }

        public IReadOnlyList<string> Rows() => _host.Tree().RowNames();

        public string? SelectedRow() => _host.Tree().SelectedRow()?.NameOf();

        public string Diagnostics()
        {
            string target;
            try
            {
                AutomationElement window = AutomationElement.FromHandle((nint)_target.WindowHandle());
                target = string.Create(
                    CultureInfo.InvariantCulture,
                    $"対象アプリの窓: '{window.Current.Name}' rect={window.Current.BoundingRectangle} offscreen={window.Current.IsOffscreen}");
            }
            catch (Exception ex) when (ex is ElementNotAvailableException or InvalidOperationException or TimeoutException)
            {
                target = $"対象アプリの窓を調べられませんでした: {ex.Message}";
            }
            return _host.Diagnostics() + Environment.NewLine + target;
        }

        public string WaitForSelectedRow() => Ui.Until(
            SelectedRow,
            CaptureTimeout,
            "ホバー捕捉でツリーの行が選択される",
            Diagnostics);

        /// <summary>
        /// 選択されている行の 1 つ上 (= その親) を閉じて開き直す。
        /// </summary>
        /// <remarks>
        /// チェーンの各段は既に「表示のために」開かれているので、そのまま開いても何も起きない。
        /// <b>閉じてから開く</b>ことで初めて「ユーザーが開いた」ことになり、子の全列挙が走る。
        /// 行はツリー順に並ぶので (WinUI は平坦・WPF は入れ子だが順序は同じ)、
        /// 捕捉直後は末尾が対象要素、その 1 つ前が親である。
        /// </remarks>
        public void ReopenTheRowAboveTheTarget()
        {
            IReadOnlyList<AutomationElement> rows = _host.Tree().Descendants(ControlType.TreeItem);
            Assert.True(
                rows.Count >= 2,
                $"親の行がありません:{Environment.NewLine}{Rows().Describe()}");

            AutomationElement parent = rows[^2];
            Assert.Contains(
                Tag(Target),
                rows[^1].NameOf(),
                StringComparison.Ordinal);

            ExpandCollapsePattern expander = parent.Expander();
            expander.Collapse();
            expander.Expand();
        }

        public void Dispose()
        {
            _host.Dispose();
            _target.Dispose();
        }
    }
}
