// 既定の保存先だけホスト側で決める。読み書きは UiaTrigger.Core の TriggerStore が持つ
// (docs/DESIGN.md C8)。
//
// 3 ホストとも同じファイルを見る。片方で追加したトリガーがもう片方で読めることが
// 「ライブラリとして同じものを提供できている」ことの一番簡単な確認になる。
using System.IO;

namespace UiaTrigger.App.Wpf;

internal static class TriggerFilePath
{
    public static string Default => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "UiaTrigger", "triggers.json");
}
