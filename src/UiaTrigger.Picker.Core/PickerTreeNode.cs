using System.Collections.ObjectModel;
using System.ComponentModel;

namespace UiaTrigger.Picker;

/// <summary>One row of the picker's element tree. The root row stands for the process itself.</summary>
/// <remarks>
/// <para>
/// A view binds to this type directly, so it stays free of any UI framework: no
/// <c>Visibility</c>, no dispatcher, no control references. <see cref="CanConfirm"/> is a
/// <see cref="bool"/> for that reason — turning it into whatever a framework needs to hide a
/// button is the view's job.
/// </para>
/// <para>
/// Opening a row means two different things, and they must not be confused:
/// <see cref="IsExpanded"/> set by a user asks the picker to enumerate every child, whereas
/// <see cref="ExpandForDisplay"/> only opens a row the picker has already filled in.
/// </para>
/// </remarks>
public sealed class PickerTreeNode : INotifyPropertyChanged
{
    private bool _hasUnrealizedChildren;
    private bool _isExpanded;
    private bool _expandingForDisplay;

    internal PickerTreeNode(string display, IPickerElement? element)
    {
        Display = display;
        Element = element;
    }

    /// <summary>The text to show for this row.</summary>
    public string Display { get; }

    /// <summary>null はプロセスルート。</summary>
    /// <remarks>
    /// <b>ノードは借りているだけである。</b>持ち主は <see cref="TriggerPickerPresenter"/> で、
    /// ノードが木から外れると掃き出しが解放する。だからノードに <c>Dispose</c> は無い —
    /// 足すと public 型の公開メンバーが増えるうえ、View が持っている遅延処理の途中で
    /// ノードを捨てられるようになってしまう。
    /// 解放後もこのハンドルの値は読める (スナップショットなので) ため、
    /// 差し替え中の古いノードを表示し続けても壊れない。
    /// </remarks>
    internal IPickerElement? Element { get; }

    /// <summary>Child rows, in tree order.</summary>
    public ObservableCollection<PickerTreeNode> Children { get; } = [];

    /// <summary>Whether every child has been enumerated. False while only a chain is on show.</summary>
    public bool ChildrenLoaded { get; set; }

    /// <summary>Whether the row should offer an expander although it has no children yet.</summary>
    public bool HasUnrealizedChildren
    {
        get => _hasUnrealizedChildren;
        set
        {
            if (_hasUnrealizedChildren != value)
            {
                _hasUnrealizedChildren = value;
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(HasUnrealizedChildren)));
            }
        }
    }

    /// <summary>Whether the row is open.</summary>
    /// <remarks>
    /// Views bind this two-way. Setting it to <see langword="true"/> counts as a user opening the
    /// row, so the picker enumerates the children it has not fetched yet. Code that only wants the
    /// row open — because the picker itself just built the subtree — must call
    /// <see cref="ExpandForDisplay"/> instead, or every row of a captured chain fetches its
    /// siblings as a side effect.
    /// </remarks>
    public bool IsExpanded
    {
        get => _isExpanded;
        set
        {
            if (_isExpanded == value)
            {
                return;
            }
            _isExpanded = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsExpanded)));
            if (value && !_expandingForDisplay)
            {
                ExpandedByUser?.Invoke(this);
            }
        }
    }

    /// <summary>Opens the row without asking for its remaining children to be enumerated.</summary>
    public void ExpandForDisplay()
    {
        _expandingForDisplay = true;
        try
        {
            IsExpanded = true;
        }
        finally
        {
            _expandingForDisplay = false;
        }
    }

    /// <summary>Whether the row stands for an element — false only for the process root.</summary>
    /// <remarks>
    /// The confirm button hangs off this. A view that needs a framework-specific value — WinUI's
    /// <c>Visibility</c>, say — converts it there; the conversion is a fact about that framework,
    /// not about the picker.
    /// </remarks>
    public bool CanConfirm => Element is not null;

    /// <summary>ユーザーが展開した (ExpandForDisplay では発生しない)。</summary>
    internal event Action<PickerTreeNode>? ExpandedByUser;

    /// <summary>Raised when a bound property changes.</summary>
    public event PropertyChangedEventHandler? PropertyChanged;
}
