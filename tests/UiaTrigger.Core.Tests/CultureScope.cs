// テスト中だけカルチャを差し替える (docs/LOCALIZATION.md §3)。
//
// **退避と復元を手書きしないこと。**CurrentCulture を CurrentUICulture の控えで戻す
// 取り違えが実際に複数箇所に居た。2 つは別の値なので、取り違えると**そのテストは通り、
// 同じスレッドで後から走る別のテストが落ちる** — 落ちる顔ぶれが並び順で変わるので、
// 原因に辿り着くまでが長い。
using System.Globalization;

namespace UiaTrigger.Tests;

internal readonly struct CultureScope : IDisposable
{
    private readonly CultureInfo _culture;
    private readonly CultureInfo _uiCulture;

    private CultureScope(CultureInfo culture, CultureInfo uiCulture)
    {
        _culture = culture;
        _uiCulture = uiCulture;
    }

    /// <summary>表示・書式の両方を <paramref name="name"/> にする。</summary>
    public static CultureScope Enter(string name) => Enter(new CultureInfo(name));

    /// <inheritdoc cref="Enter(string)"/>
    public static CultureScope Enter(CultureInfo culture)
    {
        ArgumentNullException.ThrowIfNull(culture);

        var scope = new CultureScope(CultureInfo.CurrentCulture, CultureInfo.CurrentUICulture);
        CultureInfo.CurrentCulture = culture;
        CultureInfo.CurrentUICulture = culture;
        return scope;
    }

    public void Dispose()
    {
        CultureInfo.CurrentCulture = _culture;
        CultureInfo.CurrentUICulture = _uiCulture;
    }
}
