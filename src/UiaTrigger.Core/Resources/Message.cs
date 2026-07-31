using System.Globalization;

namespace UiaTrigger.Resources;

/// <summary>
/// ユーザー向けメッセージの書式化 (docs/LOCALIZATION.md §3)。
///
/// 比較・永続化に使う文字列にこれを使ってはならない。そちらは常に
/// InvariantCulture / Ordinal で扱う (L4)。
/// </summary>
internal static class Message
{
    /// <summary>リソース文字列を現在の UI カルチャで整形する。</summary>
    public static string Format(string format, params object?[] args)
        => string.Format(CultureInfo.CurrentCulture, format, args);
}
