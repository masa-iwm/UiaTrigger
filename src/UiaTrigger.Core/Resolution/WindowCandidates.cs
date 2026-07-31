// ウィンドウ候補の列挙とその共有 (docs/DESIGN.md B4)。
//
// sweep のたびにトリガー 1 件ごとに独立して全ウィンドウを EnumWindows し、
// ウィンドウ 1 個ごとに OpenProcess + QueryFullProcessImageName する形だと、
// 同じ WindowIdentity を持つトリガーが 10 件あれば同じ列挙を 10 回やり直すことになる。
// 1 回の sweep の間だけ生きるキャッシュに閉じ込めて共有する。
using UiaTrigger.Interop;
using UiaTrigger.Models;

namespace UiaTrigger.Resolution;

/// <summary>トップレベルウィンドウ 1 個の Win32 側の属性。</summary>
internal readonly record struct WindowInfo(nint Hwnd, uint ProcessId, string ClassName, string Title);

/// <summary>ウィンドウ列挙の供給元。テストでは擬似実装を差し込む。</summary>
internal interface IWindowSource
{
    /// <summary>可視のトップレベルウィンドウを Z オーダー順 (最前面が先頭) に列挙する。</summary>
    IReadOnlyList<WindowInfo> GetVisibleWindows();

    /// <summary>プロセス ID から実行ファイルのフルパス。取得できなければ null (昇格プロセス等)。</summary>
    string? GetProcessImagePath(uint processId);
}

/// <summary>実際の Win32 API を叩く <see cref="IWindowSource"/>。</summary>
internal sealed class Win32WindowSource : IWindowSource
{
    public IReadOnlyList<WindowInfo> GetVisibleWindows()
    {
        var windows = new List<WindowInfo>();
        foreach (nint hwnd in NativeMethods.EnumTopLevelWindows())
        {
            if (!NativeMethods.IsWindowVisible(hwnd))
            {
                continue;
            }
            NativeMethods.GetWindowThreadProcessId(hwnd, out uint processId);
            if (processId == 0)
            {
                continue;
            }
            windows.Add(new WindowInfo(hwnd, processId, NativeMethods.GetClassName(hwnd), NativeMethods.GetWindowText(hwnd)));
        }
        return windows;
    }

    public string? GetProcessImagePath(uint processId) => NativeMethods.GetProcessImagePath(processId);
}

/// <summary>
/// 1 回の解決パス (起動時 / 1 回の sweep) の間だけ生きるウィンドウ候補キャッシュ。
/// ウィンドウ列挙・プロセスパス取得・候補リストの 3 つを全トリガー間で共有する。
/// <b>パスをまたいで使い回さないこと</b> — ウィンドウの出現・消滅を見落とす。
///
/// 照合方式は docs/DESIGN.md A4 のとおり。全属性を 1 つのスコアに足し込んで
/// 閾値 (実質「ClassName 完全一致が必須」) を掛けると、
/// WinForms のようにクラス名へ起動ごとに変わる token が入るアプリでは
/// 恒久的に解決不能になる。ここでは MatchStrength.Required の属性だけが足切りを行い、
/// スコアは残った候補の試行順を決めるだけである。
/// </summary>
internal sealed class WindowCandidateCache(IWindowSource source, ResolverOptions options)
{
    private readonly Dictionary<uint, string?> _imagePaths = [];
    private readonly Dictionary<IdentityKey, IReadOnlyList<nint>> _candidates = new(IdentityKeyComparer.Instance);
    private IReadOnlyList<WindowInfo>? _windows;

    /// <summary>診断用: 実際にウィンドウ列挙を行った回数 (共有できていれば 1 パスにつき 1)。</summary>
    public int EnumerationCount { get; private set; }

    /// <summary>診断用: OpenProcess 相当 (実行ファイルパス取得) を行った回数。</summary>
    public int ImagePathLookupCount { get; private set; }

    /// <summary>
    /// 実行ファイルパスを読めなかったプロセスの数 (docs/DESIGN.md A10)。
    ///
    /// 非昇格クライアントから昇格プロセスを OpenProcess すると必ず失敗する。それを
    /// 静かに候補から落とすだけだと、「昇格したアプリは監視できない」という制約が
    /// <b>症状としては「トリガーが永久に解決しない」だけ</b>に見える。数だけ数えておけば
    /// 未解決の理由として呼び出し元に返せる。
    /// </summary>
    public int InaccessibleProcessCount { get; private set; }

    /// <summary>
    /// Required 指定の属性をすべて満たすウィンドウを、スコア降順で返す。
    /// 同点の候補は列挙順 (= Z オーダー) を保つ。
    /// </summary>
    public IReadOnlyList<nint> GetCandidates(WindowIdentity identity)
    {
        ArgumentNullException.ThrowIfNull(identity);

        var key = IdentityKey.From(identity);
        if (_candidates.TryGetValue(key, out var cached))
        {
            return cached;
        }
        var computed = Compute(identity);
        _candidates.Add(key, computed);
        return computed;
    }

    /// <summary>
    /// Required なのに記録されていない属性があるか。
    /// この場合は「何も絞り込めない条件で全ウィンドウが候補になる」ことになるので、
    /// 開いた側 (全一致) ではなく閉じた側 (候補なし) に倒す。
    /// </summary>
    public static bool HasUnsatisfiableRequirement(WindowIdentity identity)
    {
        ArgumentNullException.ThrowIfNull(identity);
        return (identity.ProcessNameMatch == MatchStrength.Required && string.IsNullOrEmpty(identity.ProcessName))
            || (identity.ProcessPathMatch == MatchStrength.Required && string.IsNullOrEmpty(identity.ProcessPath))
            || (identity.ClassNameMatch == MatchStrength.Required && string.IsNullOrEmpty(identity.ClassName))
            || (identity.WindowNameMatch == MatchStrength.Required && string.IsNullOrEmpty(identity.WindowName));
    }

    private nint[] Compute(WindowIdentity identity)
    {
        if (HasUnsatisfiableRequirement(identity))
        {
            return [];
        }

        var scored = new List<(nint Hwnd, int Score, int Order)>();
        IReadOnlyList<WindowInfo> windows = GetWindows();
        for (int order = 0; order < windows.Count; order++)
        {
            WindowInfo window = windows[order];
            string? imagePath = GetImagePath(window.ProcessId);
            string? processName = imagePath is null ? null : Path.GetFileName(imagePath);

            int score = 0;
            if (!Apply(identity.ProcessNameMatch, identity.ProcessName, processName,
                    StringComparison.OrdinalIgnoreCase, options.WindowProcessNameScore, ref score) ||
                !Apply(identity.ProcessPathMatch, identity.ProcessPath, imagePath,
                    StringComparison.OrdinalIgnoreCase, options.WindowProcessPathScore, ref score) ||
                !Apply(identity.ClassNameMatch, identity.ClassName, window.ClassName,
                    StringComparison.Ordinal, options.WindowClassNameScore, ref score) ||
                !ApplyTitle(identity, window.Title, ref score))
            {
                continue;
            }

            scored.Add((window.Hwnd, score, order));
        }

        // List.Sort は不安定なので、同点時の順序を列挙順で明示的に固定する。
        // 同点候補の試行順が実行ごとに変わると、再現しない解決結果を生む
        scored.Sort(static (a, b) => a.Score == b.Score ? a.Order.CompareTo(b.Order) : b.Score.CompareTo(a.Score));

        int count = Math.Min(scored.Count, Math.Max(options.MaxWindowCandidates, 0));
        var result = new nint[count];
        for (int i = 0; i < count; i++)
        {
            result[i] = scored[i].Hwnd;
        }
        return result;
    }

    /// <summary>
    /// 1 属性の照合。Required なら不一致で候補を落とし (false)、Preferred なら加点のみ、
    /// Ignored なら何もしない。記録されていない (空の) 属性は Required でも足切りに使わない —
    /// 「記録できなかったもの」で解決不能にしないため。
    /// </summary>
    private static bool Apply(
        MatchStrength strength, string? expected, string? actual,
        StringComparison comparison, int score, ref int total)
    {
        if (strength == MatchStrength.Ignored || string.IsNullOrEmpty(expected))
        {
            return true;
        }
        bool matched = actual is not null && string.Equals(actual, expected, comparison);
        if (!matched)
        {
            return strength != MatchStrength.Required;
        }
        if (strength == MatchStrength.Preferred)
        {
            total += score;
        }
        return true;
    }

    /// <summary>タイトルだけは部分一致も加点する (末尾に "*" や文書名が付くアプリが多いため)。</summary>
    private bool ApplyTitle(WindowIdentity identity, string title, ref int total)
    {
        if (identity.WindowNameMatch == MatchStrength.Ignored || string.IsNullOrEmpty(identity.WindowName))
        {
            return true;
        }
        if (string.Equals(title, identity.WindowName, StringComparison.Ordinal))
        {
            if (identity.WindowNameMatch == MatchStrength.Preferred)
            {
                total += options.WindowNameExactScore;
            }
            return true;
        }
        if (identity.WindowNameMatch == MatchStrength.Required)
        {
            return false;
        }
        if (title.Length > 0 &&
            (title.Contains(identity.WindowName, StringComparison.Ordinal) ||
             identity.WindowName.Contains(title, StringComparison.Ordinal)))
        {
            total += options.WindowNamePartialScore;
        }
        return true;
    }

    private IReadOnlyList<WindowInfo> GetWindows()
    {
        if (_windows is null)
        {
            _windows = source.GetVisibleWindows();
            EnumerationCount++;
        }
        return _windows;
    }

    private string? GetImagePath(uint processId)
    {
        if (_imagePaths.TryGetValue(processId, out string? cached))
        {
            return cached;
        }
        string? path = source.GetProcessImagePath(processId);
        ImagePathLookupCount++;
        if (path is null)
        {
            InaccessibleProcessCount++;
        }
        _imagePaths.Add(processId, path);
        return path;
    }

    // 照合の強さもキーに含めること。同じ文字列でも Required と Preferred では候補集合が違うため、
    // 含め忘れると別条件のトリガー同士が同じ候補リストを共有してしまう
    private readonly record struct IdentityKey(
        string ProcessName, MatchStrength ProcessNameMatch,
        string ProcessPath, MatchStrength ProcessPathMatch,
        string ClassName, MatchStrength ClassNameMatch,
        string WindowName, MatchStrength WindowNameMatch)
    {
        public static IdentityKey From(WindowIdentity identity) => new(
            identity.ProcessName, identity.ProcessNameMatch,
            identity.ProcessPath ?? string.Empty, identity.ProcessPathMatch,
            identity.ClassName ?? string.Empty, identity.ClassNameMatch,
            identity.WindowName ?? string.Empty, identity.WindowNameMatch);
    }

    /// <summary>候補計算に使う比較規則と揃える (揃っていないと共有できるはずのものが分かれるだけ)。</summary>
    private sealed class IdentityKeyComparer : IEqualityComparer<IdentityKey>
    {
        public static readonly IdentityKeyComparer Instance = new();

        public bool Equals(IdentityKey x, IdentityKey y) =>
            string.Equals(x.ProcessName, y.ProcessName, StringComparison.OrdinalIgnoreCase) &&
            string.Equals(x.ProcessPath, y.ProcessPath, StringComparison.OrdinalIgnoreCase) &&
            string.Equals(x.ClassName, y.ClassName, StringComparison.Ordinal) &&
            string.Equals(x.WindowName, y.WindowName, StringComparison.Ordinal) &&
            x.ProcessNameMatch == y.ProcessNameMatch &&
            x.ProcessPathMatch == y.ProcessPathMatch &&
            x.ClassNameMatch == y.ClassNameMatch &&
            x.WindowNameMatch == y.WindowNameMatch;

        public int GetHashCode(IdentityKey obj) => HashCode.Combine(
            StringComparer.OrdinalIgnoreCase.GetHashCode(obj.ProcessName),
            StringComparer.OrdinalIgnoreCase.GetHashCode(obj.ProcessPath),
            StringComparer.Ordinal.GetHashCode(obj.ClassName),
            StringComparer.Ordinal.GetHashCode(obj.WindowName),
            obj.ProcessNameMatch,
            obj.ProcessPathMatch,
            obj.ClassNameMatch,
            obj.WindowNameMatch);
    }
}
