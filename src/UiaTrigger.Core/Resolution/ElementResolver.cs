using System.Runtime.InteropServices;
using UiaTrigger.Models;

namespace UiaTrigger.Resolution;

/// <summary>解決結果。</summary>
internal sealed class ResolvedTarget
{
    public required IElementNode Element { get; init; }

    /// <summary>
    /// ウィンドウ要素から対象の **親** までの経路 (対象自身は含まない)。
    /// 空なら対象 = ウィンドウ要素。
    /// StructureChanged の購読範囲をこの経路だけに絞るために使う (docs/DESIGN.md B3)。
    /// </summary>
    public required IReadOnlyList<IElementNode> Ancestors { get; init; }

    public required nint WindowHandle { get; init; }

    /// <summary>経路全体の合計スコア (診断用。合否の判定には使わない)。</summary>
    public required int Score { get; init; }

    /// <summary>
    /// Search 方式 (一意な AutomationId の FindAll 1 発) で見つけたか。
    /// false なら経路方式 (ビーム探索)。診断用であり、どちらでも結果の形は同じ。
    /// </summary>
    public required bool FoundBySearch { get; init; }

    /// <summary>トップレベルウィンドウ要素。</summary>
    public IElementNode WindowElement => Ancestors.Count > 0 ? Ancestors[0] : Element;
}

/// <summary>
/// 永続化された <see cref="TriggerDefinition"/> から実際の要素を探索して解決する。
///
/// <para>
/// 「段ごとにスコア合計 &gt;= 閾値の最良候補を貪欲に選ぶ」方式ではなく、
/// **必須述語 (足切り) + ランキング + ビーム探索** を使う (docs/DESIGN.md §3 / A3〜A7)。
/// 貪欲・閾値方式は
/// (a) 段ごとの閾値が「AutomationId も Name も無い段の満点」と同じ値だと
///     兄弟が 1 個増えるだけで解決全体が失敗し (A3)、
/// (b) ControlType 不一致を即除外すると状態で型が変わる要素を追えず (A5)、
/// (c) 段ごとに 1 位だけを選ぶのでその先が行き止まりでも 2 位を試さない (A6)。
/// </para>
///
/// 要素ツリーへのアクセスは <see cref="IElementTree"/> 越しに行うため、
/// 探索アルゴリズムは COM 無しで単体テストできる (D1)。
/// </summary>
internal static class ElementResolver
{
    /// <summary>
    /// 要素まで解決する。UiaDispatcher スレッド上で呼ぶこと。
    ///
    /// 受けるのは <see cref="TriggerDefinition"/> ではなく (ウィンドウ, 経路) の組である。
    /// 1 つのトリガーが複数の要素にまたがれるので、解決の単位はトリガーではない。
    /// </summary>
    public static ResolvedTarget? Resolve(
        IElementTree tree, WindowCandidateCache windows, WindowIdentity window, ElementLocator locator, ResolverOptions options)
    {
        ArgumentNullException.ThrowIfNull(tree);
        ArgumentNullException.ThrowIfNull(windows);
        ArgumentNullException.ThrowIfNull(window);
        ArgumentNullException.ThrowIfNull(locator);
        ArgumentNullException.ThrowIfNull(options);

        foreach (nint hwnd in windows.GetCandidates(window))
        {
            IElementNode? windowNode = TryGetWindow(tree, hwnd);
            if (windowNode is null)
            {
                continue;
            }

            // Search 方式を先に試す (docs/DESIGN.md §3 の Search 方式)。一意な AutomationId があれば
            // FindAll 1 発で決まり、経路の途中がどう変わっても影響を受けない。
            // 一意でなくなっていれば黙って経路方式に落ちる
            bool bySearch = true;
            (List<IElementNode> Chain, int Score)? found = SearchByAutomationId(tree, windowNode, locator, options);
            if (found is null)
            {
                bySearch = false;
                found = Search(tree, windowNode, locator, options);
            }
            if (found is null)
            {
                tree.Release(windowNode);
                continue;
            }

            List<IElementNode> chain = found.Value.Chain;
            return new ResolvedTarget
            {
                Element = chain[^1],
                Ancestors = chain.GetRange(0, chain.Count - 1),
                WindowHandle = hwnd,
                Score = found.Value.Score,
                FoundBySearch = bySearch,
            };
        }
        return null;
    }

    /// <summary>トップレベルウィンドウのみ解決する (要素出現待ちの購読用)。</summary>
    public static (IElementNode Window, nint Hwnd)? ResolveWindow(IElementTree tree, WindowCandidateCache windows, WindowIdentity identity, ResolverOptions options)
    {
        ArgumentNullException.ThrowIfNull(tree);
        ArgumentNullException.ThrowIfNull(windows);

        foreach (nint hwnd in windows.GetCandidates(identity))
        {
            IElementNode? windowNode = TryGetWindow(tree, hwnd);
            if (windowNode is not null)
            {
                return (windowNode, hwnd);
            }
        }
        return null;
    }

    private static IElementNode? TryGetWindow(IElementTree tree, nint hwnd)
    {
        try
        {
            // null = そのウィンドウが UIA を提供していない (docs/DESIGN.md A15)
            return tree.GetWindow(hwnd);
        }
        catch (COMException)
        {
            return null;
        }
    }

    /// <summary>
    /// Search 方式: 一意な <c>AutomationId</c> で対象を直接引き当てる (docs/DESIGN.md §3 の Search 方式)。
    ///
    /// <para>
    /// B3 の経路購読が「ウィンドウから対象の親までの各段」を要求するのに対し
    /// <c>FindFirst</c> は要素しか返さないため、この方式は <see cref="IElementTree.GetParent"/>
    /// による親方向のナビゲーションがあって初めて成立する。
    /// </para>
    ///
    /// <para>
    /// 一致が **ちょうど 1 件でなければ採らない**。0 件なら居ないだけだが、2 件以上あるときに
    /// 先頭を選ぶと「記録時は一意だった id が重複するようになった」ときに静かに別の要素を掴む。
    /// どちらの場合も null を返して経路方式に委ねる (そちらは兄弟インデックスなど他の手掛かりを持つ)。
    /// </para>
    ///
    /// 戻り値の経路は Search 方式でも <see cref="Search"/> と同じ [ウィンドウ, …, 対象] の形にする。
    /// 呼び出し元から見て 2 方式の違いが消えるので、購読も解放も分岐しない。
    /// </summary>
    private static (List<IElementNode> Chain, int Score)? SearchByAutomationId(
        IElementTree tree, IElementNode windowNode, ElementLocator locator, ResolverOptions options)
    {
        if (locator.Search is not { AutomationId: { Length: > 0 } automationId })
        {
            return null;
        }

        IReadOnlyList<IElementNode> found;
        try
        {
            // 上限 2 件。「一意か」を知りたいだけなので全件は要らない
            found = tree.FindByAutomationId(windowNode, locator.View, automationId, max: 2);
        }
        catch (COMException)
        {
            return null;
        }
        if (found.Count != 1)
        {
            foreach (IElementNode node in found)
            {
                tree.Release(node);
            }
            return null;
        }

        IElementNode target = found[0];
        // Subtree の検索はスコープ自身も含む。対象 = ウィンドウ要素なら経路は空
        // (呼び出し元が持つウィンドウノードを使い、重複して掴んだぶんは手放す)
        try
        {
            if (tree.AreSame(target, windowNode))
            {
                tree.Release(target);
                return ([windowNode], options.StepAutomationIdScore);
            }
        }
        catch (COMException)
        {
            tree.Release(target);
            return null;
        }

        // 経路購読 (B3) 用に、対象からウィンドウまでの祖先鎖を遡って作る
        var ancestors = new List<IElementNode>();
        try
        {
            IElementNode current = target;
            for (int depth = 0; depth < Math.Max(options.MaxSearchDepth, 1); depth++)
            {
                IElementNode? parent = tree.GetParent(current, locator.View);
                if (parent is null)
                {
                    break; // ウィンドウに行き着く前にビューの最上位へ出た
                }
                // **先に在庫へ入れてから比較する。**AreSame は投げうるので、比較してから
                // 積む形だと「取ったばかりの parent」だけが下の後始末から漏れる
                ancestors.Add(parent);
                if (tree.AreSame(parent, windowNode))
                {
                    ancestors.RemoveAt(ancestors.Count - 1);
                    tree.Release(parent);
                    var chain = new List<IElementNode>(ancestors.Count + 2) { windowNode };
                    ancestors.Reverse();
                    chain.AddRange(ancestors);
                    chain.Add(target);
                    return (chain, options.StepAutomationIdScore);
                }
                current = parent;
            }
        }
        catch (COMException)
        {
        }

        // 対象は見つかったが、それがこのウィンドウの配下だと確認できなかった。
        // 掴んだものはすべて手放し、経路方式に委ねる
        tree.Release(target);
        foreach (IElementNode node in ancestors)
        {
            tree.Release(node);
        }
        return null;
    }

    /// <summary>探索中の 1 候補。親を辿ると経路になる。</summary>
    private sealed class BeamNode
    {
        public required IElementNode Element { get; init; }

        /// <summary>1 段上の候補。ウィンドウ要素なら null。</summary>
        public BeamNode? Parent { get; init; }

        /// <summary>ここまでの合計スコア (順位付け用)。</summary>
        public required int Score { get; init; }

        /// <summary>ここまでの兄弟インデックスのずれの合計 (同点時のタイブレーク。小さいほど良い)。</summary>
        public required int IndexDistance { get; init; }

        /// <summary>発見順 (同点・同距離のときの順序を実行ごとにぶれさせないため)。</summary>
        public required int Discovery { get; init; }
    }

    /// <summary>
    /// ウィンドウ要素から対象要素までをビーム探索で辿る。
    /// 戻り値の経路は [ウィンドウ, 段 0, …, 対象要素]。解決できなければ null
    /// (その場合、途中で確保したノードはここで解放済み。ウィンドウ要素は呼び出し元の持ち物)。
    /// </summary>
    private static (List<IElementNode> Chain, int Score)? Search(
        IElementTree tree, IElementNode windowNode, ElementLocator locator, ResolverOptions options)
    {
        var root = new BeamNode { Element = windowNode, Parent = null, Score = 0, IndexDistance = 0, Discovery = 0 };
        var beam = new List<BeamNode> { root };
        int beamWidth = Math.Max(options.BeamWidth, 1);

        // 各段の生き残りノード。失敗時と成功時に「勝ち残った経路以外」を確実に解放するために持つ。
        // ビームは経路の前半を共有しうるので、段ごとに配ったノードを個別に解放しようとすると
        // 二重解放になる。保持数は beamWidth × 深さ (通常 3 × 15) で無視できる
        var levels = new List<List<BeamNode>>();

        try
        {
            foreach (ElementPathStep step in locator.Steps)
            {
                // 組み立てる前に登録する。GetChildren が途中で落ちても、この段で既に採った
                // 候補が levels 越しに解放されるようにするため
                var candidates = new List<BeamNode>();
                levels.Add(candidates);

                foreach (BeamNode parent in beam)
                {
                    // 段あたり 1 往復 (docs/DESIGN.md B1)。ビーム幅の分だけ往復が増えるが、
                    // 行き止まりから戻れることの対価としては安い
                    IReadOnlyList<IElementNode> children =
                        tree.GetChildren(parent.Element, locator.View, options.MaxChildrenPerLevel);

                    for (int index = 0; index < children.Count; index++)
                    {
                        int score = ScoreStep(step, children[index], options);
                        if (score < options.StepAcceptScore)
                        {
                            // 記録された属性が「この要素ではない」と言っている。候補にしない
                            tree.Release(children[index]);
                            continue;
                        }
                        candidates.Add(new BeamNode
                        {
                            Element = children[index],
                            Parent = parent,
                            Score = parent.Score + score,
                            IndexDistance = parent.IndexDistance + IndexDistanceOf(step, index),
                            Discovery = candidates.Count,
                        });
                    }
                }

                candidates.Sort(Compare);
                int keep = Math.Min(beamWidth, candidates.Count);
                for (int i = keep; i < candidates.Count; i++)
                {
                    tree.Release(candidates[i].Element);
                }
                candidates.RemoveRange(keep, candidates.Count - keep);

                if (candidates.Count == 0)
                {
                    ReleaseLevels(tree, levels, keep: null);
                    return null;
                }
                beam = candidates;
            }
        }
        catch (COMException)
        {
            ReleaseLevels(tree, levels, keep: null);
            return null;
        }

        BeamNode best = beam[0];
        var chain = new List<IElementNode>();
        for (BeamNode? node = best; node is not null; node = node.Parent)
        {
            chain.Add(node.Element);
        }
        chain.Reverse();
        ReleaseLevels(tree, levels, keep: best);
        return (chain, best.Score);
    }

    /// <summary>スコア降順 → 兄弟インデックスのずれ昇順 → 発見順。</summary>
    private static int Compare(BeamNode a, BeamNode b)
    {
        if (a.Score != b.Score)
        {
            return b.Score.CompareTo(a.Score);
        }
        if (a.IndexDistance != b.IndexDistance)
        {
            return a.IndexDistance.CompareTo(b.IndexDistance);
        }
        return a.Discovery.CompareTo(b.Discovery);
    }

    /// <summary>
    /// 生き残ったノードのうち、勝ち残った経路 (<paramref name="keep"/> の祖先鎖) に無いものを解放する。
    /// ウィンドウ要素は各段の生き残りに含まれないので、ここでは触らない (呼び出し元の持ち物)。
    /// </summary>
    private static void ReleaseLevels(IElementTree tree, List<List<BeamNode>> levels, BeamNode? keep)
    {
        var survivors = new HashSet<BeamNode>();
        for (BeamNode? node = keep; node is not null; node = node.Parent)
        {
            survivors.Add(node);
        }
        foreach (List<BeamNode> level in levels)
        {
            foreach (BeamNode node in level)
            {
                if (!survivors.Contains(node))
                {
                    tree.Release(node.Element);
                }
            }
        }
    }

    /// <summary>
    /// 記録された兄弟インデックスからのずれ。順位付けの **タイブレークにのみ** 使う
    /// (docs/DESIGN.md A3)。加点・減点としてスコアに混ぜると、
    /// 兄弟が 1 個挿入されただけで段の合計が閾値を割る。
    /// 記録できなかった場合 (<see cref="ElementPathStep.UnknownSiblingIndex"/>) は
    /// 全候補を等距離として扱う (A7) — ここで 0 と見なすと、
    /// 実際には不明なのに「先頭にあった」という誤った手掛かりになる。
    /// </summary>
    private static int IndexDistanceOf(ElementPathStep step, int index) =>
        step.SiblingIndex < 0 ? 0 : Math.Abs(index - step.SiblingIndex);

    /// <summary>
    /// 候補 1 件のスコア。**記録された属性が肯定する度合い**であり、
    /// <see cref="ResolverOptions.StepAcceptScore"/> を下回る候補は採らない。
    /// 記録されていない属性は加点も減点もしない。
    /// </summary>
    internal static int ScoreStep(ElementPathStep step, IElementNode candidate, ResolverOptions options)
    {
        int score = 0;

        if (step.ControlType != 0)
        {
            // 不一致は「即除外」ではなく重い減点。他に強い証拠 (AutomationId) があれば
            // 型が変わっても追随できる (docs/DESIGN.md A5)
            score += candidate.ControlType == step.ControlType
                ? options.StepControlTypeScore
                : options.StepControlTypeMismatchPenalty;
        }

        if (!string.IsNullOrEmpty(step.AutomationId))
        {
            string automationId = candidate.AutomationId;
            score += string.Equals(automationId, step.AutomationId, StringComparison.Ordinal)
                ? options.StepAutomationIdScore
                : string.IsNullOrEmpty(automationId)
                    ? options.StepAutomationIdMissingPenalty
                    : options.StepAutomationIdMismatchPenalty;
        }

        if (!string.IsNullOrEmpty(step.ClassName))
        {
            string className = candidate.ClassName;
            score += string.Equals(className, step.ClassName, StringComparison.Ordinal)
                ? options.StepClassNameScore
                : string.IsNullOrEmpty(className)
                    ? options.StepClassNameMissingPenalty
                    : options.StepClassNameMismatchPenalty;
        }

        if (!string.IsNullOrEmpty(step.Name))
        {
            string name = candidate.Name;
            if (string.Equals(name, step.Name, StringComparison.Ordinal))
            {
                score += options.StepNameExactScore;
            }
            else if (name.Length > 0 &&
                     (name.Contains(step.Name, StringComparison.Ordinal) ||
                      step.Name.Contains(name, StringComparison.Ordinal)))
            {
                score += options.StepNamePartialScore;
            }
            else
            {
                score += options.StepNameMismatchPenalty;
            }
        }

        return score;
    }
}
