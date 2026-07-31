// コマンドラインの読み取り (docs/DESIGN.md §12)。
//
// 読むのは 3 つ:
//   --pick-at x,y      ピッカーに「カーソルはここに在る」と思わせる。繰り返せる
//   --culture <name>   表示カルチャを上書きする
//   --triggers <path>  トリガーの保存先を差し替える
//
// なぜ --pick-at がホスト側に要るのか — ピッカーの主要な操作はホバー滞留であり、捕捉が
// 起きなければツリーには何も出ない。T4 (tests/UiaTrigger.Picker.UiTests) が実体化待ちの
// 経路を確かめるには、まず捕捉を起こす必要がある。実際のマウスを動かすのは擬似入力であり、
// このリポジトリのテストでは禁止されている (docs/TESTING.md §4)。
//
// 差し替えているのは入力<b>イベント</b>ではなくカーソルの<b>取得元</b>である。
// 入力経路そのものは検証対象ではない (滞留の算術は T1 が見ている) ので、
// docs/TESTING.md §4 の教訓に反しない。
//
// 引数は Environment.GetCommandLineArgs() から読む。WinUI3 のエントリーポイントは
// 生成されており、そこへ手を入れずに済ませるためである (他の 2 ホストも同じ形にしてある)。
//
// ■ 3 ホストがこのファイルをそれぞれ持っていることについて
//
// リンク共有 (<Compile Include="..\Shared\HostOptions.cs" />) にはしない。
// ソースのリンク共有は、WinUI3 / x64 でプロジェクト参照ができない場合の回避としてだけ
// 許している (実例: tests/UiaTrigger.Picker.UiTests / UiaTrigger.Input.Tests / UiaTrigger.RealUia.Tests の
// csproj が RepoPaths.cs 等をリンク共有している) — できるなら普通の参照にする、
// という判断である。ここで使うと、その回避を回避でない場所で復活させることになる。
//
// 共有するとしても、この 3 つは同一にならない: 名前空間が違い、ログの出口が違い
// (App.Log / Program.Log)、WinUI だけが MRT の言語上書きを持つ。
// 共有版は Action<string> の注入と Initialize() の呼び忘れを新しく作る。
// ホスト 3 つは元から TriggerFilePath.cs / AppStrings.cs / MainWindow の大半を
// 意図的に写している — 「同じライブラリが 3 つの UI スタックで動く」ことを見せるのが
// このディレクトリの目的だからである。ここだけ共有しても、その形は揃わない。
//
// → <b>4 つ目のオプションか 4 つ目のホストが要るときは、共有プロジェクトへ移すこと。</b>
//   ソースのリンク共有へは戻さない。
using System.Globalization;
using Microsoft.Windows.Globalization;
using UiaTrigger.Picker;

namespace UiaTrigger.App.WinUI;

internal static class HostOptions
{
    private static int _pickersOpened;

    /// <summary>
    /// <c>--pick-at x,y</c> で指定された固定カーソルの列。<b>繰り返して指定できる</b>。
    /// 指定が無ければ空 (= 実際のマウスに追随する、通常の動作)。
    /// </summary>
    public static IReadOnlyList<ICursorSource> Cursors { get; } = ReadCursors(Environment.GetCommandLineArgs());

    /// <summary>
    /// <c>--triggers &lt;path&gt;</c> で指定されたトリガーの保存先。無指定なら null。
    /// </summary>
    /// <remarks>
    /// 既定は <c>%LOCALAPPDATA%</c> の実ファイルである。
    /// <b>この口が無いと、自動テストが開発機の実ファイルを書き換える。</b>
    /// </remarks>
    public static string? TriggerFile { get; } = ReadOption(Environment.GetCommandLineArgs(), "--triggers");

    /// <summary>
    /// 次に開くピッカーへ渡すカーソル。<c>--pick-at</c> が無ければ null。
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>n 枚目のピッカーが n 番目の座標</b>を受け取る。足りなければ最後を使い回すので、
    /// <c>--pick-at</c> を 1 つだけ渡す従来の使い方は何枚開いても同じ座標になる。
    /// </para>
    /// <para>
    /// <b>「n 枚目」はこのメソッドを呼んだ回数である。</b>「ピッカーで追加」は既に開いていれば
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
    /// <para>
    /// <b>ウィンドウを 1 つも作る前に呼ぶこと。</b>WPF / WinForms の
    /// <c>ResxPickerStrings</c> は <c>CurrentUICulture</c> を追うので、これで切り替わる。
    /// </para>
    /// <para>
    /// <b>WinUI の MRT はこれだけでは切り替わらない</b>ので、
    /// <c>PrimaryLanguageOverride</c> も併せて立てる。<c>MrtPickerStrings.Loader</c> は
    /// <c>static readonly Lazy</c> であり、<b>一度でも文字列を読んだら決着している</b> —
    /// 上書きは <c>App</c> のコンストラクター (どのウィンドウを作る前) で行う必要がある。
    /// 実測の結果は docs/DESIGN.md §12 にある。
    /// </para>
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
            App.Log($"--culture の値を解釈できませんでした: '{name}'");
            return;
        }

        try
        {
            ApplicationLanguages.PrimaryLanguageOverride = name;
        }
        catch (Exception ex)
        {
            // アンパッケージのホストで効くかは環境依存である。効かなくても
            // resx 側 (メインウィンドウ) は切り替わるので、ここで落とす価値は無い
            App.Log($"PrimaryLanguageOverride を設定できませんでした: {ex}");
        }
    }

    /// <summary>
    /// <c>--pick-at</c> を<b>すべて</b>読む。値が壊れているものは飛ばし、
    /// <b>理由をログに残す</b> — 黙って通常動作に落ちると「捕捉が起きない」だけの症状になり、
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
                App.Log($"--pick-at の値を解釈できませんでした: '{value}' (期待する形式: x,y)");
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
