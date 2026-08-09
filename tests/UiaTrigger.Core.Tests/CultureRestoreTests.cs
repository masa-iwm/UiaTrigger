// カルチャの退避と復元で型を取り違えていないこと。
//
// **これは「そのテストは通り、あとから走る別のテストが落ちる」形の壊れ方である。**
// CurrentCulture を CurrentUICulture の控えで戻すと、同じスレッドを使う後続の
// テストが汚染されたカルチャで走る — 落ちる顔ぶれは実行順で変わるので、
// 原因に辿り着くまでが長い。実際に 3 か所で起きていた。
using System.Text.RegularExpressions;
using Xunit;

namespace UiaTrigger.Tests;

public sealed class CultureRestoreTests
{
    private static readonly Regex Save = new(
        @"CultureInfo (?<var>\w+) = CultureInfo\.(?<from>Current\w*Culture);",
        RegexOptions.CultureInvariant,
        TimeSpan.FromSeconds(1));

    private static readonly Regex Restore = new(
        @"CultureInfo\.(?<to>Current\w*Culture) = (?<var>\w+);",
        RegexOptions.CultureInvariant,
        TimeSpan.FromSeconds(1));

    /// <summary>
    /// 手書きの退避・復元で、退避元と復元先が同じ種類であること。
    /// </summary>
    /// <remarks>
    /// <see cref="CultureScope"/> を使えばこの間違いは起こしようがないので、そちらが本筋である。
    /// この検査が要るのは、**使わずに書ける**からであり、書いた本人には正しく見えるからである。
    /// </remarks>
    [Fact]
    public void EveryHandWrittenCultureRestoreMatchesWhatItSaved()
    {
        string[] files = [.. Directory.EnumerateFiles(
                RepoPaths.Combine("tests"), "*.cs", SearchOption.AllDirectories)
            .Where(f => !f.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.Ordinal)
                     && !f.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}", StringComparison.Ordinal))];

        // **0 件で緑にしない。**走査先が動けば、この検査は何も見ないまま通る
        Assert.True(files.Length > 20, $"走査できた .cs が {files.Length} 件しかありません。");

        var offenders = new List<string>();
        int pairs = 0;
        foreach (string file in files)
        {
            var saved = new Dictionary<string, string>(StringComparer.Ordinal);
            string[] lines = File.ReadAllLines(file);
            for (int i = 0; i < lines.Length; i++)
            {
                if (Save.Match(lines[i]) is { Success: true } save)
                {
                    saved[save.Groups["var"].Value] = save.Groups["from"].Value;
                }
                if (Restore.Match(lines[i]) is { Success: true } restore &&
                    saved.TryGetValue(restore.Groups["var"].Value, out string? from))
                {
                    pairs++;
                    string to = restore.Groups["to"].Value;
                    if (!string.Equals(from, to, StringComparison.Ordinal))
                    {
                        offenders.Add(
                            $"{Path.GetFileName(file)}:{i + 1}: {to} を {from} の控えで戻しています");
                    }
                }
            }
        }

        // **陽性対照。**組を 1 つも見つけられていないなら、探し方のほうが壊れている
        Assert.True(pairs > 5, $"退避と復元の組が {pairs} 件しか見つかりません。");

        Assert.True(
            offenders.Count == 0,
            "カルチャの復元先が退避元と食い違っています。CultureScope を使ってください:" +
            Environment.NewLine + string.Join(Environment.NewLine, offenders));
    }
}
