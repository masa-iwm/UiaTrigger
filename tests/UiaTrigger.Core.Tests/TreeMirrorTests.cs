// TreeMirror の回帰テスト (docs/DESIGN.md §12)。
//
// 3 変種のうち Windows Forms にしか無いコードである。WinUI と WPF は ItemsSource に
// ObservableCollection を渡せば済むが、Windows Forms の TreeView はモデルを見ない。
// 「新しく書いたぶんがいちばん壊れやすい」ので、独立したクラスに切って厚く見る。
//
// ここが静かに壊れると、ツリーの中身とピッカーが思っている木がずれる — 例外は出ず、
// 「選んだはずの行と違う要素が確定される」という形で出る。
using System.Collections.ObjectModel;
using System.Windows.Forms;
using UiaTrigger.Picker;
using UiaTrigger.Picker.WinForms;
using Xunit;

namespace UiaTrigger.Tests;

public sealed class TreeMirrorTests
{
    private static PickerTreeNode Node(string display)
        => new(display, new FakePickerElement(display));

    /// <summary>
    /// ハンドルまで作った <see cref="TreeView"/>。
    /// </summary>
    /// <remarks>
    /// ネイティブのコントロールが無いと <c>AfterExpand</c> / <c>AfterCollapse</c> が上がらず、
    /// 「ユーザーが開いた」経路のテストが**何も検査していないのに緑になる**。
    /// 本物のフォームも同じ理由でコンストラクターでハンドルを作っている。
    /// </remarks>
    private static TreeView CreateTree()
    {
        var tree = new TreeView();
        _ = tree.Handle;
        return tree;
    }

    /// <summary>すでに在るノードが、写した時点でツリーに並んでいること。</summary>
    [Fact]
    public void TheInitialNodesAreCopiedIntoTheTree()
        => Sta.Run(() =>
        {
            using TreeView tree = CreateTree();
            var roots = new ObservableCollection<PickerTreeNode> { Node("a"), Node("b") };
            using var mirror = new TreeMirror(tree, roots);

            Assert.Equal(["a", "b"], tree.Nodes.Cast<TreeNode>().Select(n => n.Text));
        });

    /// <summary>あとから足したノードが、同じ位置に入ること。</summary>
    [Fact]
    public void NodesAddedLaterArriveInTheSamePlace()
        => Sta.Run(() =>
        {
            using TreeView tree = CreateTree();
            var roots = new ObservableCollection<PickerTreeNode>();
            using var mirror = new TreeMirror(tree, roots);

            PickerTreeNode parent = Node("parent");
            roots.Add(parent);
            parent.Children.Add(Node("second"));
            // プレゼンターはチェーンの子の**前**に兄弟を差し込む (Insert) ので、順序が要点になる
            parent.Children.Insert(0, Node("first"));

            TreeNode mapped = tree.Nodes[0];
            Assert.Equal("parent", mapped.Text);
            Assert.Equal(["first", "second"], mapped.Nodes.Cast<TreeNode>().Select(n => n.Text));
        });

    /// <summary>
    /// まだ子を取っていない段に展開記号が出ること。
    ///
    /// Windows Forms の TreeView は子が 1 つも無い段に <c>[+]</c> を出さない。
    /// 出ないと**ユーザーが開くという操作自体ができず**、子の全列挙が永久に起きない。
    /// </summary>
    [Fact]
    public void ARowWithUnfetchedChildrenOffersAnExpander()
        => Sta.Run(() =>
        {
            using TreeView tree = CreateTree();
            PickerTreeNode node = Node("lazy");
            node.HasUnrealizedChildren = true;
            var roots = new ObservableCollection<PickerTreeNode> { node };
            using var mirror = new TreeMirror(tree, roots);

            Assert.Single(tree.Nodes[0].Nodes.Cast<TreeNode>());

            // 本物の子が届いたら仮ノードは消える (でないと空行が 1 つ増えたままになる)
            node.Children.Add(Node("real"));
            node.HasUnrealizedChildren = false;

            Assert.Equal(["real"], tree.Nodes[0].Nodes.Cast<TreeNode>().Select(n => n.Text));
        });

    /// <summary>
    /// ユーザーが開いたときだけ「子を全列挙してよい」になること。
    ///
    /// <para>
    /// 継ぎ目の中心的な不変条件である (docs/DESIGN.md §12)。ツリー側から開いた場合は
    /// <see cref="PickerTreeNode.IsExpanded"/> が false → true になるので
    /// ユーザー操作として通り、ピッカーが表示のために開いた段
    /// (<see cref="PickerTreeNode.ExpandForDisplay"/>) は既に true なので通らない。
    /// </para>
    /// </summary>
    [Fact]
    public void ExpandingARowFromTheTreeCountsAsAUserExpansion()
        => Sta.Run(() =>
        {
            using TreeView tree = CreateTree();
            PickerTreeNode node = Node("row");
            node.Children.Add(Node("child"));
            var roots = new ObservableCollection<PickerTreeNode> { node };
            using var mirror = new TreeMirror(tree, roots);

            int userExpansions = 0;
            node.ExpandedByUser += _ => userExpansions++;

            tree.Nodes[0].Expand();

            Assert.True(node.IsExpanded);
            Assert.Equal(1, userExpansions);
        });

    /// <summary>
    /// 表示のために開いた段は、ユーザー操作として報告されないこと。
    ///
    /// 上のテストと**対**である。写しの側は IsExpanded の変化を見て TreeNode を開くので、
    /// そこで上がる AfterExpand が新たな通知を生まないことまで見ないと片手落ちになる。
    /// </summary>
    [Fact]
    public void ExpandingForDisplayDoesNotCountAsAUserExpansion()
        => Sta.Run(() =>
        {
            using TreeView tree = CreateTree();
            PickerTreeNode node = Node("row");
            node.Children.Add(Node("child"));
            var roots = new ObservableCollection<PickerTreeNode> { node };
            using var mirror = new TreeMirror(tree, roots);

            int userExpansions = 0;
            node.ExpandedByUser += _ => userExpansions++;

            node.ExpandForDisplay();

            Assert.True(tree.Nodes[0].IsExpanded); // 画面上は開いている
            Assert.Equal(0, userExpansions);       // が、子を取りに行く合図は出ていない
        });

    /// <summary>
    /// ツリーを丸ごと差し替えたあと、捨てたノードがもう写しに影響しないこと。
    ///
    /// <para>
    /// プレゼンターは捕捉のたびに <c>Roots.Clear()</c> から作り直す。解除を忘れると、
    /// 捨てたはずの部分木の変更通知を受け取り続け、購読が積み上がったうえに
    /// **古いノードの変化で今のツリーが書き換わる**。例外は出ないので気づけない。
    /// </para>
    /// </summary>
    [Fact]
    public void NodesDroppedFromTheModelStopAffectingTheTree()
        => Sta.Run(() =>
        {
            using TreeView tree = CreateTree();
            PickerTreeNode old = Node("old");
            var roots = new ObservableCollection<PickerTreeNode> { old };
            using var mirror = new TreeMirror(tree, roots);

            roots.Clear();
            roots.Add(Node("new"));

            // 捨てたノードを触っても、今のツリーには何も起きない
            old.HasUnrealizedChildren = true;
            old.Children.Add(Node("ghost"));
            old.ExpandForDisplay();

            Assert.Equal(["new"], tree.Nodes.Cast<TreeNode>().Select(n => n.Text));
            Assert.Empty(tree.Nodes[0].Nodes.Cast<TreeNode>());
            Assert.Null(mirror.Find(old));
        });

    /// <summary>個別に取り除いたノードも、写しから消えて購読が切れること。</summary>
    [Fact]
    public void RemovingASingleNodeTakesItsSubtreeWithIt()
        => Sta.Run(() =>
        {
            using TreeView tree = CreateTree();
            PickerTreeNode parent = Node("parent");
            PickerTreeNode child = Node("child");
            parent.Children.Add(child);
            var roots = new ObservableCollection<PickerTreeNode> { parent, Node("other") };
            using var mirror = new TreeMirror(tree, roots);

            roots.Remove(parent);

            Assert.Equal(["other"], tree.Nodes.Cast<TreeNode>().Select(n => n.Text));
            Assert.Null(mirror.Find(parent));
            Assert.Null(mirror.Find(child));
        });

    /// <summary>解放後はツリー側の操作がモデルへ伝わらないこと。</summary>
    [Fact]
    public void AfterDisposeTheTreeNoLongerReportsIntoTheModel()
        => Sta.Run(() =>
        {
            using TreeView tree = CreateTree();
            PickerTreeNode node = Node("row");
            node.Children.Add(Node("child"));
            var roots = new ObservableCollection<PickerTreeNode> { node };
            var mirror = new TreeMirror(tree, roots);
            mirror.Dispose();

            tree.Nodes[0].Expand();

            Assert.False(node.IsExpanded);
        });
}
