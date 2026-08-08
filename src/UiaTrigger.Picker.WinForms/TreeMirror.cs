// PickerTreeNode のツリーを Windows Forms の TreeView へ写す (docs/DESIGN.md §12)。
//
// 3 変種のうちここにしか無いコードである。WinUI と WPF は ItemsSource に
// ObservableCollection を渡せば済むが、Windows Forms の TreeView はモデルを見ないので
// 手で同期させるしかない。
//
// 同期そのものより**解除**のほうが危ない。ノードが外れたときに CollectionChanged と
// PropertyChanged を外し忘れると、捨てたはずの部分木がツリーの更新を受け取り続ける
// (プレゼンターは捕捉のたびに Roots を作り直すので、放っておくと購読が積み上がる)。
using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;

namespace UiaTrigger.Picker.WinForms;

/// <summary>
/// <see cref="PickerTreeNode"/> の木と <see cref="TreeView"/> の中身を対応させ続ける。
/// </summary>
/// <remarks>
/// <para>
/// 対応は双方向だが非対称である。モデル → ツリーは全部 (追加・削除・並び・展開状態・
/// 子の有無)、ツリー → モデルは**ユーザーの操作だけ** (展開・折りたたみ) を返す。
/// </para>
/// <para>
/// 展開の扱いがこの層の要点になる。ユーザーが <c>[+]</c> を押したときだけ
/// 「開いた = 子を全列挙してよい」であり、ピッカーが経路を見せるために開いたときは違う。
/// <see cref="PickerTreeNode.IsExpanded"/> の同値ガードがその区別を保っているので、
/// ここからは**常に同じ代入**をしてよい — 既に開いている段に true を入れても
/// 何も起きない (docs/DESIGN.md §12)。
/// </para>
/// </remarks>
internal sealed class TreeMirror : IDisposable
{
    /// <summary>
    /// 子がまだ無い段に <c>[+]</c> を出すための仮ノード。
    /// Windows Forms の TreeView は子を 1 つも持たない段に展開記号を出さないため。
    /// </summary>
    private const string PlaceholderKey = "__unrealized__";

    private readonly TreeView _tree;
    private readonly ObservableCollection<PickerTreeNode> _roots;
    private readonly Dictionary<PickerTreeNode, TreeNode> _map = [];
    private bool _disposed;

    public TreeMirror(TreeView tree, ObservableCollection<PickerTreeNode> roots)
    {
        _tree = tree;
        _roots = roots;
        Attach(_tree.Nodes, _roots);
        _tree.AfterExpand += OnAfterExpand;
        _tree.AfterCollapse += OnAfterCollapse;
    }

    /// <summary>ノードに対応する <see cref="TreeNode"/>。まだ写していなければ null。</summary>
    public TreeNode? Find(PickerTreeNode node) => _map.GetValueOrDefault(node);

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }
        _disposed = true;
        _tree.AfterExpand -= OnAfterExpand;
        _tree.AfterCollapse -= OnAfterCollapse;
        Detach(_roots);
        foreach (PickerTreeNode node in _map.Keys.ToArray())
        {
            node.PropertyChanged -= OnNodePropertyChanged;
        }
        _map.Clear();
    }

    // ---------- モデル → ツリー ----------

    private void Attach(TreeNodeCollection target, ObservableCollection<PickerTreeNode> source)
    {
        source.CollectionChanged += OnChildrenChanged;
        foreach (PickerTreeNode node in source)
        {
            target.Add(CreateNode(node));
        }
    }

    private void Detach(ObservableCollection<PickerTreeNode> source)
    {
        source.CollectionChanged -= OnChildrenChanged;
        foreach (PickerTreeNode node in source)
        {
            Forget(node);
        }
    }

    /// <summary>部分木ぶんの購読をすべて解除する。</summary>
    private void Forget(PickerTreeNode node)
    {
        node.PropertyChanged -= OnNodePropertyChanged;
        _map.Remove(node);
        Detach(node.Children);
    }

    private TreeNode CreateNode(PickerTreeNode node)
    {
        var treeNode = new TreeNode(node.Display) { Tag = node };
        _map[node] = treeNode;
        node.PropertyChanged += OnNodePropertyChanged;
        Attach(treeNode.Nodes, node.Children);
        UpdatePlaceholder(node, treeNode);
        if (node.IsExpanded)
        {
            treeNode.Expand();
        }
        return treeNode;
    }

    private void OnChildrenChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        if (sender is not ObservableCollection<PickerTreeNode> source)
        {
            return;
        }
        TreeNodeCollection target = OwnerOf(source);

        switch (e.Action)
        {
            case NotifyCollectionChangedAction.Add:
                for (int i = 0; i < (e.NewItems?.Count ?? 0); i++)
                {
                    var node = (PickerTreeNode)e.NewItems![i]!;
                    target.Insert(e.NewStartingIndex + i, CreateNode(node));
                }
                break;

            case NotifyCollectionChangedAction.Remove:
                foreach (PickerTreeNode node in e.OldItems!)
                {
                    if (_map.TryGetValue(node, out TreeNode? treeNode))
                    {
                        treeNode.Remove();
                    }
                    Forget(node);
                }
                break;

            case NotifyCollectionChangedAction.Reset:
                // Clear() は消えた項目を教えてくれないので、写しの側から辿って落とす
                foreach (TreeNode treeNode in target.Cast<TreeNode>().ToArray())
                {
                    if (treeNode.Tag is PickerTreeNode node)
                    {
                        Forget(node);
                    }
                }
                target.Clear();
                foreach (PickerTreeNode node in source)
                {
                    target.Add(CreateNode(node));
                }
                break;

            default:
                // Replace / Move はプレゼンターが使わない。使い始めたらここで落ちるほうがよい。
                // **文面は英語** — 利用者には出ないプログラマ向けの契約であり、
                // 表示用の文字列 (リソース経由) とは別である (docs/LOCALIZATION.md §3 / 台帳 L2)
                throw new NotSupportedException($"Cannot mirror a '{e.Action}' change of the tree.");
        }

        // 子が増減すれば「まだ列挙していない」印の要否も変わる
        if (OwnerNodeOf(source) is { } owner && _map.TryGetValue(owner, out TreeNode? ownerTreeNode))
        {
            UpdatePlaceholder(owner, ownerTreeNode);
        }
    }

    private void OnNodePropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (sender is not PickerTreeNode node || !_map.TryGetValue(node, out TreeNode? treeNode))
        {
            return;
        }
        switch (e.PropertyName)
        {
            case nameof(PickerTreeNode.IsExpanded):
                if (node.IsExpanded)
                {
                    treeNode.Expand();
                }
                else
                {
                    treeNode.Collapse();
                }
                break;

            case nameof(PickerTreeNode.HasUnrealizedChildren):
                UpdatePlaceholder(node, treeNode);
                break;

            default:
                break;
        }
    }

    /// <summary>まだ子を取っていない段に <c>[+]</c> を出す (もう要らなければ消す)。</summary>
    private static void UpdatePlaceholder(PickerTreeNode node, TreeNode treeNode)
    {
        bool wanted = node.HasUnrealizedChildren && node.Children.Count == 0;
        TreeNode? placeholder = treeNode.Nodes.Cast<TreeNode>()
            .FirstOrDefault(n => string.Equals(n.Name, PlaceholderKey, StringComparison.Ordinal));

        if (wanted && placeholder is null)
        {
            treeNode.Nodes.Add(new TreeNode { Name = PlaceholderKey });
        }
        else if (!wanted)
        {
            placeholder?.Remove();
        }
    }

    private TreeNodeCollection OwnerOf(ObservableCollection<PickerTreeNode> source)
        => ReferenceEquals(source, _roots)
            ? _tree.Nodes
            : _map[OwnerNodeOf(source)!].Nodes;

    private PickerTreeNode? OwnerNodeOf(ObservableCollection<PickerTreeNode> source)
        => ReferenceEquals(source, _roots)
            ? null
            : _map.Keys.First(n => ReferenceEquals(n.Children, source));

    // ---------- ツリー → モデル ----------

    private void OnAfterExpand(object? sender, TreeViewEventArgs e)
    {
        // ユーザーが開いたのか、こちらが開いたのかを見分ける必要は無い。
        // 既に true なら PickerTreeNode の同値ガードで何も起きず、false から true に
        // なるのはユーザーが [+] を押したときだけである
        if (e.Node?.Tag is PickerTreeNode node)
        {
            node.IsExpanded = true;
        }
    }

    private void OnAfterCollapse(object? sender, TreeViewEventArgs e)
    {
        if (e.Node?.Tag is PickerTreeNode node)
        {
            node.IsExpanded = false;
        }
    }
}
