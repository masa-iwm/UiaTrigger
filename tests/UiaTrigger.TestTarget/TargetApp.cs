// 対象ウィンドウと、それを変化させるコマンドの実装。
//
// 設計上の約束:
//   ・コマンドは必ず UI スレッド上で同期実行し、応答を返した時点で UI 反映が完了している
//   ・生成する子コントロールは Name (= UIA の AutomationId) と Text (= UIA の Name) を持つ
//   ・構造を変えたら必ず再配置する。要素が重なっていると座標からの記録が別要素を掴む
using System.Globalization;
using System.Text;

namespace UiaTrigger.TestTarget;

internal sealed class TargetApp(string title)
{
    private const int ItemHeight = 32;
    private const int ItemWidth = 220;
    private const int Margin = 12;

    private Form? _form;
    private Panel? _root;
    private readonly List<Form> _extraWindows = [];

    private Form Form => _form ?? throw new InvalidOperationException("no window");

    private Panel Root => _root ?? throw new InvalidOperationException("no window");

    public void Open()
    {
        var form = new Form
        {
            Text = title,
            StartPosition = FormStartPosition.Manual,
            Location = new Point(40, 40),
            // 兄弟走査 (B9) の確認で 20 個以上並べるため縦に長くとる。
            // クライアント領域からはみ出した子は座標から記録できない
            Size = new Size(520, 1000),
            // 記録は座標 (ElementFromPoint) を通るため、他のウィンドウに覆われないようにする。
            // 表示だけの都合であり、入力は一切行わない
            TopMost = true,
            ShowInTaskbar = false,
        };
        var root = new Panel { Name = "root", Dock = DockStyle.Fill };
        form.Controls.Add(root);
        form.Show();

        _form = form;
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
                Form.Text = rest;
                break;

            case "add-button":
                Add(NewButton(rest), Root.Controls.Count);
                break;

            case "add-label":
                Add(NewLabel(rest), Root.Controls.Count);
                break;

            case "insert-sibling-before":
            {
                (string reference, string name) = Two(rest);
                Add(NewButton(name), IndexOf(Root, reference));
                break;
            }

            case "remove":
            {
                Control control = Find(rest);
                Control parent = control.Parent!;
                parent.Controls.Remove(control);
                control.Dispose();
                Layout(parent);
                break;
            }

            case "set-text":
            {
                (string name, string text) = Two(rest);
                // Text は UIA の Name になる。AccessibleName を設定していないため
                // ここが「監視対象のプロパティ変化」そのものになる
                Find(name).Text = text;
                break;
            }

            case "set-accessible-name":
            {
                // UIA の Name を、**変化通知を上げずに**書き換える。
                //
                // Control.Text の代入は MSAA ブリッジの通知経路を通って UIA の
                // PropertyChanged になるが、AccessibleName はただのプロパティなので通らない
                // (accName を上書きするだけ)。これで「画面上は変わっているのに
                // アプリが何も知らせてこない」というアプリが実在する状況を再現できる。
                //
                // **それがポーリング (TriggerDefinition.PollInterval) の存在理由そのものである**
                // (docs/DESIGN.md §5 / docs/MANUAL-CHECKS.md §9)。
                // 「通知を上げないこと」は推測ではなく PollingScenarioTests で測ってある。
                (string name, string text) = Two(rest);
                Find(name).AccessibleName = text;
                break;
            }

            case "set-role":
            {
                // 要素を作り直さずに UIA の ControlType だけを変える。
                // 「生きているが別物になった」= A8 の同一性再検証を実 UIA で起こせる唯一の手段
                (string name, string role) = Two(rest);
                Find(name).AccessibleRole = Enum.Parse<AccessibleRole>(role, ignoreCase: true);
                break;
            }

            case "swap-controltype":
            {
                // Label (Text) ⇄ TextBox (Edit)。Name/Text と位置は保つ
                Control old = Find(rest);
                Control parent = old.Parent!;
                int index = parent.Controls.GetChildIndex(old);
                string name = old.Name;
                string text = old.Text;
                Control replacement = old is Label ? NewTextBox(name) : NewLabel(name);
                replacement.Text = text;
                parent.Controls.Remove(old);
                old.Dispose();
                parent.Controls.Add(replacement);
                parent.Controls.SetChildIndex(replacement, index);
                Layout(parent);
                break;
            }

            case "rebuild-tree":
            {
                // 直下の子をすべて捨てて、同じ種類・同じ名前で作り直す。
                // 掴んでいた要素はゾンビになる (docs/DESIGN.md A8)
                var blueprint = new List<(string Kind, string Name, string Text)>();
                foreach (Control control in Root.Controls)
                {
                    blueprint.Add((KindOf(control), control.Name, control.Text));
                }
                foreach (Control control in Root.Controls.Cast<Control>().ToList())
                {
                    Root.Controls.Remove(control);
                    control.Dispose();
                }
                foreach ((string kind, string name, string text) in blueprint)
                {
                    Control created = kind switch
                    {
                        "label" => NewLabel(name),
                        "edit" => NewTextBox(name),
                        "panel" => NewPanel(name),
                        _ => NewButton(name),
                    };
                    created.Text = text;
                    Root.Controls.Add(created);
                }
                Layout(Root);
                break;
            }

            case "add-depth":
            {
                // 深い階層 (B1)。root/depth0/depth1/.../depth(n-1)/<leaf>
                (string countText, string leaf) = Two(rest);
                int depth = int.Parse(countText, CultureInfo.InvariantCulture);
                Control parent = Root;
                for (int i = 0; i < depth; i++)
                {
                    Panel panel = NewPanel(FormattableString.Invariant($"depth{i}"));
                    panel.Dock = DockStyle.Fill;
                    parent.Controls.Add(panel);
                    parent = panel;
                }
                Button button = NewButton(leaf);
                button.Location = new Point(Margin, Margin);
                parent.Controls.Add(button);
                break;
            }

            case "add-branch":
            {
                // 経路の外にある部分木。ここを churn しても再解決が走らないことの確認に使う (B3)
                Panel panel = NewPanel(rest);
                Add(panel, Root.Controls.Count);
                break;
            }

            case "add-child":
            {
                // 指定パネルの中に子を 1 個足して残す。経路の内と外で
                // 「同じ形の構造変化」を起こして比べるために使う (B3)
                (string parent, string name) = Two(rest);
                Control container = Find(parent);
                container.Controls.Add(NewButton(name));
                Layout(container);
                break;
            }

            case "churn":
            {
                // 指定パネルの中で子を足して消す。構造変化イベントだけを起こす
                (string name, string countText) = Two(rest);
                Control parent = Find(name);
                int count = int.Parse(countText, CultureInfo.InvariantCulture);
                for (int i = 0; i < count; i++)
                {
                    Button button = NewButton(FormattableString.Invariant($"{name}-churn{i}"));
                    parent.Controls.Add(button);
                    parent.Controls.Remove(button);
                    button.Dispose();
                }
                break;
            }

            case "recreate-window":
            {
                // 同じタイトルのウィンドウを閉じて作り直す。HWND が再利用されると
                // 「死んだ要素への購読が残る」形で壊れうる (docs/DESIGN.md A9)
                Form old = Form;
                _form = null;
                _root = null;
                old.Close();
                old.Dispose();
                Open();
                Report(output, "hwnd", Form.Handle.ToInt64().ToString(CultureInfo.InvariantCulture));
                break;
            }

            case "open-window":
            {
                var extra = new Form
                {
                    Text = rest,
                    StartPosition = FormStartPosition.Manual,
                    Location = new Point(600, 40),
                    Size = new Size(240, 160),
                    ShowInTaskbar = false,
                };
                _extraWindows.Add(extra);
                extra.Show();
                break;
            }

            case "close-window":
            {
                Form? extra = _extraWindows.Find(f => string.Equals(f.Text, rest, StringComparison.Ordinal))
                    ?? throw new InvalidOperationException($"no such window: {rest}");
                _extraWindows.Remove(extra);
                extra.Close();
                extra.Dispose();
                break;
            }

            case "place":
            {
                // 窓を物理座標へ置き直す (T4 用)。
                //
                // T4 はピッカーのウィンドウを画面に出したうえで、この窓の中の 1 点を
                // 「カーソルが在る場所」としてピッカーに教える。ピッカーの窓は大きいので、
                // 既定位置 (40,40) のままだと重なる。TopMost にしてあっても、
                // **UIA のヒットテストは重なった WinUI3 の窓を返す**ことを実測した
                // (docs/DESIGN.md §12)。重ならない場所へ動かすしかない。
                //
                // **既定サイズ (520x1000) を縮めるのは T4 の都合であり、T3 では縮めないこと。**
                // あの寸法は兄弟走査 (B9) が 20 個以上の子を縦に並べるために要るもので、
                // クライアント領域からはみ出した子は座標から記録できない (Open のコメントを参照)。
                // 縮めたまま Record_AmongManySiblings_CapturesTheSiblingIndex を走らせると、
                // 例外は出ずに**別の要素を記録した定義**が出来上がる。
                string[] parts = rest.Split(' ');
                int Value(int index) => int.Parse(parts[index], CultureInfo.InvariantCulture);
                Form.Bounds = new Rectangle(Value(0), Value(1), Value(2), Value(3));
                Layout(Root);
                break;
            }

            case "topmost-refresh":
            {
                // トップモースト帯の**先頭へ出し直す**。
                //
                // この窓は TopMost なので、同じ帯の中では最後に位置を主張したものが上に来る。
                // ピッカーのオーバーレイも TopMost なので、これで「対象アプリがオーバーレイの
                // 上に回り込む」状況を作れる。
                //
                // **Activate() / BringToFront() にしないこと。**どちらも
                // SetForegroundWindow を通り、フォアグラウンドロックの規則によって
                // バックグラウンドのプロセスからは**拒否される** (タスクバーが点滅するだけ)。
                // そのまま「再現しない」と結論すると、間違った理由で null な結果になる。
                // 要るのはフォアグラウンド化ではなく帯の先頭へ動くことなので、素の
                // Form プロパティで足りる — P/Invoke もフォアグラウンドロックも関係しない。
                Form.TopMost = false;
                Form.TopMost = true;
                break;
            }

            case "hang":
            {
                // UI スレッドを指定ミリ秒だけ塞ぐ (MANUAL-CHECKS §8)。
                //
                // **応答を先に返してから塞ぐ。**コマンドループは UI スレッドに居る
                // (Program.ReadLoop が pump.Invoke でここへ渡す) ので、この場で眠ると
                // ハーネスが応答を待って**一緒に固まる**。だから眠りは UI スレッドの
                // 待ち行列へ積むだけにして、この Dispatch はすぐ返す。
                //
                // 応答の書き出しは stdin を読んでいるスレッドが行い、UI スレッドを要らない。
                // だから積んだ眠りが先に始まっても応答は出る (順序に依存しない)。
                //
                // **これは協調的な停止である。**自分の UI スレッドを塞ぐモデルであり、
                // 本当に固まったアプリ (デバッガでブレーク等) と同じではない。
                // その差は docs/MANUAL-CHECKS.md §8 に書いてある。
                //
                // **recreate-window と混ぜないこと。**あれは _form を捨てて作り直すので、
                // 塞いでいる最中に呼ぶと積み先が無くなる。
                int milliseconds = int.Parse(rest, CultureInfo.InvariantCulture);
                ArgumentOutOfRangeException.ThrowIfNegative(milliseconds);
                _ = Form.BeginInvoke(() => Thread.Sleep(milliseconds));
                break;
            }

            case "rect":
            {
                Control control = Find(rest);
                Rectangle screen = control.Parent!.RectangleToScreen(control.Bounds);
                Report(output, "rect", FormattableString.Invariant(
                    $"{screen.Left} {screen.Top} {screen.Right} {screen.Bottom}"));
                break;
            }

            case "hwnd":
                Report(output, "hwnd", Form.Handle.ToInt64().ToString(CultureInfo.InvariantCulture));
                break;

            // ---- 以下 3 つは T5 (合成入力) 専用である (docs/TESTING.md §3) ----
            //
            // どれも「このアプリに入力が届いたか」を、UIA を通さずに報告する。
            // UIA で読むと、ピッカーと同じ経路で観測することになり、
            // 片方が詰まればもう片方も詰まる。

            case "add-edit":
            {
                // ←/→ の行き先を見るための TextBox。キャレットの位置が観測点になる
                TextBox box = NewTextBox(rest);
                // 端に居ると ← か → の片方が効かない。真ん中から始める
                box.Text = "0123456789";
                Add(box, Root.Controls.Count);
                break;
            }

            case "focus":
            {
                // フォーカスを当てて、**当たったかどうかを報告する**。
                // 「当てようとした」と「当たった」は別である — フォアグラウンドロックの
                // 規則により、背景のプロセスが自分を前面へ出すことはできない。
                // 撃つ前の検算はテスト側が UIA の HasKeyboardFocus で行うが、
                // こちらからも見えるようにしておくと、食い違ったときにどちらが
                // 嘘をついているか分かる
                Control control = Find(rest);
                Form.Activate();
                control.Focus();
                control.Select();
                Report(output, "focus", control.Focused ? "1" : "0");
                break;
            }

            case "caret":
            {
                // TextBox のキャレット位置。← / → が届けば 1 ずつ動く
                if (Find(rest) is not TextBox box)
                {
                    throw new InvalidOperationException($"not a text box: {rest}");
                }
                Report(output, "caret", box.SelectionStart.ToString(CultureInfo.InvariantCulture));
                break;
            }

            case "clicks":
            {
                // そのコントロールが受け取ったクリックの数。M1 の観測点である。
                // 押されたことを数えるだけで、どこを押されたかは見ない —
                // 座標が正しいことはテスト側が撃つ前に検算する
                Control control = Find(rest);
                Report(output, "clicks", ClickCountOf(control).ToString(CultureInfo.InvariantCulture));
                break;
            }

            case "ping":
                break;

            default:
                throw new InvalidOperationException($"unknown command: {verb}");
        }
    }

    private static void Report(StringBuilder output, string kind, string value)
        => output.Append("data ").Append(kind).Append(' ').Append(value).Append('\n');

    private static string KindOf(Control control) => control switch
    {
        Label => "label",
        TextBox => "edit",
        Panel => "panel",
        _ => "button",
    };

    /// <summary>
    /// そのコントロールが受け取ったクリックの数 (T5 の M1 用)。
    /// </summary>
    /// <remarks>
    /// <c>Control.Tag</c> に数を持たせる。専用のフィールドを足さないのは、
    /// 数えたい相手が <c>NewButton</c> で作られる**すべて**のコントロールだからである。
    /// </remarks>
    private static int ClickCountOf(Control control) => control.Tag as int? ?? 0;

    private static Button NewButton(string name)
    {
        var button = new Button { Name = name, Text = name, Size = new Size(ItemWidth, 26) };
        // 実マウスのクリックだけがここへ来る。UIA の InvokePattern でも Click は上がるが、
        // T5 でこれを読むのは合成入力を撃った後だけである (docs/TESTING.md §3)
        button.Click += (_, _) => button.Tag = ClickCountOf(button) + 1;
        return button;
    }

    private static Label NewLabel(string name) => new() { Name = name, Text = name, AutoSize = false, Size = new Size(ItemWidth, 26) };

    private static TextBox NewTextBox(string name) => new() { Name = name, Text = name, Size = new Size(ItemWidth, 26) };

    private static Panel NewPanel(string name) => new() { Name = name, Size = new Size(ItemWidth, 26) };

    private void Add(Control control, int index)
    {
        Root.Controls.Add(control);
        Root.Controls.SetChildIndex(control, index);
        Layout(Root);
    }

    private static int IndexOf(Control parent, string name)
    {
        for (int i = 0; i < parent.Controls.Count; i++)
        {
            if (string.Equals(parent.Controls[i].Name, name, StringComparison.Ordinal))
            {
                return i;
            }
        }
        throw new InvalidOperationException($"no such control: {name}");
    }

    private Control Find(string name)
    {
        // root 自身も指せるようにする。「経路上で churn する」対照実験で要る
        if (string.Equals(Root.Name, name, StringComparison.Ordinal))
        {
            return Root;
        }
        Control? found = FindIn(Root, name);
        return found ?? throw new InvalidOperationException($"no such control: {name}");
    }

    private static Control? FindIn(Control parent, string name)
    {
        foreach (Control child in parent.Controls)
        {
            if (string.Equals(child.Name, name, StringComparison.Ordinal))
            {
                return child;
            }
            if (FindIn(child, name) is { } nested)
            {
                return nested;
            }
        }
        return null;
    }

    /// <summary>
    /// 直下の子を縦一列に並べ直す。重なりを作らないことが重要 — 重なっていると
    /// 座標から記録したつもりの要素が実際には別のものになり、テストが静かに嘘をつく。
    /// </summary>
    private static void Layout(Control parent)
    {
        for (int i = 0; i < parent.Controls.Count; i++)
        {
            Control child = parent.Controls[i];
            if (child.Dock != DockStyle.None)
            {
                continue;
            }
            child.Location = new Point(Margin, Margin + (i * ItemHeight));
        }
    }

    private static (string First, string Second) Two(string rest)
    {
        string[] parts = rest.Split(' ', 2);
        return parts.Length == 2 ? (parts[0], parts[1]) : (parts[0], string.Empty);
    }
}
