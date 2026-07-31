using Xunit;

namespace UiaTrigger.RealUia.Tests;

/// <summary>
/// 実 UIA テストの対象アプリの種別。
///
/// <para>
/// WinForms は <b>MSAA ブリッジ経由</b>のプロバイダーであり、ネイティブ UIA プロバイダー
/// (WPF / Chromium) とは挙動が違う (docs/DESIGN.md §6)。たとえば WinForms では削除が
/// <c>StructureChanged</c> として届かない。片方の対象アプリだけでは、B3 (経路購読) の
/// 実装が正しいかどうかを片側のプロバイダーでしか確かめられない — だから 2 種類持つ。
/// </para>
///
/// <para>
/// <b>すべてのテストを両プロファイルに広げてはいない。</b> 34 → 68 は実行時間と flake を
/// 倍にするだけで、プロバイダー差が論点でないテスト (経路解決アルゴリズム・Search 方式・
/// セッション共有) には意味が無い。広げたのは<b>プロバイダーの挙動そのものが論点</b>のものだけである。
/// </para>
/// </summary>
internal sealed record TargetProfile(
    string Name,
    string ProjectDirectory,
    string ExecutableName,
    string ProcessName,
    string WindowClassNamePrefix,
    bool SupportsRoleSwitch,
    bool IsNativeProvider)
{
    /// <summary>
    /// WinForms 版。<b>残してある</b> — ウィンドウクラス名に起動ごとに変わりうる token
    /// (<c>WindowsForms10.Window.8.app.0.141b42a_r6_ad1</c>) が入るため、A4 の回帰ケースそのものになる。
    /// </summary>
    public static readonly TargetProfile WinForms = new(
        Name: "WinForms",
        ProjectDirectory: "UiaTrigger.TestTarget",
        ExecutableName: "UiaTrigger.TestTarget.exe",
        ProcessName: "UiaTrigger.TestTarget.exe",
        WindowClassNamePrefix: "WindowsForms10.",
        SupportsRoleSwitch: true,
        IsNativeProvider: false);

    /// <summary>
    /// WPF 版 = ネイティブ UIA プロバイダー。
    /// <see cref="SupportsRoleSwitch"/> の根拠は実測 —
    /// ControlType は要素を作り直さずに変わる (docs/DESIGN.md §6)。
    /// </summary>
    public static readonly TargetProfile Wpf = new(
        Name: "WPF",
        ProjectDirectory: "UiaTrigger.TestTarget.Wpf",
        ExecutableName: "UiaTrigger.TestTarget.Wpf.exe",
        ProcessName: "UiaTrigger.TestTarget.Wpf.exe",
        // WPF の HWND クラス名は HwndWrapper[<exe>;;<起動ごとの GUID>]
        WindowClassNamePrefix: "HwndWrapper[",
        SupportsRoleSwitch: true,
        IsNativeProvider: true);

    /// <summary>
    /// <c>[Theory]</c> のデータ源。名前 (文字列) を渡すのは、プロファイルそのものが
    /// xunit のシリアライズ対象にならず、失敗したテストを個別に再実行できなくなるため。
    /// </summary>
    public static TheoryData<string> AllNames => [WinForms.Name, Wpf.Name];

    public static TargetProfile ByName(string name) => name switch
    {
        "WinForms" => WinForms,
        "WPF" => Wpf,
        _ => throw new ArgumentOutOfRangeException(nameof(name), name, "unknown target profile"),
    };

    public override string ToString() => Name;
}
