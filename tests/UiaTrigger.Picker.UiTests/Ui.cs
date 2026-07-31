// UIA クライアント側の小道具 (docs/TESTING.md §1 T4)。
//
// 期限付きポーリングしか使わない。固定の Sleep は「速いマシンでは無駄に遅く、
// 遅いマシンでは足りない」ので、T3 と同じく期限で待つ。
using System.Globalization;
using System.Runtime.InteropServices;
using System.Windows.Automation;

namespace UiaTrigger.Picker.UiTests;

internal static class Ui
{
    /// <summary>ポーリング間隔。UI の変化を待つので、UIA の 1 往復より少し長くとる。</summary>
    private static readonly TimeSpan PollInterval = TimeSpan.FromMilliseconds(200);

    /// <summary>
    /// <paramref name="probe"/> が非 null を返すまで待つ。期限を過ぎたら
    /// <paramref name="diagnose"/> の内容を添えて落ちる。
    /// </summary>
    /// <remarks>
    /// 診断を必須の引数にしてあるのは、実 UIA のテストは**失敗したときに何も分からない**のが
    /// 常だからである (T3 の <c>MonitorHarness.History</c> と同じ理由)。
    /// 「行が実体化しなかった」と「そもそも捕捉が起きなかった」は、状態を印字しないと区別できない。
    /// </remarks>
    public static T Until<T>(Func<T?> probe, TimeSpan timeout, string what, Func<string> diagnose)
        where T : class
    {
        DateTime deadline = DateTime.UtcNow + timeout;
        while (true)
        {
            if (Probe(probe) is { } value)
            {
                return value;
            }
            // 残り時間は 1 度だけ計算して使う。条件と待ち時間で 2 度読むと、
            // その間に期限を過ぎて負の待ち時間になりうる
            TimeSpan remaining = deadline - DateTime.UtcNow;
            if (remaining <= TimeSpan.Zero)
            {
                throw new TimeoutException(
                    FormattableString.Invariant($"{what} が {timeout.TotalSeconds:0.#} 秒以内に成立しませんでした。") +
                    Environment.NewLine + Diagnose(diagnose));
            }
            Thread.Sleep(remaining < PollInterval ? remaining : PollInterval);
        }
    }

    /// <summary>
    /// <paramref name="probe"/> が期限いっぱい false のままであることを確かめる。
    /// ネガティブコントロール用 — 「起きないこと」は待ち切らないと言えない。
    /// </summary>
    /// <remarks>
    /// <see cref="Until{T}"/> と違い、ここでは <see cref="ElementNotAvailableException"/> を
    /// **握り潰さない**。握り潰すと「起きなかった」と「見られなかった」が同じ扱いになり、
    /// 対象のウィンドウが消えただけでネガティブコントロールが通ってしまう。
    /// 待つ側では一時的な失敗を「まだ成立していない」と読むのが正しいが、
    /// 起きないことを確かめる側では逆になる。
    /// </remarks>
    public static void Never(Func<bool> probe, TimeSpan window, string what, Func<string> diagnose)
    {
        DateTime deadline = DateTime.UtcNow + window;
        while (true)
        {
            if (probe())
            {
                throw new InvalidOperationException(
                    $"{what} が起きてしまいました。" + Environment.NewLine + Diagnose(diagnose));
            }
            TimeSpan remaining = deadline - DateTime.UtcNow;
            if (remaining <= TimeSpan.Zero)
            {
                return;
            }
            Thread.Sleep(remaining < PollInterval ? remaining : PollInterval);
        }
    }

    /// <summary>
    /// 対象のウィンドウが閉じている最中は UIA が例外を投げる。ポーリング中の
    /// 一時的な失敗は「まだ成立していない」として扱う (最終的には期限で落ちる)。
    /// </summary>
    private static T? Probe<T>(Func<T?> probe) where T : class
    {
        try
        {
            return probe();
        }
        catch (ElementNotAvailableException)
        {
            return null;
        }
    }

    /// <summary>
    /// 診断を集める。**集めるほうが失敗しても、元の失敗を握り潰さない。**
    /// </summary>
    /// <remarks>
    /// <see cref="ElementNotAvailableException"/> だけを捕まえるのでは足りない。
    /// 診断は UIA を叩くので <c>COMException</c> (「Operation timed out」) も普通に出る —
    /// 相手が忙しいときほど出るので、**いちばん診断が要る場面で**出る。
    /// それが素通りすると、xunit が報告するのは診断側の COMException になり、
    /// **本当のタイムアウトの説明がまるごと消える** (実際にそれで原因調査を 30 分遠回りした)。
    /// </remarks>
    private static string Diagnose(Func<string> diagnose)
    {
        try
        {
            return diagnose();
        }
        catch (Exception ex) when (ex is ElementNotAvailableException or COMException or TimeoutException)
        {
            return $"(診断の収集にも失敗しました: {ex.GetType().Name}: {ex.Message})";
        }
    }

    /// <summary>AutomationId で子孫を 1 つ探す。無ければ null。</summary>
    public static AutomationElement? ById(this AutomationElement root, string automationId)
        => root.FindFirst(
            TreeScope.Descendants,
            new PropertyCondition(AutomationElement.AutomationIdProperty, automationId));

    /// <summary>AutomationId で子孫を 1 つ探す。無ければ落ちる。</summary>
    public static AutomationElement RequireById(this AutomationElement root, string automationId)
        => root.ById(automationId)
           ?? throw new InvalidOperationException($"AutomationId '{automationId}' の要素が見つかりません。");

    /// <summary>
    /// AutomationId で子孫を 1 つ探す。**出てくるまで待つ**。
    /// </summary>
    /// <remarks>
    /// **WinUI では、レイアウトが動いた直後に既に在る要素が一時的に UIA から消える。**
    /// トグルを切り替えた直後の <c>CondCombo</c> で実際に踏んだ。
    /// <see cref="RequireById"/> を使うと「要素が無い」という顔で落ちるが、
    /// 実際には**まだ出ていないだけ**である。
    /// 待っても出てこなければ結局落ちるので、本当に無い場合を見逃すことはない。
    /// </remarks>
    public static AutomationElement RequireByIdEventually(
        this AutomationElement root, string automationId, Func<string> diagnose)
        => Until(
            () => root.ById(automationId),
            TimeSpan.FromSeconds(10),
            $"AutomationId '{automationId}' の要素",
            diagnose);

    /// <summary>指定のコントロール型の子孫をツリー順に返す。</summary>
    public static IReadOnlyList<AutomationElement> Descendants(this AutomationElement root, ControlType type)
    {
        AutomationElementCollection found = root.FindAll(
            TreeScope.Descendants,
            new PropertyCondition(AutomationElement.ControlTypeProperty, type));
        var list = new List<AutomationElement>(found.Count);
        for (int i = 0; i < found.Count; i++)
        {
            list.Add(found[i]);
        }
        return list;
    }

    /// <summary>ツリーの行の表示名。WinUI は行が平坦に、WPF は入れ子で出るが、どちらもツリー順である。</summary>
    public static IReadOnlyList<string> RowNames(this AutomationElement tree)
        => [.. tree.Descendants(ControlType.TreeItem).Select(NameOf)];

    /// <summary>選択されている行。無ければ null。</summary>
    /// <remarks>
    /// ツリー側の <see cref="SelectionPattern"/> ではなく**行側の**
    /// <see cref="SelectionItemPattern"/> を見る。WinUI の <c>TreeView</c> は自身を UIA に出さず
    /// 内側の <c>TreeViewList</c> が Tree として現れるため、<c>TreeView.SelectedItem</c> への
    /// 代入がツリー側の選択として報告されない (実測)。行側は 2 変種とも
    /// <c>SelectionItem</c> を持つので、こちらなら同じ書き方で両方に効く。
    /// </remarks>
    public static AutomationElement? SelectedRow(this AutomationElement tree)
        => tree.Descendants(ControlType.TreeItem).FirstOrDefault(IsSelected);

    /// <summary>行が選択されているか。パターンを持たない要素は false。</summary>
    public static bool IsSelected(this AutomationElement row)
        => row.TryGetCurrentPattern(SelectionItemPattern.Pattern, out object? pattern) &&
           ((SelectionItemPattern)pattern).Current.IsSelected;

    public static string NameOf(this AutomationElement element) => element.Current.Name;

    public static void Invoke(this AutomationElement element)
        => ((InvokePattern)element.GetCurrentPattern(InvokePattern.Pattern)).Invoke();

    /// <summary>
    /// テキストを入れる。**キー入力ではなく <c>ValuePattern</c> で入れる**。
    /// </summary>
    /// <remarks>
    /// 文字を入れることは検査対象ではなく**舞台の準備**である。準備に合成入力を使うと、
    /// フォーカスがどこに在るかという新しい前提が要るようになり、そのぶん
    /// 「入力が届かなかった」と「ハーネスが狙いを立てられなかった」が混ざる (docs/TESTING.md §4)。
    /// 最下層から撃つのは T5 だけで、しかも検査対象そのものに限る (docs/TESTING.md §3)。
    /// </remarks>
    public static void SetText(this AutomationElement element, string text)
        => ((ValuePattern)element.GetCurrentPattern(ValuePattern.Pattern)).SetValue(text);

    public static ExpandCollapsePattern Expander(this AutomationElement element)
        => (ExpandCollapsePattern)element.GetCurrentPattern(ExpandCollapsePattern.Pattern);

    /// <summary><c>ValuePattern</c> の現在値。持っていなければ null。</summary>
    public static string? ValueOf(this AutomationElement element)
        => element.TryGetCurrentPattern(ValuePattern.Pattern, out object? pattern)
            ? ((ValuePattern)pattern).Current.Value
            : null;

    /// <summary>
    /// コンボボックスの項目を**名前で**選ぶ。
    /// </summary>
    /// <remarks>
    /// <para>
    /// 開いてから選ぶ。項目は開くまで実体化しないので、閉じたまま探すと見つからない。
    /// </para>
    /// <para>
    /// 名前で探せるのは、条件のコンボが並べているのが**列挙メンバー名**だからである
    /// (<c>Always</c> / <c>Equals</c> / <c>Between</c> …)。これは翻訳しないと決めてある
    /// (docs/LOCALIZATION.md §7) ので、カルチャを変えても綴りが動かない。
    /// **翻訳される項目をこの関数で選んではいけない。**
    /// </para>
    /// </remarks>
    public static void SelectComboItem(this AutomationElement combo, string itemName)
    {
        var expander = (ExpandCollapsePattern)combo.GetCurrentPattern(ExpandCollapsePattern.Pattern);
        expander.Expand();
        AutomationElement item = Until(
            () => combo.FindFirst(
                TreeScope.Descendants,
                new AndCondition(
                    new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.ListItem),
                    new PropertyCondition(AutomationElement.NameProperty, itemName))),
            TimeSpan.FromSeconds(10),
            $"コンボの項目 '{itemName}'",
            () => "並んでいる項目: " +
                  string.Join(", ", combo.Descendants(ControlType.ListItem).Select(NameOf)));
        ((SelectionItemPattern)item.GetCurrentPattern(SelectionItemPattern.Pattern)).Select();
        expander.Collapse();
    }

    /// <summary>水平にスクロールできるか。<c>ScrollPattern</c> が無ければ null。</summary>
    /// <remarks>
    /// null と false を分けているのは意図である。「スクロールできない」と
    /// 「そもそもスクロールの概念が出ていない」は別の失敗で、後者は
    /// **検査対象を取り違えている**合図である (Windows Forms の <c>TreeView</c> は
    /// MSAA ブリッジ越しなので出ない可能性がある)。
    /// </remarks>
    public static bool? CanScrollHorizontally(this AutomationElement element)
        => element.TryGetCurrentPattern(ScrollPattern.Pattern, out object? pattern)
            ? ((ScrollPattern)pattern).Current.HorizontallyScrollable
            : null;

    /// <summary>
    /// その行**自身**が持つ確定ボタンの数。
    /// </summary>
    /// <remarks>
    /// <para>
    /// **引き算が要るのは WPF のためである。**WinUI の <c>TreeView</c> は行を平坦な
    /// <c>ListView</c> に展開するので行の部分木は自分だけだが、WPF は行が**入れ子**なので、
    /// 部分木をそのまま数えると子孫の行のぶんまで数える (実測: プロセスルートの行の部分木に
    /// 33 個)。直下の行のぶんを引けば、両変種とも同じ意味になる。
    /// </para>
    /// <para>
    /// **「確定ボタン」の見分けは <c>InvokePattern</c> を持つことである。**
    /// 名前では見分けられない — WinUI では名前も AutomationId も空で、WPF では
    /// リソースの字形 (✓) がそのまま名前になるため、字形を変えるとテストが落ちる。
    /// WPF の行には展開用のボタンも居るが、あちらは <c>InvokePattern</c> を持たない (実測)。
    /// </para>
    /// </remarks>
    public static int ConfirmButtonsOfItsOwn(this AutomationElement row)
    {
        int total = InvokableButtonsIn(row);
        foreach (AutomationElement child in row.FindAll(
            TreeScope.Children,
            new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.TreeItem)))
        {
            total -= InvokableButtonsIn(child);
        }
        return total;
    }

    private static int InvokableButtonsIn(AutomationElement root)
    {
        int count = 0;
        foreach (AutomationElement button in root.FindAll(
            TreeScope.Descendants,
            new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.Button)))
        {
            if (button.TryGetCurrentPattern(InvokePattern.Pattern, out _))
            {
                count++;
            }
        }
        return count;
    }

    /// <summary>行の一覧を読みやすい形に整える (失敗時の診断用)。</summary>
    public static string Describe(this IReadOnlyList<string> rows)
        => rows.Count == 0
            ? "  (ツリーは空です)"
            : string.Join(
                Environment.NewLine,
                rows.Select((r, i) => string.Create(CultureInfo.InvariantCulture, $"  [{i}] {r}")));
}
