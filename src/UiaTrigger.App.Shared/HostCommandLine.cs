// コマンドラインの解析そのもの (docs/DESIGN.md §12)。
//
// **状態を持たない。**引数とログの出口を渡すと、解析結果を 1 つ返すだけである。
// ホスト側の入口 (HostOptions) が持つ static な置き場とは分けてある — 分けておくと
// T1 が「壊れた値を渡したらログに残るか」をプロセスの状態に触らずに確かめられる。
using System.Globalization;
using UiaTrigger.Picker;

namespace UiaTrigger.App.Shared;

/// <summary>ホストが受け取るオプションの解析結果。</summary>
internal sealed class HostCommandLine
{
    private HostCommandLine(IReadOnlyList<ICursorSource> cursors, string? triggerFile, string? culture)
    {
        Cursors = cursors;
        TriggerFile = triggerFile;
        Culture = culture;
    }

    /// <summary>
    /// <c>--pick-at x,y</c> で指定された固定カーソルの列。**繰り返して指定できる**。
    /// 指定が無ければ空 (= 実際のマウスに追随する、通常の動作)。
    /// </summary>
    public IReadOnlyList<ICursorSource> Cursors { get; }

    /// <summary>
    /// <c>--triggers &lt;path&gt;</c> で指定されたトリガーの保存先。無指定なら null。
    /// </summary>
    /// <remarks>
    /// 既定は <c>%LOCALAPPDATA%</c> の実ファイルである。
    /// **この口が無いと、自動テストが開発機の実ファイルを書き換える。**
    /// </remarks>
    public string? TriggerFile { get; }

    /// <summary><c>--culture &lt;name&gt;</c> の値。無指定なら null。</summary>
    public string? Culture { get; }

    /// <summary>
    /// 引数を解析する。**解釈できない値は飛ばし、理由を <paramref name="log"/> に残す。**
    /// </summary>
    /// <remarks>
    /// 黙って通常動作に落ちてはいけない。<c>--pick-at</c> が壊れていると
    /// 「捕捉が起きない」だけの症状になり、原因が読めなくなる。
    /// ログの出口を**引数で受ける**のは、書き忘れようがない形にするためである。
    /// </remarks>
    public static HostCommandLine Parse(string[] args, Action<string> log)
    {
        ArgumentNullException.ThrowIfNull(args);
        ArgumentNullException.ThrowIfNull(log);

        return new HostCommandLine(
            ReadCursors(args, log),
            ReadOption(args, "--triggers"),
            ReadOption(args, "--culture"));
    }

    /// <summary><c>--pick-at</c> を**すべて**読む。</summary>
    private static List<ICursorSource> ReadCursors(string[] args, Action<string> log)
    {
        var cursors = new List<ICursorSource>();
        for (int i = 0; i < args.Length; i++)
        {
            if (!string.Equals(args[i], "--pick-at", StringComparison.Ordinal))
            {
                continue;
            }

            string value = i + 1 < args.Length ? args[i + 1] : string.Empty;
            if (ReadIntegers(value, 2) is { } parts)
            {
                cursors.Add(new FixedCursorSource(parts[0], parts[1]));
            }
            else
            {
                log($"--pick-at の値を解釈できませんでした: '{value}' (期待する形式: x,y)");
            }
        }
        return cursors;
    }

    /// <summary>「<c>a,b,…</c>」を <paramref name="count"/> 個の整数として読む。読めなければ null。</summary>
    /// <remarks>
    /// 解釈は必ず <see cref="CultureInfo.InvariantCulture"/> で行う。表示ではなく
    /// **引数の解釈**なので、機械の地域設定で意味が変わってはならない
    /// (docs/LOCALIZATION.md §3 の 2 番)。
    /// </remarks>
    private static int[]? ReadIntegers(string value, int count)
    {
        string[] parts = value.Split(',');
        if (parts.Length != count)
        {
            return null;
        }

        var numbers = new int[count];
        for (int i = 0; i < count; i++)
        {
            if (!int.TryParse(parts[i], NumberStyles.Integer, CultureInfo.InvariantCulture, out numbers[i]))
            {
                return null;
            }
        }
        return numbers;
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
