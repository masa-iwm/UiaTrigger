using UiaTrigger.Models;
using UiaTrigger.Monitoring;
using UiaTrigger.Resources;
using Xunit;

namespace UiaTrigger.RealUia.Tests;

/// <summary>
/// イベント購読スコープの回帰 (docs/DESIGN.md B2/B3)。
///
/// 「余分なイベントを受け取らない」という否定形の性質なので、擬似ツリーでは検証できない。
/// 実 UIA で受信件数を数えて確かめる。
///
/// <para>
/// このファイルは **全件を両プロファイル (WinForms / WPF) で走らせる**。
/// プロバイダーによってイベントの上げ方が違う (docs/DESIGN.md §6) ので、
/// 片方でしか測っていない購読スコープは、測っていないのと大差ない。
/// </para>
/// </summary>
public sealed class EventScopeTests
{
    /// <summary>イベントが落ち着くのを待つ (デバウンス 200ms + 配送)。</summary>
    private static readonly TimeSpan Quiet = TimeSpan.FromMilliseconds(1500);

    /// <summary>
    /// B3: 解決後は経路上の段だけを購読していること。
    ///
    /// **同じ形の構造変化 (子を 1 個足す) を経路の内と外で起こして比べる**。
    /// 内側では届き、外側では 1 件も届かないことが同時に成り立って初めて、
    /// 「絞れている」と「絞りすぎていない」の両方が言える。
    /// </summary>
    [Theory]
    [MemberData(nameof(TargetProfile.AllNames), MemberType = typeof(TargetProfile))]
    public async Task Structure_OnceResolved_OnlyArrivesForChangesOnThePath(string profile)
    {
        using var target = TestTargetProcess.Start(profile);
        target.Send("add-button btnTarget");
        target.Send("add-branch offPath");

        TriggerDefinition definition = (await Recording.RecordAsync(target, "btnTarget"))
            .PinToWindow(target).WatchName();

        await using var harness = new MonitorHarness();
        await harness.StartAsync(definition);
        harness.WaitForResolution(resolved: true);
        await Task.Delay(Quiet, TestContext.Current.CancellationToken);

        // 購読の形そのもの: ウィンドウ + root の 2 段だけ、かつ経路スコープ
        TriggerMonitorDiagnostics resolved = harness.Monitor.GetDiagnostics();
        Assert.Equal(definition.Locator.Steps.Count, resolved.StructureSubscriptionCount);
        Assert.True(resolved.StructureIsPathScoped);
        Assert.Equal(1, resolved.ResolvedTriggerCount);

        // 経路外 (offPath パネルの中) に子を足す
        int before = harness.Monitor.GetDiagnostics().StructureEventCount;
        for (int i = 0; i < 5; i++)
        {
            target.Send(FormattableString.Invariant($"add-child offPath far{i}"));
        }
        await Task.Delay(Quiet, TestContext.Current.CancellationToken);
        int offPath = harness.Monitor.GetDiagnostics().StructureEventCount - before;

        // 経路上 (root = 対象の親) に子を足す
        int middle = harness.Monitor.GetDiagnostics().StructureEventCount;
        target.Send("add-button btnDecoy");
        await Task.Delay(Quiet, TestContext.Current.CancellationToken);
        int onPath = harness.Monitor.GetDiagnostics().StructureEventCount - middle;

        Assert.True(
            onPath > 0,
            $"経路上の構造変化が届いていません。この計測には検出力がありません " +
            $"(offPath={offPath}, onPath={onPath}, sweeps={harness.Monitor.GetDiagnostics().SweepCount})。{harness.History()}");
        Assert.True(
            offPath == 0,
            $"経路外の構造変化を {offPath} 件受け取りました。購読が経路上の段より広がっています " +
            $"(ウィンドウ全体の Subtree に戻っていないか確認すること)。{harness.History()}");
    }

    /// <summary>
    /// 未解決の間はウィンドウ全体を 1 つだけ購読していること (どこに出現するか分からないため)。
    /// 解決後の経路購読と対で、張り替えが起きていることを示す。
    /// </summary>
    [Theory]
    [MemberData(nameof(TargetProfile.AllNames), MemberType = typeof(TargetProfile))]
    public async Task Structure_WhileUnresolved_WatchesTheWholeWindow(string profile)
    {
        using var target = TestTargetProcess.Start(profile);
        target.Send("add-button btnTarget");

        TriggerDefinition definition = (await Recording.RecordAsync(target, "btnTarget"))
            .PinToWindow(target).WatchName();
        target.Send("remove btnTarget");

        await using var harness = new MonitorHarness();
        await harness.StartAsync(definition);
        harness.WaitForResolution(resolved: false);

        Assert.Equal(1, harness.Monitor.GetDiagnostics().StructureSubscriptionCount);
        Assert.False(harness.Monitor.GetDiagnostics().StructureIsPathScoped);

        // 出現すれば経路購読に張り替わる
        target.Send("add-button btnTarget");
        harness.WaitForResolution(resolved: true);

        Assert.Equal(definition.Locator.Steps.Count, harness.Monitor.GetDiagnostics().StructureSubscriptionCount);
        Assert.True(harness.Monitor.GetDiagnostics().StructureIsPathScoped);
    }

    // ---- A21: 対象が削除されたことを経路側で拾えるか (プロバイダー差は docs/DESIGN.md §6、生存判定は §8) ----

    /// <summary>
    /// ネイティブ UIA プロバイダーでは、対象の**削除**が経路購読に届くこと。
    ///
    /// <para>
    /// WinForms (MSAA ブリッジ) では削除が <c>StructureChanged</c> として届かず
    /// <c>WindowClosed</c> 経由になるが、ネイティブプロバイダーでは経路側で拾える
    /// (docs/DESIGN.md §6)。WinForms 側は実測で 0 件なので、この assert は
    /// ネイティブプロバイダーにしか成り立たない — 両方に広げると偽の期待になる。
    /// </para>
    /// </summary>
    [Theory]
    [MemberData(nameof(TargetProfile.AllNames), MemberType = typeof(TargetProfile))]
    public async Task Structure_WhenTheTargetIsDeleted_ArrivesOnThePath(string profile)
    {
        Assert.SkipUnless(
            TargetProfile.ByName(profile).IsNativeProvider,
            "MSAA ブリッジ (WinForms) では削除が StructureChanged として届かない (docs/DESIGN.md §6)。" +
            "そちらの経路は WindowClosed_ForATopLevelWindow_ReachesTheMonitor が見ている。");

        using var target = TestTargetProcess.Start(profile);
        target.Send("add-button btnTarget");
        target.Send("add-branch offPath");
        target.Send("add-child offPath victim");

        TriggerDefinition definition = (await Recording.RecordAsync(target, "btnTarget"))
            .PinToWindow(target).WatchName();

        await using var harness = new MonitorHarness();
        await harness.StartAsync(definition);
        harness.WaitForResolution(resolved: true);
        await Task.Delay(Quiet, TestContext.Current.CancellationToken);

        // ネガティブコントロール: 経路外の削除は 1 件も届かず、解決も維持される
        int before = harness.Monitor.GetDiagnostics().StructureEventCount;
        target.Send("remove victim");
        await Task.Delay(Quiet, TestContext.Current.CancellationToken);
        int offPath = harness.Monitor.GetDiagnostics().StructureEventCount - before;
        Assert.True(
            offPath == 0,
            $"経路外の削除を {offPath} 件受け取りました。購読が広がっています。{harness.History()}");
        Assert.Equal(1, harness.Monitor.GetDiagnostics().ResolvedTriggerCount);

        // 本題: 対象そのものの削除は経路 (Element | Children) で届く
        before = harness.Monitor.GetDiagnostics().StructureEventCount;
        target.Send("remove btnTarget");
        await Task.Delay(Quiet, TestContext.Current.CancellationToken);
        int onPath = harness.Monitor.GetDiagnostics().StructureEventCount - before;

        Assert.True(
            onPath > 0,
            $"対象の削除が経路購読に届きませんでした (offPath={offPath}, onPath={onPath})。" +
            $"経路スコープを Element だけに戻していないか確認すること。{harness.History()}");

        // 届いただけでなく、実際に手放していること (A21)
        TriggerResolutionChangedEventArgs lost = harness.WaitForResolution(resolved: false);
        Assert.Equal(Strings.Status_ElementDetached, lost.Message);
    }

    /// <summary>
    /// A21: 祖先ごとまとめて消えた場合も未解決になること。
    ///
    /// <para>
    /// **ここが動かないなら B3 の実装が誤っている。**
    /// 「親 1 段だけを購読する」案を却下した理由そのものでもある (docs/DESIGN.md §6)。
    /// </para>
    /// <para>
    /// ネイティブプロバイダーでは、切り離された部分木の中では親子関係が保たれたまま
    /// 応答し続けるため、**属性の突合でも上向きの navigation でも検出できない** (docs/DESIGN.md §8)。
    /// 下向きの到達性を確かめる <c>IsStillOnThePath</c> を外すと WPF 側だけが落ちる。
    /// </para>
    /// </summary>
    [Theory]
    [MemberData(nameof(TargetProfile.AllNames), MemberType = typeof(TargetProfile))]
    public async Task Structure_WhenAnAncestorIsRemovedWholesale_BecomesUnresolved(string profile)
    {
        using var target = TestTargetProcess.Start(profile);
        target.Send("add-branch branch");
        target.Send("add-child branch btnTarget");
        target.Send("add-branch sibling");
        target.Send("add-child sibling btnOther");

        TriggerDefinition definition = (await Recording.RecordAsync(target, "btnTarget"))
            .PinToWindow(target).WatchName().WithoutSearch();
        // ウィンドウ → root → branch → btnTarget
        Assert.Equal(3, definition.Locator.Steps.Count);

        await using var harness = new MonitorHarness();
        await harness.StartAsync(definition);
        harness.WaitForResolution(resolved: true);
        await Task.Delay(Quiet, TestContext.Current.CancellationToken);

        // ネガティブコントロール: 同じ深さの経路外の部分木を丸ごと消しても解決は維持される
        target.Send("remove sibling");
        await Task.Delay(Quiet, TestContext.Current.CancellationToken);
        Assert.True(
            harness.Monitor.GetDiagnostics().ResolvedTriggerCount == 1,
            $"経路外の部分木を消しただけで未解決になりました。{harness.History()}");

        // 本題: 対象の祖先を丸ごと消す。対象自身には触れていない
        target.Send("remove branch");

        harness.WaitForResolution(resolved: false);
    }

    /// <summary>
    /// B3 の狙いそのもの: 解決済みの間、経路外の激しい構造変化を 1 件も拾わないこと。
    ///
    /// MANUAL-CHECKS §7 の「Chromium 系で CPU が張り付かない」のうち、自動化できる半分である。
    /// CPU 使用率は測れないが、**受信件数 0** はその必要条件であり、
    /// 経路上で同じ churn を起こせば届くことと対で見れば検出力も示せる。
    /// </summary>
    [Theory]
    [MemberData(nameof(TargetProfile.AllNames), MemberType = typeof(TargetProfile))]
    public async Task Structure_WhileResolved_IgnoresHeavyOffPathChurn(string profile)
    {
        using var target = TestTargetProcess.Start(profile);
        target.Send("add-button btnTarget");
        target.Send("add-branch offPath");

        TriggerDefinition definition = (await Recording.RecordAsync(target, "btnTarget"))
            .PinToWindow(target).WatchName();

        await using var harness = new MonitorHarness();
        await harness.StartAsync(definition);
        harness.WaitForResolution(resolved: true);
        await Task.Delay(Quiet, TestContext.Current.CancellationToken);

        // 経路外で 30 回の追加・削除。1 コマンド = 1 回の構造変化なので、
        // プロバイダーがまとめて捨てる余地が無い (churn コマンドは 1 コマンド内で
        // 追加と削除を済ませてしまうため、どちらのプロバイダーでも 1 件も上がらない)
        int before = harness.Monitor.GetDiagnostics().StructureEventCount;
        for (int i = 0; i < 30; i++)
        {
            target.Send(FormattableString.Invariant($"add-child offPath churn{i}"));
            target.Send(FormattableString.Invariant($"remove churn{i}"));
        }
        await Task.Delay(Quiet, TestContext.Current.CancellationToken);
        int offPath = harness.Monitor.GetDiagnostics().StructureEventCount - before;

        // 検出力の対: 経路上で子を 1 個足せば届く。
        // ここだけ「足して残す」のは実測に基づく — MSAA ブリッジ (WinForms) では
        // 追加してすぐ削除した子の構造変化は 1 件も観測できない (docs/DESIGN.md §6)。
        // 「削除も届くこと」はネイティブプロバイダー側で
        // Structure_WhenTheTargetIsDeleted_ArrivesOnThePath が見ている
        before = harness.Monitor.GetDiagnostics().StructureEventCount;
        target.Send("add-button onPathDecoy");
        await Task.Delay(Quiet, TestContext.Current.CancellationToken);
        int onPath = harness.Monitor.GetDiagnostics().StructureEventCount - before;

        Assert.True(
            onPath > 0,
            $"経路上の構造変化が届いていません。この計測には検出力がありません " +
            $"(offPath={offPath}, onPath={onPath})。{harness.History()}");
        Assert.True(
            offPath == 0,
            $"経路外で 30 回の追加・削除を行ったところ {offPath} 件届きました。" +
            $"動的な UI を対象にすると同じだけ再解決が走ります (docs/DESIGN.md B3)。{harness.History()}");
    }

    /// <summary>
    /// B2: トップレベルウィンドウの出現・消滅が届くこと。
    ///
    /// root の購読を Subtree から Children へ絞った (全プロセスの全ウィンドウイベントを
    /// 受け取らないため) が、絞りすぎるとウィンドウの出現を取りこぼして
    /// 「トリガーが黙って動かなくなる」— 最も避けたい壊れ方になる。ここではその側を固定する。
    /// このトリガーは未解決時に何も購読しない (ウィンドウ自体が無い) ため、
    /// WindowOpened が届かなければ永久に発火しない。
    /// </summary>
    [Theory]
    [MemberData(nameof(TargetProfile.AllNames), MemberType = typeof(TargetProfile))]
    public async Task WindowOpened_ForANewTopLevelWindow_ReachesTheMonitor(string profile)
    {
        using var target = TestTargetProcess.Start(profile);
        string extraTitle = target.Title + "-extra";

        var definition = new TriggerDefinition
        {
            Id = "window",
            Window = new WindowIdentity
            {
                ProcessName = target.Profile.ProcessName,
                WindowName = extraTitle,
                WindowNameMatch = MatchStrength.Required,
            },
            // 経路が空 = 対象はトップレベルウィンドウ自身
            Locator = new ElementLocator { View = TreeViewMode.Control },
            On = TriggerOn.ElementAppeared,
        };

        await using var harness = new MonitorHarness();
        await harness.StartAsync(definition);
        harness.WaitForResolution(resolved: false);

        target.Send("open-window " + extraTitle);

        Assert.Equal(TriggerOn.ElementAppeared, harness.WaitForFire().On);
    }

    /// <summary>B2 の対: ウィンドウの消滅も届くこと。</summary>
    [Theory]
    [MemberData(nameof(TargetProfile.AllNames), MemberType = typeof(TargetProfile))]
    public async Task WindowClosed_ForATopLevelWindow_ReachesTheMonitor(string profile)
    {
        using var target = TestTargetProcess.Start(profile);
        string extraTitle = target.Title + "-extra";
        target.Send("open-window " + extraTitle);

        var definition = new TriggerDefinition
        {
            Id = "window",
            Window = new WindowIdentity
            {
                ProcessName = target.Profile.ProcessName,
                WindowName = extraTitle,
                WindowNameMatch = MatchStrength.Required,
            },
            Locator = new ElementLocator { View = TreeViewMode.Control },
            On = TriggerOn.ElementRemoved,
        };

        await using var harness = new MonitorHarness();
        await harness.StartAsync(definition);
        harness.WaitForResolution(resolved: true);

        target.Send("close-window " + extraTitle);

        Assert.Equal(TriggerOn.ElementRemoved, harness.WaitForFire().On);
    }

    /// <summary>
    /// ネガティブコントロール: 条件に合わないウィンドウが開いても発火しないこと。
    /// 上の 2 つが「何にでも反応する」わけではないことを示す。
    /// </summary>
    [Theory]
    [MemberData(nameof(TargetProfile.AllNames), MemberType = typeof(TargetProfile))]
    public async Task WindowOpened_ForAnUnrelatedWindow_DoesNotFire(string profile)
    {
        using var target = TestTargetProcess.Start(profile);

        var definition = new TriggerDefinition
        {
            Id = "window",
            Window = new WindowIdentity
            {
                ProcessName = target.Profile.ProcessName,
                WindowName = target.Title + "-wanted",
                WindowNameMatch = MatchStrength.Required,
            },
            Locator = new ElementLocator { View = TreeViewMode.Control },
            On = TriggerOn.ElementAppeared,
        };

        await using var harness = new MonitorHarness();
        await harness.StartAsync(definition);
        harness.WaitForResolution(resolved: false);

        target.Send("open-window " + target.Title + "-unrelated");

        harness.AssertNoFire(TimeSpan.FromSeconds(3));
    }
}
