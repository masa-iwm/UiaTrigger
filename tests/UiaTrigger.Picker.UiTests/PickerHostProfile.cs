using Xunit;

namespace UiaTrigger.Picker.UiTests;

/// <summary>
/// T4 が駆動するサンプルホストの種別。
///
/// <para>
/// <b>Windows Forms は入っていない。</b> ここで見るのは「行のコントロールが生成されるのを
/// 待つ経路」であり、Windows Forms の View にはそれが無い
/// (<c>TriggerPickerForm.ExpandThenSelect</c> は遅延もリトライも持たない — 仮想化が無く
/// ウィンドウを出さなくても <c>TreeNode</c> が存在するため)。同期の正しさは
/// <c>TreeMirrorTests</c> が T1 で覆っている。機械的に 3 変種へ広げると、
/// WinForms のぶんは<b>二重になるだけ</b>で実行時間と flake だけが増える
/// (docs/DESIGN.md §12)。
/// </para>
/// </summary>
/// <param name="Name">プロファイル名。<c>[Theory]</c> にはこれを渡す。</param>
/// <param name="ProjectDirectory"><c>src/</c> 直下のプロジェクトフォルダー名。</param>
/// <param name="ExecutableName">実行ファイル名。</param>
/// <param name="TreeAutomationId">
/// 要素ツリーの AutomationId。<b>変種で違う</b> — WPF は <c>TreeView</c> 自身
/// (<c>ElementTree</c>) が UIA に出るが、WinUI の <c>TreeView</c> は自身を出さず
/// 内側の <c>TreeViewList</c> (部品名 <c>ListControl</c>) が Tree として現れる (実測)。
/// </param>
/// <param name="RequiredSiblingFile">
/// 実行ファイルの隣に必ず在るべきファイル。WinUI は <c>.pri</c> が隣に無いと起動できない
/// (<c>bin/**/native/</c> は AOT の中間生成物で、これが欠ける —
/// docs/LOCALIZATION.md §2 の実測)。空文字なら検査しない。
/// </param>
/// <param name="LogFileName">
/// ホストが未処理例外を書き出すログ (<c>%TEMP%</c> 配下)。失敗時の診断に読む。
/// </param>
/// <param name="PublishDirectory">
/// リポジトリ直下 <c>publish/</c> の中のフォルダー名。CI の <c>aot</c> ジョブがここへ発行し、
/// S4 が<b>発行レイアウトのほう</b>を起動する (docs/TESTING.md §1)。
/// </param>
internal sealed record PickerHostProfile(
    string Name,
    string ProjectDirectory,
    string ExecutableName,
    string TreeAutomationId,
    string RequiredSiblingFile,
    string LogFileName,
    string PublishDirectory)
{
    public static readonly PickerHostProfile Wpf = new(
        Name: "WPF",
        ProjectDirectory: "UiaTrigger.App.Wpf",
        ExecutableName: "UiaTrigger.App.Wpf.exe",
        TreeAutomationId: "ElementTree",
        RequiredSiblingFile: "",
        LogFileName: "uiatrigger-app-wpf.log",
        PublishDirectory: "AppWpf");

    public static readonly PickerHostProfile WinUI = new(
        Name: "WinUI",
        ProjectDirectory: "UiaTrigger.App.WinUI",
        ExecutableName: "UiaTrigger.App.WinUI.exe",
        TreeAutomationId: "ListControl",
        RequiredSiblingFile: "UiaTrigger.App.WinUI.pri",
        LogFileName: "uiatrigger-app.log",
        PublishDirectory: "App");

    /// <summary>
    /// Windows Forms ホスト。<b>変種を横断する <c>[Theory]</c> には入れない</b>
    /// (<see cref="AllNames"/> を参照)。
    /// </summary>
    /// <remarks>
    /// <b>この変種にしか無い UI</b> 1 つだけのために在る —
    /// 行の中にボタンを置けないので、確定は「選択中の行を確定する」ボタンで行う
    /// (<c>TriggerPickerForm.cs</c>)。その配線は 1 行で、
    /// <c>_tree.SelectedNode?.Tag as PickerTreeNode</c> が null になると
    /// <b>例外を出さずに何もしない</b>形をしている。
    /// </remarks>
    public static readonly PickerHostProfile WinForms = new(
        Name: "WinForms",
        ProjectDirectory: "UiaTrigger.App.WinForms",
        ExecutableName: "UiaTrigger.App.WinForms.exe",
        TreeAutomationId: "ElementTree",
        RequiredSiblingFile: "",
        LogFileName: "uiatrigger-app-winforms.log",
        // 発行していない。S4 (発行レイアウト) は WinForms を対象にしていない —
        // resx 経路は WPF が代表して見ており、同じことを 2 回見ることになる
        PublishDirectory: "AppWinForms");

    /// <summary>
    /// <c>[Theory]</c> のデータ源。名前 (文字列) を渡すのは、レコードそのものが
    /// xunit のシリアライズ対象にならず、失敗したテストを個別に再実行できなくなるため
    /// (<c>TargetProfile</c> と同じ理由)。
    /// </summary>
    /// <remarks>
    /// <b>Windows Forms は入っていない。</b>入れると、ツリーの同期のように
    /// <c>TreeMirrorTests</c> がウィンドウ無しで丸ごと覆っているものを二重に見ることになり、
    /// 実行時間と flake だけが増える (docs/DESIGN.md §12)。
    /// WinForms は<b>固有の項目のためだけ</b>に <see cref="WinForms"/> を名指しで使う。
    /// </remarks>
    public static TheoryData<string> AllNames => [Wpf.Name, WinUI.Name];

    public static PickerHostProfile ByName(string name) => name switch
    {
        "WPF" => Wpf,
        "WinUI" => WinUI,
        "WinForms" => WinForms,
        _ => throw new ArgumentOutOfRangeException(nameof(name), name, "unknown picker host profile"),
    };

    public override string ToString() => Name;
}
