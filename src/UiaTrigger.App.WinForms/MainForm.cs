// サンプルホスト (Windows Forms 版) のメインウィンドウ。
// デザイナーは使わずコードだけで組む — デザイナーが吐く Text = "…" のリテラルを
// ハードコード検出の対象外にしなければならなくなるためである。
using UiaTrigger.App.Shared;
using UiaTrigger.Models;
using UiaTrigger.Persistence;
using UiaTrigger.Picker.WinForms;

namespace UiaTrigger.App.WinForms;

internal sealed class MainForm : Form
{
    // --triggers があればそちら (docs/DESIGN.md §12)。無指定なら実ファイル。
    // 読み書きの両方がこの 1 つのフィールドを通る
    private readonly string _filePath = HostOptions.TriggerFile ?? TriggerFilePath.Default;
    private readonly List<TriggerDefinition> _triggers = [];

    /// <summary>
    /// 開いているピッカー。**複数開ける** — オーバーレイは static singleton ではなく
    /// 登録表方式であり (A18)、その効果はホストが 2 つ目を開けなければ実機で確かめようがない
    /// (docs/MANUAL-CHECKS.md §6)。
    /// </summary>
    private readonly List<TriggerPickerForm> _pickers = [];

    // Control.Name は Windows Forms の UIA プロバイダーが AutomationId として出す。
    // 綴りは WPF / WinUI ホストの x:Name と揃えてある — 揃っていないと T4 が
    // 変種ごとに別のボタンを探すことになる (docs/TESTING.md §1 の T4)。
    private readonly Button _openPicker = new() { Name = "OpenPickerButton", AutoSize = true };
    private readonly Button _openAnother = new() { Name = "OpenAnotherPickerButton", AutoSize = true };
    private readonly Button _delete = new() { Name = "DeleteButton", AutoSize = true };
    private readonly Button _reload = new() { Name = "ReloadButton", AutoSize = true };
    // トリガ一覧エディタ (docs/DESIGN.md §4)。この窓が持っている一覧・削除相当を、
    // 組み込む側が使える形でライブラリが持つ
    private readonly Button _editList = new() { Name = "EditListButton", AutoSize = true };
    private readonly Label _status = new() { Name = "StatusText", AutoSize = true };
    private readonly ListBox _list = new() { Name = "TriggerList", Dock = DockStyle.Fill, HorizontalScrollbar = true, IntegralHeight = false };
    private readonly Label _path = new() { Name = "FilePathText", Dock = DockStyle.Bottom, AutoSize = true };

    public MainForm()
    {
        ClientSize = new Size(900, 600);

        var bar = new FlowLayoutPanel { Dock = DockStyle.Top, AutoSize = true, WrapContents = true };
        bar.Controls.AddRange([_openPicker, _openAnother, _delete, _reload, _editList, _status]);
        Controls.Add(_list);
        Controls.Add(_path);
        Controls.Add(bar);

        Text = AppStrings.Get("WindowTitle");
        _openPicker.Text = AppStrings.Get("OpenPickerButton.Content");
        _openAnother.Text = AppStrings.Get("OpenAnotherPickerButton.Content");
        _delete.Text = AppStrings.Get("DeleteButton.Content");
        _reload.Text = AppStrings.Get("ReloadButton.Content");
        _editList.Text = AppStrings.Get("EditListButton.Content");
        _path.Text = _filePath;

        _openPicker.Click += (_, _) =>
        {
            if (_pickers.Count > 0)
            {
                _pickers[0].Activate();
                return;
            }
            OpenPicker();
        };
        // A18 の確認用。既にあっても必ず新しく開く
        _openAnother.Click += (_, _) => OpenPicker();
        _delete.Click += (_, _) => Delete();
        _reload.Click += (_, _) => Reload();
        _editList.Click += async (_, _) => await EditListAsync();

        Reload();
    }

    /// <summary>
    /// メインウィンドウを閉じたときにピッカーを閉じ忘れると、オーバーレイの
    /// 低レベルキーボードフックが張られたまま取り残される。
    /// </summary>
    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            // Close は FormClosed → _pickers.Remove を同期で走らせうるので、複製してから回す
            foreach (TriggerPickerForm picker in _pickers.ToArray())
            {
                picker.Close();
                picker.Dispose();
            }
            _pickers.Clear();
        }
        base.Dispose(disposing);
    }

    private void Reload()
    {
        try
        {
            _triggers.Clear();
            _triggers.AddRange(TriggerStore.Load(_filePath));
            _status.Text = AppStrings.Format("TriggerCount", _triggers.Count);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            _status.Text = AppStrings.Format("LoadFailed", ex.Message);
        }
        RefreshList();
    }

    private void RefreshList()
    {
        var items = new List<string>();
        foreach (TriggerDefinition def in _triggers)
        {
            // 列挙メンバー名 (Combine / Property / Op / On) は翻訳しない
            string clauses = def.Clauses.Count == 0
                ? AppStrings.Get("NoClauses")
                : string.Join($" {def.Combine} ", def.Clauses.Select(c => $"{c.Property} {c.Op}"));
            items.Add(AppStrings.Format(
                "TriggerRow", def.Id, def.DisplayName, def.Window.ProcessName, def.On, clauses));
        }
        _list.DataSource = items;
    }

    private void Save()
    {
        try
        {
            TriggerStore.Save(_filePath, _triggers);
            _status.Text = AppStrings.Format("TriggerCountSaved", _triggers.Count);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            _status.Text = AppStrings.Format("SaveFailed", ex.Message);
        }
    }

    private void OpenPicker()
    {
        // 既に開いているピッカーの自動選択を切る (docs/DESIGN.md §12)。
        // カーソル位置はプロセスで 1 つなので、入れたままだと 2 枚とも同じ点を捕捉し、
        // 先に開いていたほうが新しいピッカーのマウスに黙って追随して、出していた要素を失う
        // (確定済みの条件は無事である)。
        //
        // **--pick-at で位置を注入している実行では調停しない。**あのとき各ピッカーは
        // 自分に渡された ICursorSource を読むので**ポインターを共有していない** —
        // 取り合いが起きないものを止める理由は無く、止めると A18 の
        // 「2 枚が別々の枠を出す」検査 (T4 / T5) が原理的に成立しなくなる。
        if (HostOptions.Cursors.Count == 0)
        {
            foreach (TriggerPickerForm open in _pickers)
            {
                open.StopAutoSelect();
            }
        }

        // --pick-at が指定されていれば、そこにカーソルが在ることにする。
        // n 枚目は n 番目の座標を受け取る (HostOptions.NextCursor を参照)
        TriggerPickerForm picker = HostOptions.NextCursor() is { } cursor
            ? new TriggerPickerForm(cursor)
            : new TriggerPickerForm();
        picker.TriggerCommitted += (_, args) => BeginInvoke(() =>
        {
            _triggers.RemoveAll(t => string.Equals(t.Id, args.Definition.Id, StringComparison.Ordinal));
            _triggers.Add(args.Definition);
            RefreshList();
            Save();
        });
        picker.FormClosed += (_, _) => _pickers.Remove(picker);
        _pickers.Add(picker);
        picker.Show(this);
    }

    private void Delete()
    {
        int index = _list.SelectedIndex;
        if (index < 0 || index >= _triggers.Count)
        {
            return;
        }
        _triggers.RemoveAt(index);
        RefreshList();
        Save();
    }

    /// <summary>
    /// トリガ一覧エディタを開く (docs/DESIGN.md §4)。
    /// </summary>
    /// <remarks>
    /// **渡して受け取るだけ**である。エディタは保存先を知らないので書くのはここでやる。
    /// エディタが返すのは写しなので、返ってくるまで <c>_triggers</c> は一切変わらない
    /// (取り消しても何も起きない)。
    /// </remarks>
    private async Task EditListAsync()
    {
        _editList.Enabled = false;
        try
        {
            IReadOnlyList<TriggerDefinition>? edited = HostOptions.NextCursor() is { } cursor
                ? await TriggerListEditorForm.EditAsync(this, _triggers, cursor)
                : await TriggerListEditorForm.EditAsync(this, _triggers);
            if (edited is null)
            {
                return; // 取り消し
            }

            _triggers.Clear();
            _triggers.AddRange(edited);
            RefreshList();
            Save();
        }
        finally
        {
            _editList.Enabled = true;
        }
    }
}
