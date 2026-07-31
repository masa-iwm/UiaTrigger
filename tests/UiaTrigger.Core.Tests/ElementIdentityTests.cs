using System.Runtime.InteropServices;
using UiaTrigger.Resolution;
using Xunit;

namespace UiaTrigger.Tests;

/// <summary>
/// 解決済み要素の同一性再検証 (docs/DESIGN.md A8)。
///
/// ProcessId を読んで例外が出ないことを見るだけでは、
/// 「生きているが別物」(仮想化リストのプロバイダー再利用など) を見抜けない。
/// TriggerMonitor への配線そのものは実 UIA が要るため T3 で担保する。
/// ここでは判定に使う組の意味を固定する。
/// </summary>
public sealed class ElementIdentityTests
{
    private static FakeElement Element(
        int processId = 42,
        int controlType = 50000,
        string automationId = "okButton",
        string className = "Button",
        string name = "OK") =>
        new()
        {
            ProcessId = processId,
            ControlType = controlType,
            AutomationId = automationId,
            ClassName = className,
            Name = name,
        };

    /// <summary>
    /// Name は含めない。監視対象として最もよく使われる可変プロパティであり、含めると
    /// 「値が変わっただけ」で再解決が走ってしまう。
    /// </summary>
    [Fact]
    public void Identity_IgnoresTheName()
    {
        Assert.Equal(
            ElementIdentity.Of(Element(name: "OK")),
            ElementIdentity.Of(Element(name: "変更後")));
    }

    [Theory]
    [InlineData(43, 50000, "okButton", "Button")]
    [InlineData(42, 50004, "okButton", "Button")]
    [InlineData(42, 50000, "cancelButton", "Button")]
    [InlineData(42, 50000, "okButton", "Edit")]
    public void Identity_ChangesWithEveryStableAttribute(int processId, int controlType, string automationId, string className)
    {
        Assert.NotEqual(
            ElementIdentity.Of(Element()),
            ElementIdentity.Of(Element(processId, controlType, automationId, className)));
    }

    /// <summary>AutomationId の比較は Ordinal (大文字小文字を区別する)。</summary>
    [Fact]
    public void Identity_ComparesTextOrdinally()
    {
        Assert.NotEqual(
            ElementIdentity.Of(Element(automationId: "okButton")),
            ElementIdentity.Of(Element(automationId: "OKBUTTON")));
    }

    /// <summary>作り直された要素は、生きていても別の識別として観測されること。</summary>
    [Fact]
    public void ReadIdentity_ReportsDriftWhenTheElementIsRecycled()
    {
        FakeElement element = Element();
        var tree = new FakeElementTree(new Dictionary<nint, FakeElement> { [1] = element });
        IElementNode node = tree.GetWindow(1)!;
        ElementIdentity resolved = ElementIdentity.Of(node);

        element.IdentityOverride = ElementIdentity.Of(Element(automationId: "somethingElse"));

        Assert.NotEqual(resolved, tree.ReadIdentity(node));
    }

    /// <summary>要素が消えていれば「消滅」として COMException になること。</summary>
    [Fact]
    public void ReadIdentity_ThrowsWhenTheElementIsGone()
    {
        FakeElement element = Element();
        var tree = new FakeElementTree(new Dictionary<nint, FakeElement> { [1] = element });
        IElementNode node = tree.GetWindow(1)!;
        element.Gone = true;

        var exception = Assert.Throws<COMException>(() => tree.ReadIdentity(node));
        Assert.True(UiaTrigger.Interop.ComErrors.IsElementGone(exception));
    }
}
