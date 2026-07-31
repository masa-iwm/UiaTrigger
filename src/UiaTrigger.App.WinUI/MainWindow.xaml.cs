using System.Collections.ObjectModel;
using Microsoft.UI.Xaml;
using UiaTrigger.Models;
using UiaTrigger.Monitoring;
using UiaTrigger.Persistence;
using UiaTrigger.Picker.WinUI;

namespace UiaTrigger.App.WinUI;

public sealed partial class MainWindow : Window, IDisposable
{
    /// <summary>
    /// 監視ログに残す行数の上限。
    /// 発火は 1 秒に何度も来うるので、際限なく積むサンプルは
    /// そのまま「メモリを食う使い方」の見本になる (docs/DESIGN.md §12)。
    /// </summary>
    private const int MonitorLogLimit = 200;

    // --triggers があればそちら (docs/DESIGN.md §12)。無指定なら実ファイル。
    // 読み書きの両方がこの 1 つのフィールドを通る
    private readonly string _filePath = HostOptions.TriggerFile ?? TriggerFilePath.Default;
    // キーは定義自身が持つ。並行配列で別持ちしない (docs/DESIGN.md §3)
    private readonly List<TriggerDefinition> _triggers = [];
    /// <summary>
    /// 開いているピッカー。<b>複数開ける</b> — オーバーレイは static singleton ではなく
    /// 登録表方式であり (A18)、その効果はホストが 2 つ目を開けなければ実機で確かめようがない
    /// (docs/MANUAL-CHECKS.md §6)。
    /// </summary>
    private readonly List<TriggerPickerWindow> _pickers = [];

    /// <summary>
    /// 監視ログの行。<see cref="ObservableCollection{T}"/> なので、
    /// <c>ItemsSource</c> は 1 度だけ結び付ければよい。
    /// </summary>
    private readonly ObservableCollection<string> _log = [];

    /// <summary>
    /// 動いている監視。null = 停止中。<b>UI スレッドからのみ読み書きする。</b>
    /// </summary>
    private TriggerMonitor? _monitor;
    private bool _disposed;

    public MainWindow()
    {
        InitializeComponent();
        // Window は FrameworkElement ではないので x:Uid が効かない
        Title = AppStrings.Get("WindowTitle");
        FilePathText.Text = _filePath;
        MonitorLogList.ItemsSource = _log;
        Reload();
        // メインウィンドウを閉じたときにピッカーを閉じ忘れると、
        // オーバーレイの低レベルキーボードフックが張られたまま取り残される。
        Closed += (_, _) => Dispose();
    }

    /// <summary>監視を止め、開いたままのピッカーをすべて閉じて解放する。複数回呼んでも安全。</summary>
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }
        _disposed = true;

        // 監視を先に止める。ここで待ち切っても行き止まりにはならない —
        // TriggerMonitor.DisposeAsync が待つのは自分のディスパッチャとイベントキューだけであり、
        // こちらのハンドラーは DispatcherQueue へ積んで即座に返る (UI スレッドを待たない)。
        if (_monitor is { } monitor)
        {
            _monitor = null;
            Unsubscribe(monitor);
            monitor.DisposeAsync().AsTask().GetAwaiter().GetResult();
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
        try
        {
            _triggers.Clear();
            _triggers.AddRange(TriggerStore.Load(_filePath));
            StatusText.Text = AppStrings.Format("TriggerCount", _triggers.Count);
        }
        catch (Exception ex)
        {
            StatusText.Text = AppStrings.Format("LoadFailed", ex.Message);
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
            if (def.Expression is { } expression)
            {
                // 複合条件は 1 行に収まらないので別書式にする。プロセス名を出しても
                // 意味が無い (要素ごとに違う) ので、代わりに条件の数と式を出す。
                //
                // <b>「要素が何個か」をここで数えないこと。</b>同じ要素かどうかは
                // Window / Locator の値で決まるが、あの 2 つは Equals を持たない可変クラスなので、
                // 素朴に比べると JSON から読んだ「同じ要素を指す 2 句」を別物と数えてしまう。
                // 正しい数はライブラリが持っている — 監視中の
                // TriggerMonitorDiagnostics.ElementSlotCount がそれである
                items.Add(AppStrings.Format(
                    "CompositeRow", def.Id, def.Clauses.Count, def.On, expression));
                continue;
            }
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
        try
        {
            TriggerStore.Save(_filePath, _triggers);
            StatusText.Text = AppStrings.Format("TriggerCountSaved", _triggers.Count);
        }
        catch (Exception ex)
        {
            StatusText.Text = AppStrings.Format("SaveFailed", ex.Message);
        }
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
        // <b>--pick-at で位置を注入している実行では調停しない。</b>あのとき各ピッカーは
        // 自分に渡された ICursorSource を読むので<b>ポインターを共有していない</b> —
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
            DispatcherQueue.TryEnqueue(() =>
            {
                _triggers.RemoveAll(t => string.Equals(t.Id, args.Definition.Id, StringComparison.Ordinal));
                _triggers.Add(args.Definition);
                RefreshList();
                Save();
                // 監視中なら、止めずにこの 1 件だけ入れ替える。
                // 例外は ReregisterAsync の中でログ行になるので、ここは捨ててよい
                _ = ReregisterAsync(args.Definition);
            });
        };
        picker.Closed += (_, _) => _pickers.Remove(picker);
        _pickers.Add(picker);
        picker.Activate();
    }

    private void OnDelete(object sender, RoutedEventArgs e)
    {
        // SelectedIndex (単一) ではなく SelectedRanges を読む。一覧は「選択をまとめる」の
        // ために SelectionMode=Extended なので、単一だと複数選んで押しても 1 件しか消えず、
        // しかも例外が出ない。選択の読み方は OnCombine と同じにする
        List<int> indexes = [.. TriggerList.SelectedRanges
            .SelectMany(r => Enumerable.Range(r.FirstIndex, (int)r.Length))
            .Where(i => i >= 0 && i < _triggers.Count)
            .Order()];
        if (indexes.Count == 0)
        {
            return;
        }
        var removedIds = new List<string>(indexes.Count);
        // 後ろから消す。前から消すと残りの index がずれる
        for (int i = indexes.Count - 1; i >= 0; i--)
        {
            removedIds.Add(_triggers[indexes[i]].Id);
            _triggers.RemoveAt(indexes[i]);
        }
        RefreshList();
        Save();
        foreach (string id in removedIds)
        {
            _ = UnregisterAsync(id);
        }
    }

    // ---------- 複合条件 (docs/DESIGN.md §4) ----------
    //
    // 録ったトリガーを選んで 1 件にまとめる。条件の名前には元のトリガーの id をそのまま
    // 使うので、式が「login && !busy」のように読める形になる。
    //
    // <b>まとめた結果も同じ一覧に並ぶ。</b>それをまた選んでまとめれば入れ子になるので、
    // 入れ子のための UI は要らない。
    //
    // 組む規則そのものはここには無い — TriggerComposer (docs/DESIGN.md §4) が持つ。
    // ここに残るのは UI の読み書きと、ホストにしか出来ないこと (保存・監視の入れ替え) だけ。

    private void OnCombine(object sender, RoutedEventArgs e)
    {
        List<TriggerDefinition> sources = [.. TriggerList.SelectedRanges
            .SelectMany(r => Enumerable.Range(r.FirstIndex, (int)r.Length))
            .Where(i => i >= 0 && i < _triggers.Count)
            .Order()
            .Select(i => _triggers[i])];

        // 「絞るだけ」にする元トリガーの id。ここに書いた条件は購読されず、その変化では鳴らない
        string[] unwatched = UnwatchedText.Text.Split(
            ',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);

        TriggerCompositionResult result = TriggerComposer.Compose(
            sources, ExpressionText.Text, unwatched, _triggers.Select(t => t.Id));
        if (!result.IsValid)
        {
            StatusText.Text = AppStrings.Format("CombineFailed", result.Error);
            return;
        }

        TriggerDefinition composite = result.Definition!;
        _triggers.Add(composite);
        RefreshList();
        Save();
        StatusText.Text = AppStrings.Format("CombineDone", composite.Id, sources.Count);
        _ = ReregisterAsync(composite);
    }

    /// <summary>
    /// 再読込。<b>監視中なら先に止める</b> — 一覧を丸ごと入れ替えるので、
    /// 走らせたままにすると「画面のトリガーと監視しているトリガーが違う」状態になる。
    /// </summary>
    private async void OnReload(object sender, RoutedEventArgs e)
    {
        if (_monitor is not null)
        {
            await StopMonitorAsync();
        }
        Reload();
    }

    /// <summary>
    /// トリガ一覧エディタを開く (docs/DESIGN.md §4)。
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>渡して受け取るだけ</b>である。エディタは保存先も監視も知らないので、
    /// 書くのも登録し直すのもここでやる。エディタが返すのは写しなので、
    /// 返ってくるまで <c>_triggers</c> は一切変わらない (取り消しても何も起きない)。
    /// </para>
    /// <para>
    /// <b>監視中なら先に止める</b> — 一覧を丸ごと入れ替えるので、理由は
    /// <see cref="OnReload"/> と同じである。差分だけ登録し直す形は書かない:
    /// エディタは「どれが変わったか」を返さないし、返させると
    /// <b>監視の所有をエディタへ渡すことになる</b>。
    /// </para>
    /// <para>
    /// WinUI のエディタは<b>モーダルではない</b> (WinUI3 に窓単位のモーダルが無い)。
    /// 押した口を無効にしておくのが、再入を防ぐいちばん簡単な形である。
    /// </para>
    /// </remarks>
    private async void OnEditList(object sender, RoutedEventArgs e)
    {
        EditListButton.IsEnabled = false;
        try
        {
            if (_monitor is not null)
            {
                await StopMonitorAsync();
            }

            IReadOnlyList<TriggerDefinition>? edited = HostOptions.NextCursor() is { } cursor
                ? await TriggerListEditorWindow.EditAsync(_triggers, cursor)
                : await TriggerListEditorWindow.EditAsync(_triggers);
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

    // ---------- D9: ピッカー → 監視の E2E ----------
    //
    // ライブラリの売りは「記録した定義で実際に発火すること」であり、記録して JSON に
    // するだけのサンプルでは、その半分しか見せられない (docs/DESIGN.md D9 / §12)。
    //
    // 3 つあるサンプルホストのうち、これを持つのは WinUI 版だけである
    // (README の "Two asymmetries are deliberate")。監視の配線に UI フレームワーク固有の
    // ものは無いので、3 重に書いても見せられるものは増えない。

    private async void OnStartMonitor(object sender, RoutedEventArgs e)
    {
        if (_monitor is not null)
        {
            return;
        }
        StartMonitorButton.IsEnabled = false;

        // ホスト自身は要素を調べないので、単体の TriggerMonitor でよい。
        // ホストが UiaSession を持つ場合は UiaSession.CreateMonitor のほうが望ましい —
        // あちらは監視を同じ MTA スレッドへ載せる (2 本目を立てない)。
        // ここでピッカーのセッションを借りないのは、ピッカーが自分のセッションを
        // 内部に閉じていて外へ出さないためである
        var monitor = new TriggerMonitor(new TriggerMonitorOptions
        {
            Session = new UiaSessionOptions { ThreadName = "UiaTrigger.App.WinUI" },
        });
        monitor.TriggerFired += OnTriggerFired;
        monitor.ResolutionChanged += OnResolutionChanged;
        // 握り潰さない。ライブラリの doc が「トリガーが発火しない理由」として
        // 昇格アプリのような他に出口の無いものを挙げている
        monitor.UnhandledException += OnMonitorException;

        try
        {
            // 定義エラーは StartAsync が呼び出し元スレッドで投げる (UIA へ積む前に)
            await monitor.StartAsync(_triggers);
        }
        catch (ArgumentException ex)
        {
            Unsubscribe(monitor);
            await monitor.DisposeAsync();
            StatusText.Text = AppStrings.Format("MonitorStartFailed", ex.Message);
            StartMonitorButton.IsEnabled = true;
            return;
        }

        _monitor = monitor;
        StopMonitorButton.IsEnabled = true;
        // 要素の数はライブラリに訊く。1 つのトリガーが複数の要素にまたがれるので
        // トリガーの件数だけでは何を見張っているのか分からず、
        // 「同じ要素を指す条件がまとまっているか」もここでしか確かめられない (docs/DESIGN.md §4)
        StatusText.Text = AppStrings.Format(
            "MonitorStarted", _triggers.Count, monitor.GetDiagnostics().ElementSlotCount);
    }

    private async void OnStopMonitor(object sender, RoutedEventArgs e)
    {
        StopMonitorButton.IsEnabled = false;
        await StopMonitorAsync();
        StatusText.Text = AppStrings.Get("MonitorStopped");
    }

    private async Task StopMonitorAsync()
    {
        if (_monitor is not { } monitor)
        {
            return;
        }
        _monitor = null;
        Unsubscribe(monitor);
        await monitor.DisposeAsync();
        StopMonitorButton.IsEnabled = false;
        StartMonitorButton.IsEnabled = true;
    }

    private void Unsubscribe(TriggerMonitor monitor)
    {
        monitor.TriggerFired -= OnTriggerFired;
        monitor.ResolutionChanged -= OnResolutionChanged;
        monitor.UnhandledException -= OnMonitorException;
    }

    /// <summary>
    /// 1 件だけ登録し直す (走らせたまま編集できることの実演)。
    /// </summary>
    /// <remarks>
    /// <b>必ず外してから足す。</b>ピッカーの確定は「同じ id を録り直す」経路を普通に通り、
    /// <c>AddAsync</c> は id が重複すると <see cref="ArgumentException"/> を投げる。
    /// </remarks>
    private async Task ReregisterAsync(TriggerDefinition definition)
    {
        if (_monitor is not { } monitor)
        {
            return;
        }
        try
        {
            await monitor.RemoveAsync(definition.Id);
            await monitor.AddAsync(definition);
        }
        catch (Exception ex) when (ex is ArgumentException or ObjectDisposedException)
        {
            AppendLog("MonitorRowError", DateTimeOffset.Now, ex.Message);
        }
    }

    private async Task UnregisterAsync(string triggerId)
    {
        if (_monitor is not { } monitor)
        {
            return;
        }
        try
        {
            await monitor.RemoveAsync(triggerId);
        }
        catch (Exception ex) when (ex is ArgumentException or ObjectDisposedException)
        {
            AppendLog("MonitorRowError", DateTimeOffset.Now, ex.Message);
        }
    }

    private void OnTriggerFired(object? sender, TriggerFiredEventArgs e) => AppendLog(
        "MonitorRowFired", e.Timestamp.ToLocalTime(), e.TriggerId, e.On, e.OldValue, e.NewValue);

    private void OnResolutionChanged(object? sender, TriggerResolutionChangedEventArgs e) => AppendLog(
        e.IsResolved ? "MonitorRowResolved" : "MonitorRowUnresolved",
        e.Timestamp.ToLocalTime(), e.TriggerId, e.Message);

    // UnhandledException は時刻を運んでこないので、ここだけは自分で読む。
    // 監視の既定の時計は TimeProvider.System なので、上の 2 つと同じ壁時計である
    private void OnMonitorException(Exception exception) =>
        AppendLog("MonitorRowError", DateTimeOffset.Now, exception.Message);

    /// <summary>
    /// 監視ログに 1 行足す。<b>どのスレッドから呼んでもよい。</b>
    /// </summary>
    /// <remarks>
    /// 発火・解決状態・例外は 3 つとも単一のバックグラウンドワーカー上で配られる
    /// (<see cref="TriggerMonitor.TriggerFired"/> の remarks)。UI に触れるのは
    /// <c>TryEnqueue</c> の先だけである。<b>ここを省くのがサンプルの最も写されやすい間違い。</b>
    /// </remarks>
    private void AppendLog(string key, params object?[] args) => DispatcherQueue.TryEnqueue(() =>
    {
        _log.Add(AppStrings.Format(key, args));
        while (_log.Count > MonitorLogLimit)
        {
            _log.RemoveAt(0);
        }
    });
}
