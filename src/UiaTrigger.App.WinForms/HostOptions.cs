// コマンドラインの読み取り (docs/DESIGN.md §12)。
//
// 読むのは 3 つ:
//   --pick-at x,y      ピッカーに「カーソルはここに在る」と思わせる。繰り返せる
//   --culture <name>   表示カルチャを上書きする
//   --triggers <path>  トリガーの保存先を差し替える
//
// 3 ホストは同じ引数を読む。--pick-at を読まないホストはホバー捕捉を起こせず、
// T4 の profile に足す選択肢が閉じたままになる (現状 T4 の profile に WinForms は
// 無いが、引数は揃えてある)。
//
// なぜ --pick-at がホスト側に要るのか — ピッカーの主要な操作はホバー滞留であり、捕捉が
// 起きなければツリーには何も出ない。実際のマウスを動かすのは擬似入力であり、
// このリポジトリのテストでは禁止されている (docs/TESTING.md §4)。
// 差し替えているのは入力**イベント**ではなくカーソルの**取得元**である。
//
// 引数は Environment.GetCommandLineArgs() から読む。Main(string[]) を生やすこともできるが、
// WinUI ホストがそうせざるをえない (エントリーポイントが生成される) 以上、
// 3 ホストで**同じ形**にしておくほうが、写しどうしの差分が意味を持つ。
//
// 3 ホストがこのファイルをそれぞれ持っている理由は、WinUI 版の同じファイルの冒頭に書いた。
using System.Globalization;
using UiaTrigger.Picker;

namespace UiaTrigger.App.WinForms;

internal static class HostOptions
{
    private static int _pickersOpened;

    /// <summary>
    /// <c>--pick-at x,y</c> で指定された固定カーソルの列。**繰り返して指定できる**。
    /// 指定が無ければ空 (= 実際のマウスに追随する、通常の動作)。
    /// </summary>
    public static IReadOnlyList<ICursorSource> Cursors { get; } = ReadCursors(Environment.GetCommandLineArgs());

    /// <summary>
    /// <c>--triggers &lt;path&gt;</c> で指定されたトリガーの保存先。無指定なら null。
    /// </summary>
    /// <remarks>
    /// 既定は <c>%LOCALAPPDATA%</c> の実ファイルである。
    /// **この口が無いと、自動テストが開発機の実ファイルを書き換える。**
    /// </remarks>
    public static string? TriggerFile { get; } = ReadOption(Environment.GetCommandLineArgs(), "--triggers");

    /// <summary>
    /// 次に開くピッカーへ渡すカーソル。<c>--pick-at</c> が無ければ null。
    /// </summary>
    /// <remarks>
    /// <para>
    /// **n 枚目のピッカーが n 番目の座標**を受け取る。足りなければ最後を使い回すので、
    /// <c>--pick-at</c> を 1 つだけ渡す従来の使い方は何枚開いても同じ座標になる。
    /// </para>
    /// <para>
    /// **「n 枚目」はこのメソッドを呼んだ回数である。**「ピッカーで追加」は既に開いていれば
    /// 前面に出すだけなので、2 枚目を開くには「もう 1 つ開く」を押す必要がある。
    /// S1 (2 枚がそれぞれ独立に追従すること) の検出力はここに依存する — 2 枚が同じ座標を
    /// 受け取ると、オーバーレイを static singleton へ戻す退行が「枠が一致する」で素通りする。
    /// </para>
    /// </remarks>
    public static ICursorSource? NextCursor()
    {
        if (Cursors.Count == 0)
        {
            return null;
        }
        int index = _pickersOpened++;
        return Cursors[Math.Min(index, Cursors.Count - 1)];
    }

    /// <summary>
    /// <c>--culture &lt;name&gt;</c> を表示カルチャへ反映する。
    /// </summary>
    /// <remarks>
    /// **ウィンドウを 1 つも作る前に呼ぶこと。**<c>ResxPickerStrings</c> は
    /// <c>culture: null</c> で作られており <c>CurrentUICulture</c> を追うので、これで切り替わる。
    /// </remarks>
    public static void ApplyCulture()
    {
        if (ReadOption(Environment.GetCommandLineArgs(), "--culture") is not { Length: > 0 } name)
        {
            return;
        }

        try
        {
            var culture = new CultureInfo(name);
            CultureInfo.DefaultThreadCurrentCulture = culture;
            CultureInfo.DefaultThreadCurrentUICulture = culture;
            CultureInfo.CurrentCulture = culture;
            CultureInfo.CurrentUICulture = culture;
        }
        catch (CultureNotFoundException)
        {
            Program.Log($"--culture の値を解釈できませんでした: '{name}'");
        }
    }

    /// <summary>
    /// <c>--pick-at</c> を**すべて**読む。値が壊れているものは飛ばし、
    /// **理由をログに残す** — 黙って通常動作に落ちると「捕捉が起きない」だけの症状になり、
    /// 原因が分からなくなる。
    /// </summary>
    internal static IReadOnlyList<ICursorSource> ReadCursors(string[] args)
    {
        ArgumentNullException.ThrowIfNull(args);

        var cursors = new List<ICursorSource>();
        for (int i = 0; i < args.Length; i++)
        {
            if (!string.Equals(args[i], "--pick-at", StringComparison.Ordinal))
            {
                continue;
            }

            string value = i + 1 < args.Length ? args[i + 1] : string.Empty;
            string[] parts = value.Split(',');
            if (parts.Length == 2 &&
                int.TryParse(parts[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out int x) &&
                int.TryParse(parts[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out int y))
            {
                cursors.Add(new FixedCursorSource(x, y));
            }
            else
            {
                Program.Log($"--pick-at の値を解釈できませんでした: '{value}' (期待する形式: x,y)");
            }
        }
        return cursors;
    }

    /// <summary>「<c>--name value</c>」の value を読む。無ければ null。</summary>
    private static string? ReadOption(string[] args, string name)
    {
        int i = Array.IndexOf(args, name);
        return i >= 0 && i + 1 < args.Length && args[i + 1].Length > 0 ? args[i + 1] : null;
    }
}

/// <summary>常に同じスクリーン座標を返すカーソル。<c>--pick-at</c> のためだけに在る。</summary>
internal sealed class FixedCursorSource(int x, int y) : ICursorSource
{
    public bool TryGetPosition(out int cursorX, out int cursorY)
    {
        cursorX = x;
        cursorY = y;
        return true;
    }
}
