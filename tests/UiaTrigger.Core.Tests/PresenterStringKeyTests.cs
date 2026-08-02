// プレゼンターが実行時に引くキーはドットを含まないこと (docs/LOCALIZATION.md §4)。
//
// **これは実機でしか出ない壊れ方を、ソースの形で塞ぐためのテストである。**
// ドットを含むキーはコントロールのキーであり、WinUI3 の resources.pri では
// 「CombineTriggersButton/Content」という経路として格納される。x:Uid はそれを解決するが、
// ResourceLoader.GetString("CombineTriggersButton.Content") は解決できず投げる。
// MrtPickerStrings はそれを握り潰して**キー文字列そのもの**を返すので、
// 画面のボタンに "CombineTriggersButton.Content" と出る (T6 で実際に出た)。
//
// resx 経路 (WPF / Windows Forms) はドットの有無に関係なく引けてしまうため、
// T1 のふつうのテストでは緑のまま通る。ここでソースの形として縛るしかない。
using System.Text.RegularExpressions;
using UiaTrigger.Picker;
using Xunit;

namespace UiaTrigger.Tests;

public sealed class PresenterStringKeyTests
{
    /// <summary>プレゼンターのソース (実行時に文字列を引く側)。</summary>
    private static readonly string[] PresenterSources =
    [
        Path.Combine("src", "UiaTrigger.Picker.Core", "TriggerListEditorPresenter.cs"),
        Path.Combine("src", "UiaTrigger.Picker.Core", "TriggerPickerPresenter.cs"),
    ];

    /// <summary><c>GetString(XxxStringKeys.Name)</c> / <c>Format(XxxStringKeys.Name</c> を拾う。</summary>
    private static readonly Regex Request = new(
        @"(?:GetString|Format)\(\s*(EditorStringKeys|PickerStringKeys)\.(\w+)",
        RegexOptions.Compiled);

    [Fact]
    public void NoPresenterAsksForAKeyThatOnlyAUidCanResolve()
    {
        var offenders = new List<string>();
        int found = 0;

        foreach (string relative in PresenterSources)
        {
            string path = RepoPaths.Combine(relative);
            Assert.True(File.Exists(path), $"{relative} が見つかりません。");

            foreach (Match match in Request.Matches(File.ReadAllText(path)))
            {
                found++;
                string table = match.Groups[1].Value;
                string constant = match.Groups[2].Value;
                string key = ValueOf(table, constant);
                if (key.Contains('.', StringComparison.Ordinal))
                {
                    offenders.Add($"{relative}: {table}.{constant} = \"{key}\"");
                }
            }
        }

        // 正規表現が何も拾わなくなったら、この検査は黙って空になる
        Assert.True(found > 10, $"要求箇所を {found} 件しか拾えませんでした。正規表現が古くなっています。");
        Assert.True(
            offenders.Count == 0,
            "プレゼンターがドット付きのキーを引いています。WinUI では解決できず、画面にキー名が出ます:\n"
                + string.Join("\n", offenders));
    }

    /// <summary>キー表の定数の値を名前から引く。</summary>
    private static string ValueOf(string table, string constant)
    {
        Type type = table switch
        {
            nameof(EditorStringKeys) => typeof(EditorStringKeys),
            nameof(PickerStringKeys) => typeof(PickerStringKeys),
            _ => throw new InvalidOperationException($"未知のキー表: {table}"),
        };
        System.Reflection.FieldInfo field = type.GetField(constant)
            ?? throw new InvalidOperationException($"{table}.{constant} が見つかりません。");
        return (string)field.GetValue(null)!;
    }
}
