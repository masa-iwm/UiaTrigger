using UiaTrigger.Models;
using UiaTrigger.Resolution;
using Xunit;

namespace UiaTrigger.Tests;

/// <summary>
/// ウィンドウ候補の共有 (docs/DESIGN.md B4) と照合方式 (A4) の回帰テスト。
///
/// 共有が無いと、sweep のたびにトリガー 1 件ごとに EnumWindows し、
/// ウィンドウ 1 個ごとに OpenProcess することになる (トリガー N 件・ウィンドウ M 個で N×M 回)。
/// 照合は、全属性を 1 つのスコアに足し込んで閾値を掛けると、
/// 実質「ClassName 完全一致が必須」になる。
/// </summary>
public sealed class WindowCandidateCacheTests
{
    private static readonly ResolverOptions Options = new();

    private static FakeWindowSource ThreeWindows() =>
        new FakeWindowSource(
                new WindowInfo(1, 100, "Notepad", "a.txt - Notepad"),
                new WindowInfo(2, 100, "Notepad", "b.txt - Notepad"),
                new WindowInfo(3, 200, "Chrome_WidgetWin_1", "Example"))
            .WithProcess(100, @"C:\Windows\notepad.exe")
            .WithProcess(200, @"C:\Program Files\chrome.exe");

    private static WindowIdentity Notepad(string? title = null) => new()
    {
        ProcessName = "notepad.exe",
        ClassName = "Notepad",
        WindowName = title,
        // 既定は Ignored。タイトルで順位を付けたいテストだけ明示的に上げる
        WindowNameMatch = title is null ? MatchStrength.Ignored : MatchStrength.Preferred,
    };

    /// <summary>
    /// 識別がすべて異なっていても (= 候補リストのキャッシュが 1 度も効かなくても)
    /// ウィンドウ列挙自体はパスあたり 1 回であること。
    /// </summary>
    [Fact]
    public void GetCandidates_EnumeratesWindowsOncePerPass()
    {
        FakeWindowSource source = ThreeWindows();
        var cache = new WindowCandidateCache(source, Options);

        for (int i = 0; i < 10; i++)
        {
            cache.GetCandidates(Notepad($"title-{i}"));
        }

        Assert.Equal(1, source.EnumerateCalls);
        Assert.Equal(1, cache.EnumerationCount);
    }

    /// <summary>識別が違っても列挙は共有され、プロセスパスの取得は PID ごとに 1 回であること。</summary>
    [Fact]
    public void GetCandidates_SharesTheProcessPathLookupAcrossIdentities()
    {
        FakeWindowSource source = ThreeWindows();
        var cache = new WindowCandidateCache(source, Options);

        cache.GetCandidates(Notepad());
        cache.GetCandidates(new WindowIdentity { ProcessName = "chrome.exe", ClassName = "Chrome_WidgetWin_1" });
        cache.GetCandidates(Notepad("b.txt - Notepad"));

        Assert.Equal(1, source.EnumerateCalls);
        // ウィンドウは 3 個あるが PID は 2 種類
        Assert.Equal(2, source.ImagePathCalls);
        Assert.Equal(2, cache.ImagePathLookupCount);
    }

    /// <summary>同じ識別なら候補計算そのものが再利用されること。</summary>
    [Fact]
    public void GetCandidates_ReturnsTheSameResultForAnEquivalentIdentity()
    {
        var cache = new WindowCandidateCache(ThreeWindows(), Options);

        IReadOnlyList<nint> first = cache.GetCandidates(Notepad());
        IReadOnlyList<nint> second = cache.GetCandidates(Notepad());

        Assert.Same(first, second);
    }

    /// <summary>
    /// 照合の強さが違えば候補集合も違うので、キャッシュを共有しないこと。
    /// これを取り違えると、別条件のトリガーが他人の候補リストを使うことになる。
    /// </summary>
    [Fact]
    public void GetCandidates_DoesNotShareTheCacheAcrossDifferentMatchStrengths()
    {
        var cache = new WindowCandidateCache(ThreeWindows(), Options);

        var preferred = Notepad("b.txt - Notepad");
        var required = Notepad("b.txt - Notepad");
        required.WindowNameMatch = MatchStrength.Required;

        Assert.Equal([(nint)2, 1], cache.GetCandidates(preferred));
        Assert.Equal([(nint)2], cache.GetCandidates(required));
    }

    /// <summary>プロセス名は大文字小文字を無視して一致させること (キャッシュキーも同様)。</summary>
    [Fact]
    public void GetCandidates_MatchesTheProcessNameCaseInsensitively()
    {
        FakeWindowSource source = ThreeWindows();
        var cache = new WindowCandidateCache(source, Options);

        IReadOnlyList<nint> lower = cache.GetCandidates(Notepad());
        var upper = Notepad();
        upper.ProcessName = "NOTEPAD.EXE";

        Assert.Same(lower, cache.GetCandidates(upper));
        Assert.Equal([(nint)1, 2], lower);
    }

    [Fact]
    public void GetCandidates_ExcludesWindowsFromOtherProcesses()
    {
        var cache = new WindowCandidateCache(ThreeWindows(), Options);

        Assert.Equal([(nint)3], cache.GetCandidates(
            new WindowIdentity { ProcessName = "chrome.exe", ClassName = "Chrome_WidgetWin_1" }));
    }

    /// <summary>
    /// タイトル一致で加点された候補が先に来ること。
    /// 同点の候補は列挙順 (Z オーダー) を保つ — List.Sort は不安定なので明示的に固定している。
    /// </summary>
    [Fact]
    public void GetCandidates_RanksByScoreAndKeepsZOrderForTies()
    {
        var cache = new WindowCandidateCache(ThreeWindows(), Options);

        Assert.Equal([(nint)2, 1], cache.GetCandidates(Notepad("b.txt - Notepad")));
        Assert.Equal([(nint)1, 2], cache.GetCandidates(Notepad()));
    }

    /// <summary>プロセスパスを取得できないウィンドウ (昇格プロセス等) は候補にしないこと。</summary>
    [Fact]
    public void GetCandidates_SkipsWindowsWhoseImagePathIsUnavailable()
    {
        var source = new FakeWindowSource(new WindowInfo(9, 999, "Notepad", "elevated"));
        var cache = new WindowCandidateCache(source, Options);

        Assert.Empty(cache.GetCandidates(Notepad()));
    }

    /// <summary>ProcessName が Required なのに記録されていなければ候補なし (閉じた側に倒す)。</summary>
    [Fact]
    public void GetCandidates_WithoutAProcessNameMatchesNothing()
    {
        FakeWindowSource source = ThreeWindows();
        var cache = new WindowCandidateCache(source, Options);

        Assert.Empty(cache.GetCandidates(new WindowIdentity()));
        Assert.Equal(0, source.EnumerateCalls);
        Assert.True(WindowCandidateCache.HasUnsatisfiableRequirement(new WindowIdentity()));
    }

    // ---------- A4: 揮発性の属性で足切りしない ----------

    /// <summary>
    /// A4 そのもの。WinForms のクラス名には起動ごとに変わる token が入る。
    /// 記録時と違うクラス名になっていても候補として残ること。
    ///
    /// ClassName 一致を実質必須にすると、不一致だけでそのアプリのトリガーは
    /// <b>恒久的に</b>解決不能になる (docs/DESIGN.md A4)。
    /// </summary>
    [Fact]
    public void GetCandidates_StillMatchesWhenAWinFormsClassNameTokenChanged()
    {
        var source = new FakeWindowSource(
                new WindowInfo(7, 300, "WindowsForms10.Window.8.app.0.99aa11b_r9_ad1", "新しいタイトル"))
            .WithProcess(300, @"C:\apps\legacy.exe");
        var cache = new WindowCandidateCache(source, Options);

        var recorded = new WindowIdentity
        {
            ProcessName = "legacy.exe",
            ProcessPath = @"C:\apps\legacy.exe",
            // 記録時のクラス名 (token 部分が今回とは違う)
            ClassName = "WindowsForms10.Window.8.app.0.141b42a_r6_ad1",
            WindowName = "記録時のタイトル",
        };

        Assert.Equal([(nint)7], cache.GetCandidates(recorded));
    }

    /// <summary>
    /// ただし Required に上げれば足切りできること (足切りしたい利用者のための逃げ道)。
    /// 上のテストが「単に何でも通している」のではないことの対。
    /// </summary>
    [Fact]
    public void GetCandidates_RejectsAMismatchedClassNameWhenItIsRequired()
    {
        var source = new FakeWindowSource(
                new WindowInfo(7, 300, "WindowsForms10.Window.8.app.0.99aa11b_r9_ad1", "title"))
            .WithProcess(300, @"C:\apps\legacy.exe");
        var cache = new WindowCandidateCache(source, Options);

        var recorded = new WindowIdentity
        {
            ProcessName = "legacy.exe",
            ClassName = "WindowsForms10.Window.8.app.0.141b42a_r6_ad1",
            ClassNameMatch = MatchStrength.Required,
        };

        Assert.Empty(cache.GetCandidates(recorded));
    }

    /// <summary>
    /// 一致するクラス名を持つ候補のほうが先に来ること。
    /// 足切りには使わないが、順位付けには使う。
    /// </summary>
    [Fact]
    public void GetCandidates_RanksAMatchingClassNameFirst()
    {
        var source = new FakeWindowSource(
                new WindowInfo(1, 100, "OtherClass", "x"),
                new WindowInfo(2, 100, "Notepad", "y"))
            .WithProcess(100, @"C:\Windows\notepad.exe");
        var cache = new WindowCandidateCache(source, Options);

        Assert.Equal([(nint)2, 1], cache.GetCandidates(Notepad()));
    }

    /// <summary>候補数には上限があること (プロセスに何十個もウィンドウがある場合の歯止め)。</summary>
    [Fact]
    public void GetCandidates_IsCappedAtMaxWindowCandidates()
    {
        var windows = new WindowInfo[20];
        for (int i = 0; i < windows.Length; i++)
        {
            windows[i] = new WindowInfo(i + 1, 100, "Notepad", $"w{i}");
        }
        var source = new FakeWindowSource(windows).WithProcess(100, @"C:\Windows\notepad.exe");
        var cache = new WindowCandidateCache(source, new ResolverOptions { MaxWindowCandidates = 4 });

        Assert.Equal(4, cache.GetCandidates(Notepad()).Count);
    }
}
