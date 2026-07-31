// 対象ウィンドウと、それを変化させるコマンドの実装 (WPF 版)。
//
// tests/UiaTrigger.TestTarget (WinForms) と同じコマンド語彙を実装する。
// テスト側は TargetProfile を差し替えるだけで同じシナリオを両方に流せる。
//
// 設計上の約束 (WinForms 版と同じ):
//   ・コマンドは必ず UI スレッド上で同期実行し、応答を返した時点で UI 反映が完了している
//   ・生成する子は AutomationId (安定なキー) と Name (変化するテキスト) を持つ。
//     WinForms は Control.Name / Text から自動で付くが、WPF は AutomationProperties で明示的に付ける
//   ・構造を変えたら必ず再配置する。要素が重なっていると座標からの記録が別要素を掴む
using System.Globalization;
using System.Text;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Automation.Peers;
using System.Windows.Controls;
using System.Windows.Interop;

namespace UiaTrigger.TestTarget.Wpf;

internal sealed class TargetApp(string title)
{
    private const double ItemHeight = 32;
    private const double ItemWidth = 220;
    private const double ItemThickness = 26;
    private const double Margin = 12;

    // add-depth の入れ子パネル。すべて (0,0) に重ねるので、葉の座標は入れ子の深さに依らない
    private const double FillWidth = 400;
    private const double FillHeight = 240;

    private Window? _window;
    private TestPanel? _root;
    private readonly List<Window> _extraWindows = [];

    /// <summary>要素に付ける、このハーネスだけが使うメタ情報。</summary>
    /// <param name="Name">コマンドで指す名前。AutomationId とは別に持つ (add-anonymous は AutomationId を持たないため)。</param>
    /// <param name="Fill">親いっぱいに広げる要素か (WinForms の <c>DockStyle.Fill</c> 相当。再配置の対象外)。</param>
    private sealed record Meta(string Name, bool Fill);

    private Window Window => _window ?? throw new InvalidOperationException("no window");

    private TestPanel Root => _root ?? throw new InvalidOperationException("no window");

    public void Open()
    {
        var root = new TestPanel();
        root.Tag = new Meta("root", Fill: true);
        AutomationProperties.SetAutomationId(root, "root");
        AutomationProperties.SetName(root, "root");

        var window = new Window
        {
            Title = title,
            WindowStartupLocation = WindowStartupLocation.Manual,
            Left = 40,
            Top = 40,
            // 兄弟走査 (B9) の確認で 20 個以上並べるため縦に長くとる
            Width = 520,
            Height = 1000,
            // 記録は座標 (ElementFromPoint) を通るため、他のウィンドウに覆われないようにする。
            // 表示だけの都合であり、入力は一切行わない
            Topmost = true,
            ShowInTaskbar = false,
            Content = root,
        };
        window.Show();

        _window = window;
        _root = root;
    }

    /// <summary>1 コマンドを実行し、応答文字列 (末尾は必ず ok/err 行) を返す。</summary>
    public string Execute(string line)
    {
        string verb = line.Split(' ', 2)[0];
        string rest = line.Length > verb.Length ? line[(verb.Length + 1)..] : string.Empty;
        var output = new StringBuilder();
        try
        {
            Dispatch(verb, rest, output);
            // WPF のレイアウトは遅延する。WinForms 版と同じ約束
            // 「応答を返した時点で UI 反映が完了している」を守るため、ここで同期的に流し切る。
            // これが無いと、追加した直後の要素は ActualWidth が 0 のままで
            // rect が親の座標を返し、座標からの記録が<b>静かに別の要素</b>を掴む
            _window?.UpdateLayout();
            output.Append("ok ").Append(verb).Append('\n');
        }
#pragma warning disable CA1031 // どの失敗もテスト側へ err として返す (プロセスを落とさない)
        catch (Exception ex)
#pragma warning restore CA1031
        {
            output.Append("err ").Append(ex.Message.Replace('\n', ' ')).Append('\n');
        }
        return output.ToString();
    }

    private void Dispatch(string verb, string rest, StringBuilder output)
    {
        switch (verb)
        {
            case "title":
                Window.Title = rest;
                break;

            case "add-button":
                Add(NewButton(rest), Root.Children.Count);
                break;

            case "add-label":
                Add(NewLabel(rest), Root.Children.Count);
                break;

            case "add-anonymous":
            {
                // AutomationId を持たない要素。WinForms は Control.Name をそのまま
                // AutomationId に出すため、この状態は WinForms 版では作れない。
                // 記録が Search 方式に落ちず経路方式になることの確認に使う (docs/DESIGN.md §3 の Search 方式)
                var button = new TestButton { Content = rest, Width = ItemWidth, Height = ItemThickness };
                button.Tag = new Meta(rest, Fill: false);
                AutomationProperties.SetName(button, rest);
                Add(button, Root.Children.Count);
                break;
            }

            case "insert-sibling-before":
            {
                (string reference, string name) = Two(rest);
                Add(NewButton(name), IndexOf(Root, reference));
                break;
            }

            case "remove":
            {
                FrameworkElement element = Find(rest);
                Panel parent = ParentOf(element);
                parent.Children.Remove(element);
                Layout(parent);
                break;
            }

            case "set-text":
            {
                (string name, string text) = Two(rest);
                // AutomationProperties.Name が UIA の Name になる。
                // 表示内容も一緒に変えるのは、座標からの記録が実際に見えているものを掴むため
                FrameworkElement element = Find(name);
                SetContent(element, text);
                AutomationProperties.SetName(element, text);
                break;
            }

            case "set-accessible-name":
            {
                // UIA の Name だけを、<b>表示内容を変えずに</b>書き換える。
                //
                // WinForms 側の同名の verb は AccessibleName を差し替えるもので、
                // MSAA ブリッジの通知経路を通らない = 変化通知が上がらない。
                // <b>WPF で同じことが起きるかは自明ではない</b> — ネイティブ UIA
                // プロバイダーは peer のプロパティを自動で突き合わせて通知を上げる仕組みを
                // 持っており、レイアウトが動けばそこで拾われうる (2 つのプロバイダー経路は
                // 非対称である — docs/DESIGN.md §6)。
                // だから set-text と違って SetContent を呼ばない — レイアウトを動かさずに
                // Name だけを変え、通知が上がるかどうかを PollingScenarioTests が実測する。
                (string name, string text) = Two(rest);
                AutomationProperties.SetName(Find(name), text);
                break;
            }

            case "set-role":
            {
                // 要素を作り直さずに UIA の ControlType だけを変える。
                // 「生きているが別物になった」= A8 の同一性再検証を実 UIA で起こす手段
                (string name, string role) = Two(rest);
                PeerOf(Find(name)).ControlType = ParseRole(role);
                break;
            }

            case "swap-controltype":
            {
                // Label (Text) ⇄ TextBox (Edit)。名前と位置は保つが要素は作り直す
                FrameworkElement old = Find(rest);
                Panel parent = ParentOf(old);
                int index = parent.Children.IndexOf(old);
                string name = NameOf(old);
                string text = ContentOf(old);
                FrameworkElement replacement = old is TextBox ? NewLabel(name) : NewTextBox(name);
                SetContent(replacement, text);
                AutomationProperties.SetName(replacement, text);
                parent.Children.Remove(old);
                parent.Children.Insert(index, replacement);
                Layout(parent);
                break;
            }

            case "rebuild-tree":
            {
                // 直下の子をすべて捨てて、同じ種類・同じ名前で作り直す。
                // 掴んでいた要素はゾンビになる (docs/DESIGN.md A8)
                var blueprint = new List<(string Kind, string Name, string Text)>();
                foreach (FrameworkElement child in Root.Children.OfType<FrameworkElement>())
                {
                    blueprint.Add((KindOf(child), NameOf(child), ContentOf(child)));
                }
                Root.Children.Clear();
                foreach ((string kind, string name, string text) in blueprint)
                {
                    FrameworkElement created = kind switch
                    {
                        "label" => NewLabel(name),
                        "edit" => NewTextBox(name),
                        "panel" => NewPanel(name),
                        _ => NewButton(name),
                    };
                    SetContent(created, text);
                    AutomationProperties.SetName(created, text);
                    Root.Children.Add(created);
                }
                Layout(Root);
                break;
            }

            case "add-depth":
            {
                // 深い階層 (B1)。root/depth0/depth1/.../depth(n-1)/<leaf>
                (string countText, string leaf) = Two(rest);
                int depth = int.Parse(countText, CultureInfo.InvariantCulture);
                Panel parent = Root;
                for (int i = 0; i < depth; i++)
                {
                    TestPanel panel = NewPanel(FormattableString.Invariant($"depth{i}"));
                    panel.Tag = new Meta(FormattableString.Invariant($"depth{i}"), Fill: true);
                    panel.Width = FillWidth;
                    panel.Height = FillHeight;
                    Canvas.SetLeft(panel, 0);
                    Canvas.SetTop(panel, 0);
                    parent.Children.Add(panel);
                    parent = panel;
                }
                TestButton button = NewButton(leaf);
                Canvas.SetLeft(button, Margin);
                Canvas.SetTop(button, Margin);
                parent.Children.Add(button);
                break;
            }

            case "add-branch":
            {
                // 経路の外にある部分木。ここを churn しても再解決が走らないことの確認に使う (B3)
                Add(NewPanel(rest), Root.Children.Count);
                break;
            }

            case "add-child":
            {
                // 指定パネルの中に子を 1 個足して残す。経路の内と外で
                // 「同じ形の構造変化」を起こして比べるために使う (B3)
                (string parent, string name) = Two(rest);
                var container = (Panel)Find(parent);
                container.Children.Add(NewButton(name));
                Layout(container);
                break;
            }

            case "churn":
            {
                // 指定パネルの中で子を足して消す。構造変化イベントだけを起こす
                (string name, string countText) = Two(rest);
                var parent = (Panel)Find(name);
                int count = int.Parse(countText, CultureInfo.InvariantCulture);
                for (int i = 0; i < count; i++)
                {
                    TestButton button = NewButton(FormattableString.Invariant($"{name}-churn{i}"));
                    parent.Children.Add(button);
                    parent.Children.Remove(button);
                }
                break;
            }

            case "recreate-window":
            {
                // 同じタイトルのウィンドウを閉じて作り直す。HWND が再利用されると
                // 「死んだ要素への購読が残る」形で壊れうる (docs/DESIGN.md A9)。
                // ShutdownMode = OnExplicitShutdown なので、最後のウィンドウを閉じても
                // Dispatcher は止まらない (WinForms 版の隠し pump Form に相当するものは要らない)
                Window old = Window;
                _window = null;
                _root = null;
                old.Close();
                Open();
                Report(output, "hwnd", HandleOf(Window));
                break;
            }

            case "open-window":
            {
                var extra = new Window
                {
                    Title = rest,
                    WindowStartupLocation = WindowStartupLocation.Manual,
                    Left = 600,
                    Top = 40,
                    Width = 240,
                    Height = 160,
                    ShowInTaskbar = false,
                };
                _extraWindows.Add(extra);
                extra.Show();
                break;
            }

            case "close-window":
            {
                Window extra = _extraWindows.Find(w => string.Equals(w.Title, rest, StringComparison.Ordinal))
                    ?? throw new InvalidOperationException($"no such window: {rest}");
                _extraWindows.Remove(extra);
                extra.Close();
                break;
            }

            case "rect":
            {
                // Visual.PointToScreen は HwndSource の DPI 変換を含むので物理ピクセルで返る。
                // 手計算のスケール係数を持ち込まないこと — 100% の CI では緑のまま通ってしまう
                FrameworkElement element = Find(rest);
                Point topLeft = element.PointToScreen(new Point(0, 0));
                Point bottomRight = element.PointToScreen(new Point(element.ActualWidth, element.ActualHeight));
                Report(output, "rect", FormattableString.Invariant(
                    $"{(int)topLeft.X} {(int)topLeft.Y} {(int)bottomRight.X} {(int)bottomRight.Y}"));
                break;
            }

            case "hwnd":
                Report(output, "hwnd", HandleOf(Window));
                break;

            case "ping":
                break;

            default:
                throw new InvalidOperationException($"unknown command: {verb}");
        }
    }

    private static void Report(StringBuilder output, string kind, string value)
        => output.Append("data ").Append(kind).Append(' ').Append(value).Append('\n');

    private static string HandleOf(Window window)
        => new WindowInteropHelper(window).Handle.ToInt64().ToString(CultureInfo.InvariantCulture);

    /// <summary>
    /// WinForms の <c>AccessibleRole</c> 名を UIA の ControlType に読み替える。
    /// 両プロファイルで同じコマンド行 (<c>set-role btnTarget StaticText</c>) を使えるようにするため。
    /// </summary>
    private static AutomationControlType ParseRole(string role) => role.ToUpperInvariant() switch
    {
        "STATICTEXT" or "TEXT" => AutomationControlType.Text,
        "PUSHBUTTON" or "BUTTON" => AutomationControlType.Button,
        "EDIT" => AutomationControlType.Edit,
        "PANE" or "CLIENT" => AutomationControlType.Pane,
        _ => throw new InvalidOperationException($"unknown role: {role}"),
    };

    private static SwitchablePeer PeerOf(FrameworkElement element)
        => UIElementAutomationPeer.CreatePeerForElement(element) as SwitchablePeer
           ?? throw new InvalidOperationException($"element has no switchable peer: {NameOf(element)}");

    private static string KindOf(FrameworkElement element) => element switch
    {
        TestLabel => "label",
        TextBox => "edit",
        TestPanel => "panel",
        _ => "button",
    };

    private static string NameOf(FrameworkElement element)
        => element.Tag is Meta meta ? meta.Name : string.Empty;

    private static string ContentOf(FrameworkElement element) => element switch
    {
        TextBox box => box.Text,
        ContentControl content => content.Content as string ?? string.Empty,
        _ => string.Empty,
    };

    private static void SetContent(FrameworkElement element, string text)
    {
        switch (element)
        {
            case TextBox box:
                box.Text = text;
                break;
            case ContentControl content:
                content.Content = text;
                break;
            default:
                break;
        }
    }

    private static TestButton NewButton(string name)
        => Identify(new TestButton { Content = name, Width = ItemWidth, Height = ItemThickness }, name);

    private static TestLabel NewLabel(string name)
        => Identify(new TestLabel { Content = name, Width = ItemWidth, Height = ItemThickness }, name);

    private static TextBox NewTextBox(string name)
        => Identify(new TextBox { Text = name, Width = ItemWidth, Height = ItemThickness }, name);

    private static TestPanel NewPanel(string name)
        => Identify(new TestPanel { Width = ItemWidth, Height = ItemThickness }, name);

    /// <summary>
    /// AutomationId (安定なキー) と Name (変化するテキスト) を明示的に付ける。
    /// WinForms は <c>Control.Name</c> / <c>Text</c> から自動で付くが、WPF は付かない。
    /// </summary>
    private static T Identify<T>(T element, string name) where T : FrameworkElement
    {
        element.Tag = new Meta(name, Fill: false);
        AutomationProperties.SetAutomationId(element, name);
        AutomationProperties.SetName(element, name);
        return element;
    }

    private void Add(FrameworkElement element, int index)
    {
        Root.Children.Insert(index, element);
        Layout(Root);
    }

    private static int IndexOf(Panel parent, string name)
    {
        for (int i = 0; i < parent.Children.Count; i++)
        {
            if (parent.Children[i] is FrameworkElement child && string.Equals(NameOf(child), name, StringComparison.Ordinal))
            {
                return i;
            }
        }
        throw new InvalidOperationException($"no such control: {name}");
    }

    private FrameworkElement Find(string name)
        // root 自身も指せるようにする。「経路上で churn する」対照実験で要る
        => string.Equals(NameOf(Root), name, StringComparison.Ordinal)
            ? Root
            : FindIn(Root, name) ?? throw new InvalidOperationException($"no such control: {name}");

    private static FrameworkElement? FindIn(Panel parent, string name)
    {
        foreach (FrameworkElement child in parent.Children.OfType<FrameworkElement>())
        {
            if (string.Equals(NameOf(child), name, StringComparison.Ordinal))
            {
                return child;
            }
            if (child is Panel nested && FindIn(nested, name) is { } found)
            {
                return found;
            }
        }
        return null;
    }

    private static Panel ParentOf(FrameworkElement element)
        => element.Parent as Panel ?? throw new InvalidOperationException($"no parent: {NameOf(element)}");

    /// <summary>
    /// 直下の子を縦一列に並べ直す。重なりを作らないことが重要 — 重なっていると
    /// 座標から記録したつもりの要素が実際には別のものになり、テストが静かに嘘をつく。
    /// </summary>
    private static void Layout(Panel parent)
    {
        int row = 0;
        foreach (FrameworkElement child in parent.Children.OfType<FrameworkElement>())
        {
            if (child.Tag is Meta { Fill: true })
            {
                continue;
            }
            Canvas.SetLeft(child, Margin);
            Canvas.SetTop(child, Margin + (row * ItemHeight));
            row++;
        }
    }

    private static (string First, string Second) Two(string rest)
    {
        string[] parts = rest.Split(' ', 2);
        return parts.Length == 2 ? (parts[0], parts[1]) : (parts[0], string.Empty);
    }
}
