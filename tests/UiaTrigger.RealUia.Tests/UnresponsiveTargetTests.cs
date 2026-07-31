// T3: 応答しないアプリに対する耐性 (docs/MANUAL-CHECKS.md §8 / docs/DESIGN.md §3)。
//
// 「応答しないアプリを CI で安定して作れない」は成り立たない — 対象アプリは
// 自分たちのものであり stdin のコマンド protocol を持っている。
// UI スレッドを塞ぐ hang コマンドで**決定的に**作れる。
//
// **ただし、手動チェックの項目がそのままの形で全部成立するわけではない** (実測)。
// 成立しないものを無理に緑にせず、到達できる形に組み替えてある。
using System.Diagnostics;
using System.Globalization;
using UiaTrigger.Models;
using Xunit;

namespace UiaTrigger.RealUia.Tests;

public sealed class UnresponsiveTargetTests
{
    /// <summary>対象アプリを塞ぐ時間。</summary>
    /// <remarks>
    /// <see cref="TestTargetProcess"/> の応答待ち (20 秒) より十分に短くとる。
    /// 塞ぐ直前に送っておいた <c>ping</c> をこの後で受けるので、超えると
    /// **ハーネス自身が時間切れになる**。
    /// </remarks>
    private static readonly TimeSpan Hang = TimeSpan.FromSeconds(10);

    /// <summary>
    /// 塞がれていない側の発火を待つ期限。**この数字がこのテストの検出力そのものである。**
    /// </summary>
    /// <remarks>
    /// <para>
    /// <see cref="Hang"/> より**ずっと短く**すること。monitor が全体として直列化していれば
    /// 発火は塞ぎが明けるまで (10 秒) 遅れるので、短い期限だけがその失敗を捕まえる。
    /// </para>
    /// <para>
    /// **<see cref="MonitorHarness.DefaultTimeout"/> (15 秒) で待ってはいけない。**
    /// 完全に直列化した実装でも 10 秒で発火して通ってしまい、**何も主張しないテスト**になる。
    /// 実測では 19〜119ms で発火しているので 3 秒でも余裕がある。
    /// </para>
    /// </remarks>
    private static readonly TimeSpan FireDeadline = TimeSpan.FromSeconds(3);

    /// <summary>
    /// 呼び出し上限時間。既定 (5 秒) より短くして、塞がれた相手への呼び出しが
    /// <see cref="Hang"/> を待たずに諦めるようにする。
    /// </summary>
    private static readonly TimeSpan TransactionTimeout = TimeSpan.FromSeconds(1);

    /// <summary>
    /// 片方のアプリが応答しなくなっても、もう片方のトリガーは発火し続けること。
    /// </summary>
    /// <remarks>
    /// <para>
    /// 「対象アプリを一時停止しても、他のトリガーが動き続ける」ことの一次の網である
    /// (docs/MANUAL-CHECKS.md §8)。**1 プロセスでは言えない** — アプリが固まれば
    /// そのアプリのトリガーは全部固まるので、対象アプリを 2 つ起動して比べるしかない。
    /// </para>
    /// <para>
    /// **「<c>TransactionTimeout</c> を hang より長くすれば B も止まる」という形の
    /// ネガティブコントロールは成立しないので入れていない** — 実測では
    /// 1 秒でも 30 秒でも B は同じ速さで発火する。**B の発火経路は A を一度も通らない**
    /// ためで、呼び出し上限時間は「A を読みに行った呼び出し」を救う設定でしかない。
    /// 代わりに**期限**が検出力を持つ (<see cref="FireDeadline"/> の remarks)。
    /// </para>
    /// <para>
    /// 退行: monitor がトリガーを 1 本のスレッドで直列に評価する実装へ戻す
    /// → B の発火が塞ぎ明けまで遅れ、3 秒の期限で落ちる。
    /// </para>
    /// </remarks>
    [Fact]
    public async Task WhileOneAppIsUnresponsive_TheOtherAppsTriggerKeepsFiring()
    {
        using var appA = TestTargetProcess.Start(TargetProfile.WinForms);
        using var appB = TestTargetProcess.Start(TargetProfile.WinForms);
        PlaceSideBySide(appA, appB);

        TriggerDefinition a = (await Recording.RecordAsync(appA, "btnA", "a")).PinToWindow(appA).WatchName();
        TriggerDefinition b = (await Recording.RecordAsync(appB, "btnB", "b")).PinToWindow(appB).WatchName();

        await using UiaSession session = NewSession();
        await RequireTimeoutsAreSupported(session);

        await using var harness = new MonitorHarness(session);
        await harness.StartAsync(a, b);
        harness.WaitForResolution(resolved: true);
        harness.WaitForResolution(resolved: true);

        var clock = Stopwatch.StartNew();
        appA.Hang(Hang);
        // 「この窓のあいだ塞がっていた」ことを固定するために先に送っておく。
        // 塞ぎの**後**に確かめても、観測した窓のあいだ塞がっていた証拠にならない
        appA.Post("ping");

        appB.Send("set-text btnB changed");

        Assert.Equal("changed", harness.WaitForFire("b", FireDeadline).NewValue.Value);

        RequireItWasBlockedThroughout(appA, clock);
    }

    /// <summary>
    /// 応答しない状態が、そのアプリのトリガーの解決を**恒久的に汚染しない**こと。
    /// </summary>
    /// <remarks>
    /// <para>
    /// MANUAL-CHECKS §8 の「停止したアプリのトリガーは未解決になり、
    /// 復帰後に再解決される」を**到達できる形へ組み替えたもの**である。
    /// </para>
    /// <para>
    /// **前半は実測で成立しない。**解決済みのトリガーは 10 秒塞いでも未解決にならない
    /// (<c>resolved=2/2</c> のまま — docs/DESIGN.md §3)。これは「揺れない」という意味で
    /// むしろ正しい振る舞いであり、無理に未解決を作って assert すると**実装が持っていない
    /// 性質を要求する**ことになる。
    /// </para>
    /// <para>
    /// 代わりに**先に要素を消して未解決にしてから塞ぐ**。塞いでいるあいだ monitor は
    /// A のツリーを歩き直そうとして失敗し続ける。明けてから要素を戻すと解決し、発火する。
    /// 「塞がれたことが解決の経路を壊したままにしない」が言える形になっている。
    /// </para>
    /// <para>
    /// 退行: 塞がれた相手への呼び出しが上限時間で諦めない実装 (あるいは諦めた後に
    /// 解決を再試行しない実装) にすると、要素を戻しても解決せず期限で落ちる**はずである**。
    /// **これは未検証である** — 簡単に注入できる形の退行が無い。
    /// 実際に落ちることを確かめてある退行は 2 種 (<c>hang</c> を塞がないものと、
    /// <c>RefreshCurrentNode</c> のキャッシュ矩形描画を外すもの) だけで、
    /// 前者ならこのテストも落ちる。
    /// </para>
    /// </remarks>
    [Fact]
    public async Task AfterAnUnresponsiveSpell_TheAppsOwnTriggerResolvesAgain()
    {
        using var appA = TestTargetProcess.Start(TargetProfile.WinForms);
        using var appB = TestTargetProcess.Start(TargetProfile.WinForms);
        PlaceSideBySide(appA, appB);

        TriggerDefinition a = (await Recording.RecordAsync(appA, "btnA", "a")).PinToWindow(appA).WatchName();
        TriggerDefinition b = (await Recording.RecordAsync(appB, "btnB", "b")).PinToWindow(appB).WatchName();

        // 先に消して未解決にする。以後 monitor は A を歩き直しつづける
        appA.Send("remove btnA");
        appA.Send("ping");

        await using UiaSession session = NewSession();
        await RequireTimeoutsAreSupported(session);

        await using var harness = new MonitorHarness(session);
        await harness.StartAsync(a, b);
        Assert.False(harness.WaitForResolution(resolved: false).IsResolved);

        var clock = Stopwatch.StartNew();
        appA.Hang(Hang);
        appA.Post("ping");

        // 塞がっている最中も、もう片方は動く (1 件目と同じ主張。ここでは前提として使う)
        appB.Send("set-text btnB changed");
        Assert.Equal("changed", harness.WaitForFire("b", FireDeadline).NewValue.Value);

        RequireItWasBlockedThroughout(appA, clock);

        // 明けてから戻すと解決し、発火する
        appA.Send("add-button btnA");
        harness.WaitForResolution(resolved: true);

        appA.Send("set-text btnA changed");
        Assert.Equal("changed", harness.WaitForFire("a").NewValue.Value);
    }

    private static UiaSession NewSession()
        => new(new UiaSessionOptions { TransactionTimeout = TransactionTimeout });

    /// <summary>
    /// 呼び出し上限時間が本当に適用できていること。
    /// </summary>
    /// <remarks>
    /// ハーネス自身の検証である (docs/TESTING.md §1 の教訓 (b))。<c>IUIAutomation2</c> を
    /// QI できない環境では <c>put_TransactionTimeout</c> に届かず、塞がれた相手への呼び出しが
    /// UIA の既定 (20 秒) まで返らない。**その状態で緑になっても何も言えない**。
    /// </remarks>
    private static async Task RequireTimeoutsAreSupported(UiaSession session) => Assert.True(
        await session.GetSupportsTimeoutsAsync(),
        "このセッションは呼び出し上限時間を適用できていません (IUIAutomation2 を QI できない環境)。" +
        "塞がれた相手への呼び出しが UIA の既定まで返らないため、このテストの結果は解釈できません。");

    /// <summary>
    /// 2 つの対象アプリを**重ならない場所**へ置き、それぞれに 1 個ずつボタンを足す。
    /// </summary>
    /// <remarks>
    /// <para>
    /// どちらも既定位置 (40,40) に出るうえ <c>TopMost</c> なので、置き直さないと重なる。
    /// 記録は座標 (<c>ElementFromPoint</c>) を通るため、重なっていると
    /// **別のプロセスの窓を記録する**。
    /// </para>
    /// <para>
    /// **ここで窓を縮めてよいのは、このシナリオがボタンを 1 個しか置かないためである。**
    /// 既定サイズ (520x1000) は兄弟走査が 20 個以上の子を縦に並べるために要るもので、
    /// そちらで縮めるとクライアント領域からはみ出した子を記録できなくなる
    /// (tests/UiaTrigger.TestTarget/TargetApp.cs の <c>place</c> のコメント)。
    /// </para>
    /// <para>
    /// **ボタンの名前を 2 つで変えてある** (<c>btnA</c> / <c>btnB</c>)。同じ実行ファイル・
    /// 同じクラス名なので、名前まで同じにすると**取り違えた定義が静かに解決してしまい**、
    /// 「2 つのアプリを区別できている」という前提そのものが検査されない。
    /// </para>
    /// </remarks>
    private static void PlaceSideBySide(TestTargetProcess appA, TestTargetProcess appB)
    {
        appA.Send("place 40 40 420 300");
        appB.Send("place 500 40 420 300");
        appA.Send("add-button btnA");
        appB.Send("add-button btnB");
        appA.Send("ping");
        appB.Send("ping");
    }

    /// <summary>
    /// 観測の窓のあいだ、対象アプリが本当に応答していなかったこと。
    /// </summary>
    /// <remarks>
    /// **ハーネス自身の検証である** (docs/TESTING.md §1 の教訓 (b))。塞ぐ**前**に送っておいた
    /// <c>ping</c> をここで受ける。塞ぎが効いていなければ即座に返っているので、
    /// 経過時間が短ければ「もう片方が発火した」は**何も言っていない** —
    /// 応答するアプリの隣で発火しただけである。
    /// </remarks>
    private static void RequireItWasBlockedThroughout(TestTargetProcess app, Stopwatch clock)
    {
        app.AwaitResponse();
        TimeSpan elapsed = clock.Elapsed;
        // 余裕を見て 8 割。実測では hang の長さとほぼ一致する
        TimeSpan least = Hang * 0.8;
        Assert.True(
            elapsed >= least,
            string.Create(
                CultureInfo.InvariantCulture,
                $"対象アプリは {elapsed.TotalMilliseconds:0}ms で応答しました ({Hang.TotalMilliseconds:0}ms 塞いだはずです)。") +
            "塞ぐ前に送った ping がこれだけ早く返ったということは、観測のあいだ相手は応答していました。" +
            "この状態では「もう片方が発火した」ことに意味がありません。");
    }
}
