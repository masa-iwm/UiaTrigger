// ピッカーのユーザー向け文字列 (docs/DESIGN.md L3 / docs/LOCALIZATION.md §3)。
//
// XAML 側は x:Uid で解決されるが、コードで組み立てる文字列 (件数・エラー理由など) は
// プレゼンターが IPickerStrings 経由で引き、その WinUI3 での実装がここである。
// Core の Strings.resx と役割は同じで、供給元だけが違う:
//   Core         … .resx + ResourceManager + サテライトアセンブリ
//   Picker.WinUI … .resw + MRT Core (resources.pri)。WinUI3 のリソース経路に合わせる必要がある
//
// クラスライブラリのリソースは、アプリの resources.pri の中で
// 「<ライブラリのアセンブリ名>/Resources」というサブツリーに入る。既定の ResourceLoader()
// はアプリ自身の "Resources" を見るため、ここを指定し忘れると Picker の文字列だけが
// 一切引けなくなる (例外にはならず、キー名がそのまま画面に出る)。
//
// アセンブリ名に追随しなければならないことに注意 — アセンブリをリネームしたときに
// ここを直し忘れても<b>ビルドは通り、実行時に画面がキー名だらけになる</b>だけである。
// XamlLocalizationTests.TheResourceMapNamesTheAssemblyItLivesIn がここを固定している。
using Microsoft.Windows.ApplicationModel.Resources;

namespace UiaTrigger.Picker.WinUI;

/// <summary>Supplies the picker's strings from this assembly's <c>.resw</c> through MRT Core.</summary>
public sealed class MrtPickerStrings : IPickerStrings
{
    private const string ResourceMap = "UiaTrigger.Picker.WinUI/Resources";

    private static readonly Lazy<ResourceLoader?> Loader = new(() =>
    {
        try
        {
            return new ResourceLoader(ResourceLoader.GetDefaultResourceFilePath(), ResourceMap);
        }
        catch (Exception)
        {
            // resources.pri が見つからない構成 (単体でロードされた場合など)。
            // 文字列が引けないことでピッカーごと落とすのは割に合わない
            return null;
        }
    });

    /// <summary>The string for <paramref name="key"/>, or the key itself when it cannot be found.</summary>
    /// <remarks>Returning the key is deliberate: a blank label is far harder to notice.</remarks>
    public string GetString(string key)
    {
        string? value = Loader.Value?.GetString(key);
        return string.IsNullOrEmpty(value) ? key : value;
    }
}
