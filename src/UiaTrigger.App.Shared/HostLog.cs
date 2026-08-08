// サンプルホスト 3 種が共有する診断ログの書き出し (docs/DESIGN.md §12 / L7)。
//
// 引き下ろす理由は HostTriggerFile と同じで、答えが UI スタックに依らないためである。
// ここが握っている判断は 2 つだけ:
//
//   1. **時刻は invariant で書く** (docs/LOCALIZATION.md §3 の分類 3)。ログは共有して
//      grep するものであり、実行環境の言語で桁や区切りが変わってはいけない。
//      素の文字列補間は CurrentCulture を使うので、ja-JP でも見た目は同じだが、
//      アラビア数字を使わないカルチャでは別の綴りになる。TestHost だけが
//      string.Create(InvariantCulture, ...) で正しく書いていて、GUI ホスト 3 つが漏れていた
//   2. 書き出しの失敗は握る。ログが書けないことでサンプルが落ちるほうがおかしい
using System.Globalization;

namespace UiaTrigger.App.Shared;

internal static class HostLog
{
    /// <summary>1 行追記する。時刻は invariant の <c>HH:mm:ss.fff</c>。</summary>
    public static void Append(string path, string message)
    {
        try
        {
            File.AppendAllText(
                path,
                string.Create(CultureInfo.InvariantCulture, $"{DateTime.Now:HH:mm:ss.fff} {message}") +
                    Environment.NewLine);
        }
        catch (IOException)
        {
            // ログが書けないだけでサンプルを止めない
        }
        catch (UnauthorizedAccessException)
        {
        }
    }
}
