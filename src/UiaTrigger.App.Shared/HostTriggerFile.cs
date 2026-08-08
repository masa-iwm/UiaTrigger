// サンプルホスト 3 種が共有するトリガーファイルの読み書き (docs/DESIGN.md §12)。
//
// **答えが UI スタックに依らないものは引き下ろす。**ここに置いているのは「どの例外を
// 拾うか」という 1 つの判断だけで、WinUI / WPF / WinForms のどれでも答えは同じである。
// 3 変種に写すと必ずずれる — 実際、TriggerStore.Load が文書化している JsonException /
// NotSupportedException が 3 つとも catch の顔ぶれから漏れていた。
//
// **漏れると何が起きるか**は変種ごとに違い、しかもどれも「起動しない」形になる:
//   ・WPF      — StartupUri の窓生成中に投げると DispatcherUnhandledException が飲み、
//                窓が 1 つも出ないままメッセージループだけが走り続ける (ShutdownMode 既定)
//   ・WinForms — Application.Run(new MainForm()) の引数評価はループ開始**前**なので
//                Application.ThreadException の網の外で落ちる
//   ・WinUI    — 全捕捉なので表示される (この 1 つだけが正しかった)
// 壊れた / 新しい形式の triggers.json を持つ利用者は、サンプルを一度も起動できない。
using System.Text.Json;
using UiaTrigger.Models;
using UiaTrigger.Persistence;

namespace UiaTrigger.App.Shared;

internal static class HostTriggerFile
{
    /// <summary>
    /// トリガーファイルを読む。失敗は例外ではなく理由の文字列で返す。
    /// </summary>
    /// <remarks>
    /// 拾うのは <see cref="TriggerStore.Load"/> が文書化している例外
    /// (<see cref="JsonException"/> / <see cref="NotSupportedException"/>) と、
    /// ファイル入出力の常として起きるもの。**ここに無い例外は拾わない** —
    /// 想定していない失敗まで飲むと、サンプルが「静かに空のまま起動する」形になる。
    /// </remarks>
    public static bool TryLoad(string path, out IReadOnlyList<TriggerDefinition> triggers, out string? error)
    {
        try
        {
            triggers = TriggerStore.Load(path);
            error = null;
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException
            or InvalidOperationException or JsonException or NotSupportedException)
        {
            triggers = [];
            error = ex.Message;
            return false;
        }
    }

    /// <summary>トリガーファイルを書く。失敗は例外ではなく理由の文字列で返す。</summary>
    public static bool TrySave(string path, IEnumerable<TriggerDefinition> triggers, out string? error)
    {
        try
        {
            TriggerStore.Save(path, triggers);
            error = null;
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException
            or InvalidOperationException or JsonException or NotSupportedException)
        {
            error = ex.Message;
            return false;
        }
    }
}
