// 既定の保存先だけホスト側で決める。読み書きは UiaTrigger.Core の TriggerStore が持つ
// (docs/DESIGN.md C8)。シリアライザ定義をホスト側へ複製しない。
namespace UiaTrigger.TestHost;

internal static class TriggerFilePath
{
    public static string Default => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "UiaTrigger", "triggers.json");
}
