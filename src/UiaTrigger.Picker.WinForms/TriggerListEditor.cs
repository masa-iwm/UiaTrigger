// PropertyGrid からトリガ一覧を編集するための UITypeEditor (docs/DESIGN.md §4)。
//
// Windows Forms だけに置く。UITypeEditor は System.Drawing.Design の仕組みで、
// WPF / WinUI には相当物が無い — 継ぎ目の判定規準 (docs/DESIGN.md §12) どおり、
// 「そのフレームワークでしかそうならないもの」はその View の側に置く。
using System.ComponentModel;
using System.Drawing.Design;
using System.Windows.Forms.Design;
using UiaTrigger.Models;

namespace UiaTrigger.Picker.WinForms;

/// <summary>Edits a list of triggers from a <see cref="PropertyGrid"/>.</summary>
/// <remarks>
/// <para>
/// Put it on a property holding triggers and the grid grows a button that opens
/// <see cref="TriggerListEditorForm"/>:
/// </para>
/// <code>
/// [Editor(typeof(TriggerListEditor), typeof(UITypeEditor))]
/// public List&lt;TriggerDefinition&gt; Triggers { get; set; } = [];
/// </code>
/// <para>
/// Intended for a <see cref="PropertyGrid"/> shown by a running application, which is where a user
/// edits triggers. It is not meant for the Visual Studio designer: the designer runs editors in its
/// own process, and the picker records elements from the live screen, so what it would record there
/// is the designer.
/// </para>
/// </remarks>
public sealed class TriggerListEditor : UITypeEditor
{
    /// <summary>Reports that this editor opens a dialog.</summary>
    /// <param name="context">The property being edited. Not used.</param>
    /// <returns><see cref="UITypeEditorEditStyle.Modal"/>, always.</returns>
    public override UITypeEditorEditStyle GetEditStyle(ITypeDescriptorContext? context)
        => UITypeEditorEditStyle.Modal;

    /// <summary>Opens the editor on <paramref name="value"/> and returns the edited list.</summary>
    /// <param name="context">The property being edited. Not used.</param>
    /// <param name="provider">
    /// Where the grid's <see cref="IWindowsFormsEditorService"/> comes from, so the dialog is owned
    /// by the grid.
    /// </param>
    /// <param name="value">
    /// The triggers to edit: any <see cref="IEnumerable{T}"/> of <see cref="TriggerDefinition"/>.
    /// Null counts as none.
    /// </param>
    /// <returns>
    /// A new <see cref="List{T}"/> of the edited triggers, or <paramref name="value"/> unchanged when
    /// the user cancelled — which is what tells the grid not to set the property.
    /// </returns>
    /// <remarks>
    /// The list handed in is never modified: cancelling leaves the property exactly as it was, even
    /// after triggers were recorded and removed inside the dialog.
    /// </remarks>
    public override object? EditValue(
        ITypeDescriptorContext? context, IServiceProvider provider, object? value)
    {
        ArgumentNullException.ThrowIfNull(provider);

        IReadOnlyList<TriggerDefinition> current = value switch
        {
            IEnumerable<TriggerDefinition> triggers => [.. triggers],
            _ => [],
        };

        var service = provider.GetService(typeof(IWindowsFormsEditorService)) as IWindowsFormsEditorService;
        using var form = new TriggerListEditorForm(
            new ResxPickerStrings(), current, createPresenter: null, createPicker: null);

        if (service is not null)
        {
            // グリッドに出してもらう。所有者付けと入れ子メッセージループはグリッドの担当である
            _ = service.ShowDialog(form);
        }
        else
        {
            // サービスが取れない場（PropertyGrid の外から直に呼ばれた場合）でも動くようにする。
            // 黙って編集できないより、所有者の無いダイアログのほうがましである
            _ = form.ShowDialog();
        }

        // キャンセルは**元の value をそのまま返す**。UITypeEditor の規約であり、
        // 新しいリストを返すと「変更なし」がプロパティの差し替えとして扱われる
        return form.Result is { } edited ? new List<TriggerDefinition>(edited) : value;
    }
}
