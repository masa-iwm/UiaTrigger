// ピッカーの文字列を .resx + サテライトアセンブリから供給する (docs/DESIGN.md L3 / docs/LOCALIZATION.md §4)。
//
// WinUI は MRT Core (resources.pri) を使うため MrtPickerStrings が別にあるが、
// WPF と WinForms は**仕組みが同一**なのでここを共有する。
// UiaTrigger.Core が Strings.resx でやっていることと同じ形であり、この repo の定石である。
//
// カルチャは ResourceManager に任せる (CurrentUICulture)。明示的に渡さないのは、
// ホストが Thread.CurrentThread.CurrentUICulture を切り替えたときにそのまま追随させるためである。
using System.Resources;

namespace UiaTrigger.Picker;

/// <summary>Supplies the picker's strings from <c>Strings.resx</c> and its satellite assemblies.</summary>
/// <remarks>
/// The route for views that have no MRT Core — Windows Presentation Foundation and Windows Forms.
/// Both are handed the same table, because the two use the very same resource mechanism; keeping a
/// copy each would mean maintaining one set of keys twice.
/// </remarks>
public sealed class ResxPickerStrings : IPickerStrings
{
    private static readonly ResourceManager Resources =
        new("UiaTrigger.Picker.Resources.Strings", typeof(ResxPickerStrings).Assembly);

    /// <summary>The string for <paramref name="key"/>, or the key itself when it cannot be found.</summary>
    /// <remarks>Returning the key is deliberate: a blank label is far harder to notice.</remarks>
    public string GetString(string key)
    {
        // MissingManifestResourceException を通さない。文字列 1 つのためにピッカーごと
        // 落とすのは割に合わず、キー名が画面に出れば原因はすぐ分かる (MrtPickerStrings と同じ判断)
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
}
