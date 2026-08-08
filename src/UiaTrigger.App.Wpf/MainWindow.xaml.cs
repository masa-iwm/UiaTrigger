using System.IO;
using System.Linq;
using System.Windows;
using UiaTrigger.App.Shared;
using UiaTrigger.Models;
using UiaTrigger.Persistence;
using UiaTrigger.Picker.Wpf;

namespace UiaTrigger.App.Wpf;

/// <summary>The WPF sample host's main window: list the triggers, and open pickers.</summary>
public partial class MainWindow : Window, IDisposable
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
    private readonly List<TriggerPickerWindow> _pickers = [];
    private bool _disposed;

    /// <summary>Creates the window and loads the trigger file.</summary>
    public MainWindow()
    {
        InitializeComponent();
        ApplyStrings();
        FilePathText.Text = _filePath;
        Reload();
        // メインウィンドウを閉じたときにピッカーを閉じ忘れると、
        // オーバーレイの低レベルキーボードフックが張られたまま取り残される。
        Closed += (_, _) => Dispose();
    }

    private void ApplyStrings()
    {
        Title = AppStrings.Get("WindowTitle");
        OpenPickerButton.Content = AppStrings.Get("OpenPickerButton.Content");
        OpenAnotherPickerButton.Content = AppStrings.Get("OpenAnotherPickerButton.Content");
        DeleteButton.Content = AppStrings.Get("DeleteButton.Content");
        ReloadButton.Content = AppStrings.Get("ReloadButton.Content");
        EditListButton.Content = AppStrings.Get("EditListButton.Content");
    }

    /// <summary>開いたままのピッカーをすべて閉じて解放する。複数回呼んでも安全。</summary>
    public void Dispose()
    {
        Dispose(disposing: true);
        GC.SuppressFinalize(this);
    }

    /// <summary>Closes every open picker.</summary>
    /// <param name="disposing">True when called from <see cref="Dispose()"/>.</param>
    protected virtual void Dispose(bool disposing)
    {
        if (_disposed)
        {
            return;
        }
        _disposed = true;
        if (!disposing)
        {
            return;
        }

        // Close は Closed → _pickers.Remove を同期で走らせうるので、複製してから回す
        foreach (TriggerPickerWindow picker in _pickers.ToArray())
        {
            picker.Close();   // Close → ピッカー側の Closed で Dispose される
            picker.Dispose(); // 念のため冪等に呼ぶ (Close が発火しない経路への保険)
        }
        _pickers.Clear();
    }

    private void Reload()
    {
        // 拾う例外の顔ぶれは App.Shared に 1 つだけ置く (docs/DESIGN.md §12)。
        // ここに写すと、TriggerStore が文書化した例外の追加に 3 変種とも追随できない —
        // WPF では StartupUri の窓生成中に漏れると、窓が 1 つも出ないまま
        // メッセージループだけが走り続ける (DispatcherUnhandledException が飲む)
        _triggers.Clear();
        if (HostTriggerFile.TryLoad(_filePath, out IReadOnlyList<TriggerDefinition> loaded, out string? error))
        {
            _triggers.AddRange(loaded);
            StatusText.Text = AppStrings.Format("TriggerCount", _triggers.Count);
        }
        else
        {
            StatusText.Text = AppStrings.Format("LoadFailed", error);
        }
        RefreshList();
    }

    private void RefreshList()
    {
        var items = new List<string>();
        foreach (TriggerDefinition def in _triggers)
        {
            // 列挙メンバー名 (Combine / Property / Op / On) は翻訳しない。
            // ユーザーが JSON やピッカーで目にするのと同じ語でなければ対応が取れなくなる
            string clauses = def.Clauses.Count == 0
                ? AppStrings.Get("NoClauses")
                : string.Join($" {def.Combine} ", def.Clauses.Select(c => $"{c.Property} {c.Op}"));
            items.Add(AppStrings.Format(
                "TriggerRow", def.Id, def.DisplayName, def.Window.ProcessName, def.On, clauses));
        }
        TriggerList.ItemsSource = items;
    }

    private void Save()
    {
        StatusText.Text = HostTriggerFile.TrySave(_filePath, _triggers, out string? error)
            ? AppStrings.Format("TriggerCountSaved", _triggers.Count)
            : AppStrings.Format("SaveFailed", error);
    }

    /// <summary>ピッカーを開く。既に開いていればそれを前面に出す。</summary>
    private void OnOpenPicker(object sender, RoutedEventArgs e)
    {
        if (_pickers.Count > 0)
        {
            _pickers[0].Activate();
            return;
        }
        OpenPicker();
    }

    /// <summary>もう 1 つピッカーを開く (A18 の確認用。既にあっても必ず新しく開く)。</summary>
    private void OnOpenAnotherPicker(object sender, RoutedEventArgs e) => OpenPicker();

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
            foreach (TriggerPickerWindow open in _pickers)
            {
                open.StopAutoSelect();
            }
        }

        // --pick-at が指定されていれば、そこにカーソルが在ることにする。
        // n 枚目は n 番目の座標を受け取る (HostOptions.NextCursor を参照)
        TriggerPickerWindow picker = HostOptions.NextCursor() is { } cursor
            ? new TriggerPickerWindow(cursor)
            : new TriggerPickerWindow();
        picker.TriggerCommitted += (_, args) =>
        {
            Dispatcher.BeginInvoke(() =>
            {
                _triggers.RemoveAll(t => string.Equals(t.Id, args.Definition.Id, StringComparison.Ordinal));
                _triggers.Add(args.Definition);
                RefreshList();
                Save();
            });
        };
        picker.Closed += (_, _) => _pickers.Remove(picker);
        _pickers.Add(picker);
        picker.Show();
        picker.Activate();
    }

    private void OnDelete(object sender, RoutedEventArgs e)
    {
        int index = TriggerList.SelectedIndex;
        if (index < 0 || index >= _triggers.Count)
        {
            return;
        }
        _triggers.RemoveAt(index);
        RefreshList();
        Save();
    }

    private void OnReload(object sender, RoutedEventArgs e) => Reload();

    /// <summary>
    /// トリガ一覧エディタを開く (docs/DESIGN.md §4)。
    /// </summary>
    /// <remarks>
    /// **渡して受け取るだけ**である。エディタは保存先を知らないので書くのはここでやる。
    /// エディタが返すのは写しなので、返ってくるまで <c>_triggers</c> は一切変わらない
    /// (取り消しても何も起きない)。このホストは監視を持たない — README の
    /// "Two asymmetries are deliberate" のとおり、それは WinUI 版だけの役目である。
    /// </remarks>
    private async void OnEditList(object sender, RoutedEventArgs e)
    {
        EditListButton.IsEnabled = false;
        try
        {
            IReadOnlyList<TriggerDefinition>? edited = HostOptions.NextCursor() is { } cursor
                ? await TriggerListEditorWindow.EditAsync(this, _triggers, cursor)
                : await TriggerListEditorWindow.EditAsync(this, _triggers);
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
            EditListButton.IsEnabled = true;
        }
    }
}
