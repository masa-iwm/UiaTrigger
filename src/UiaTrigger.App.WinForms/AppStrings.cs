// サンプルホスト (Windows Forms 版) のユーザー向け文字列 (docs/DESIGN.md L3)。
// 仕組みは WPF 版と同一 (.resx + ResourceManager + サテライト)。
// 3 ホストのキー集合と文言が一致していることは HostStringTests が見ている。
using System.Globalization;
using System.Resources;

namespace UiaTrigger.App.WinForms;

internal static class AppStrings
{
    private static readonly ResourceManager Resources =
        new("UiaTrigger.App.WinForms.Resources.Strings", typeof(AppStrings).Assembly);

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
