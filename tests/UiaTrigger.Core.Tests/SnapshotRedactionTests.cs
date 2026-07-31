using UiaTrigger.Models;
using Xunit;

namespace UiaTrigger.Tests;

/// <summary>
/// パスワード欄の値をスナップショットから落とすこと (docs/DESIGN.md C12)。
///
/// スナップショットは発火イベントに添付され、ホストのログにそのまま流れる。
/// IsPassword を見ないと、パスワード欄を監視対象にすると (あるいは
/// たまたま監視対象がパスワード欄だと) 平文がログに残りうる。
/// </summary>
public sealed class SnapshotRedactionTests
{
    private static ElementPropertySnapshot Snapshot(bool isPassword, bool supportsValue = true) => new()
    {
        Name = "hunter2",
        AutomationId = "passwordBox",
        ClassName = "Edit",
        ControlType = 50004,
        ControlTypeName = "Edit",
        IsPassword = isPassword,
        SupportsValuePattern = supportsValue,
        Value = supportsValue ? "hunter2" : null,
    };

    /// <summary>
    /// Value だけでなく Name も伏せる。プロバイダーによっては Name 側に値が出るためであり、
    /// ラベルを 1 つ隠す代価は秘密を漏らす代価よりはるかに小さい。
    /// </summary>
    [Fact]
    public void PasswordFields_HaveTheirValueAndNameWithheld()
    {
        ElementPropertySnapshot redacted = Snapshot(isPassword: true).RedactIfPassword();

        Assert.Equal(ElementPropertySnapshot.RedactedMarker, redacted.Value);
        Assert.Equal(ElementPropertySnapshot.RedactedMarker, redacted.Name);
        Assert.DoesNotContain("hunter2", redacted.ToString(), StringComparison.Ordinal);
    }

    /// <summary>伏せてもパスワード欄であること自体は分かること (ホストが説明できるように)。</summary>
    [Fact]
    public void PasswordFields_KeepTheirNonSensitiveProperties()
    {
        ElementPropertySnapshot redacted = Snapshot(isPassword: true).RedactIfPassword();

        Assert.True(redacted.IsPassword);
        Assert.Equal("passwordBox", redacted.AutomationId);
        Assert.Equal("Edit", redacted.ClassName);
    }

    /// <summary>ValuePattern 非対応なら Value は null のまま (伏字で「値がある」ことにしない)。</summary>
    [Fact]
    public void PasswordFieldWithoutAValuePattern_KeepsANullValue()
    {
        ElementPropertySnapshot redacted = Snapshot(isPassword: true, supportsValue: false).RedactIfPassword();

        Assert.Null(redacted.Value);
        Assert.Equal(ElementPropertySnapshot.RedactedMarker, redacted.Name);
    }

    /// <summary>普通の要素には一切手を触れないこと。</summary>
    [Fact]
    public void OrdinaryElements_AreUntouched()
    {
        ElementPropertySnapshot snapshot = Snapshot(isPassword: false);

        Assert.Same(snapshot, snapshot.RedactIfPassword());
    }

    /// <summary>条件評価から見ても伏字であること (伏せた値が比較経路で復活しないこと)。</summary>
    [Fact]
    public void TheWithheldValueIsAlsoWithheldFromComparison()
    {
        ElementPropertySnapshot redacted = Snapshot(isPassword: true).RedactIfPassword();

        Assert.Equal(ElementPropertySnapshot.RedactedMarker, redacted.GetComparisonValue(TriggerProperty.Value).Value);
        Assert.Equal(ElementPropertySnapshot.RedactedMarker, redacted.GetComparisonValue(TriggerProperty.Name).Value);
    }
}
