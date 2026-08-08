// 検証用コンソールのユーザー向け文字列 (docs/LOCALIZATION.md §3 の分類 2)。
//
// GUI ホストの AppStrings と同じ形 — .resx + ResourceManager + サテライトである。
// TestHost は PublishAot=true なので、**このアセンブリが自前のサテライトを持つ最初の
// AOT 発行物**になる。サテライトが発行後も引けることは CI の aot ジョブが
// 実際の出力で確かめている (ファイルの有無では確かめられない)。
//
// 監視中の発火ログはここを通さない。あれは分類 3 (開発者向け) で英語固定である。
using System.Globalization;
using System.Resources;

namespace UiaTrigger.TestHost;

internal static class TestHostStrings
{
    private static readonly ResourceManager Resources =
        new("UiaTrigger.TestHost.Resources.Strings", typeof(TestHostStrings).Assembly);

    /// <summary>リソース文字列。引けなければキー名をそのまま返す (無言で空にはしない)。</summary>
    public static string Get(string key)
    {
        string? value;
        try
        {
            value = Resources.GetString(key, culture: null);
        }
        catch (MissingManifestResourceException)
        {
            return key;
        }
        return string.IsNullOrEmpty(value) ? key : value;
    }

    /// <summary>リソース文字列を現在の UI カルチャで整形する。表示専用。</summary>
    public static string Format(string key, params object?[] args)
        => string.Format(CultureInfo.CurrentCulture, Get(key), args);
}
