// サンプルホストのユーザー向け文字列 (docs/DESIGN.md L3)。
//
// App は実行可能プロジェクトなので、リソースは自分の resources.pri の既定サブツリー
// ("Resources") に入る。既定の ResourceLoader() がそのまま使える点が
// クラスライブラリ (UiaTrigger.Picker.PickerStrings) との違いである。
using System.Globalization;
using Microsoft.Windows.ApplicationModel.Resources;

namespace UiaTrigger.App.WinUI;

internal static class AppStrings
{
    private static readonly Lazy<ResourceLoader?> Loader = new(() =>
    {
        try
        {
            return new ResourceLoader();
        }
        catch (Exception)
        {
            return null;
        }
    });

    /// <summary>リソース文字列。引けなければキー名をそのまま返す (無言で空にはしない)。</summary>
    public static string Get(string key)
    {
        string? value = Loader.Value?.GetString(key);
        return string.IsNullOrEmpty(value) ? key : value;
    }

    /// <summary>リソース文字列を現在の UI カルチャで整形する。表示専用。</summary>
    public static string Format(string key, params object?[] args)
        => string.Format(CultureInfo.CurrentCulture, Get(key), args);
}
