using System.Globalization;
using UiaTrigger.Interop;
using UiaTrigger.Models;
using UiaTrigger.Resolution;

namespace UiaTrigger.Inspection;

/// <summary>選択された要素から永続化用の TriggerDefinition (経路+ウィンドウ識別) を構築する。</summary>
internal static class DefinitionBuilder
{
    private const int MaxSiblingScan = 2048;

    /// <summary>
    /// 要素からトップレベルウィンドウまで指定ビューで遡り、経路とウィンドウ識別情報を記録する。
    /// UiaDispatcher スレッド上で呼ぶこと。
    /// </summary>
    public static TriggerDefinition Build(UiaContext ctx, IUIAutomationElement element, TreeViewMode view)
    {
        ArgumentNullException.ThrowIfNull(ctx);
        // 呼び出し側で null を弾いておくこと。ここで chain が空になると chain[^1] が落ちる
        // (ElementFromPoint は null を返しうる — docs/DESIGN.md A15)
        ArgumentNullException.ThrowIfNull(element);

        var walker = ctx.GetWalker(view);

        // 指定ビューに正規化してから祖先チェーンを構築 (先頭=対象要素, 末尾=トップレベルウィンドウ)
        walker.NormalizeElement(element, out var normalized);
        var chain = new List<IUIAutomationElement>();
        IUIAutomationElement? current = normalized ?? element;
        while (current is not null)
        {
            walker.GetParentElement(current, out var parent);
            if (parent is null || ctx.AreSame(parent, ctx.Root))
            {
                chain.Add(current);
                break;
            }
            chain.Add(current);
            current = parent;
        }

        var topLevel = chain[^1];
        var targetSnapshot = PropertyReader.ReadSnapshot(ctx, chain[0]);
        var definition = new TriggerDefinition
        {
            Id = MakeId(targetSnapshot),
            Window = BuildWindowIdentity(topLevel),
            Locator = new ElementLocator { View = view },
        };

        // トップレベル直下 → 対象要素 の順に記録
        for (int i = chain.Count - 2; i >= 0; i--)
        {
            definition.Locator.Steps.Add(BuildStep(ctx, parent: chain[i + 1], child: chain[i], view));
        }

        // 経路とは別に、一意な AutomationId があれば Search 方式も記録する。
        // 解決はこちらを先に試すが、経路は「一意でなくなったとき」の退避路として残す
        definition.Locator.Search = TryRecordSearch(ctx, topLevel, chain[0], view, targetSnapshot.AutomationId);

        definition.DisplayName = BuildDisplayName(targetSnapshot);
        return definition;
    }

    /// <summary>
    /// 一覧に出す既定の表示名。**安定名から**組み立てる — DisplayName は永続化されるので、
    /// LocalizedControlType (相手アプリのロケール) を混ぜると、対象アプリを別の言語で
    /// 起動し直しただけで保存内容が変わる (docs/DESIGN.md L6)。
    /// </summary>
    internal static string BuildDisplayName(ElementPropertySnapshot targetSnapshot) =>
        string.IsNullOrEmpty(targetSnapshot.Name)
            ? targetSnapshot.ControlTypeName
            : $"{targetSnapshot.ControlTypeName}: {targetSnapshot.Name}";

    /// <summary>
    /// 対象の <c>AutomationId</c> がウィンドウ内で一意なら Search 方式を記録する。
    ///
    /// 一意性は **記録時に実際に数えて** 確かめる。「一意だろう」で記録すると、
    /// 解決時に黙って別の要素を掴む定義が出来上がる — 静かに間違う類の壊れ方であり、
    /// 記録側でしか防げない (docs/DESIGN.md §3 の Search 方式)。
    /// </summary>
    private static ElementSearch? TryRecordSearch(
        UiaContext ctx, IUIAutomationElement topLevel, IUIAutomationElement target, TreeViewMode view, string? automationId)
    {
        if (string.IsNullOrEmpty(automationId))
        {
            return null;
        }

        // 上限 2 件。「1 件だけか」を知りたいだけなので全件は要らない
        IReadOnlyList<IElementNode> matches = ctx.Tree.FindByAutomationId(
            UiaElementNode.ForNavigation(topLevel), view, automationId, max: 2);
        try
        {
            return matches.Count == 1 && ctx.AreSame(UiaElementNode.Unwrap(matches[0]), target)
                ? new ElementSearch { AutomationId = automationId }
                : null;
        }
        finally
        {
            foreach (IElementNode match in matches)
            {
                ctx.Tree.Release(match);
            }
        }
    }

    /// <summary>
    /// Id の初期値。ホストが後から付け替える前提の「とりあえず一意」な値であり、
    /// キーは永続化される識別子なので必ず InvariantCulture で組み立てる。
    /// </summary>
    private static string MakeId(ElementPropertySnapshot snapshot)
    {
        string slug = !string.IsNullOrEmpty(snapshot.AutomationId) ? snapshot.AutomationId
            : !string.IsNullOrEmpty(snapshot.Name) ? snapshot.Name
            : snapshot.ControlTypeName;
        string suffix = Guid.NewGuid().ToString("N", CultureInfo.InvariantCulture)[..8];
        return $"{Sanitize(slug)}-{suffix}";
    }

    private static string Sanitize(string value)
    {
        Span<char> buffer = stackalloc char[Math.Min(value.Length, 24)];
        int length = 0;
        foreach (char c in value)
        {
            if (length == buffer.Length)
            {
                break;
            }
            buffer[length++] = char.IsAsciiLetterOrDigit(c) ? char.ToLowerInvariant(c) : '-';
        }
        return length == 0 ? "element" : new string(buffer[..length]);
    }

    private static WindowIdentity BuildWindowIdentity(IUIAutomationElement topLevel)
    {
        topLevel.get_CurrentProcessId(out int pid);
        topLevel.get_CurrentNativeWindowHandle(out nint hwnd);

        string? className = null;
        string? windowName = null;
        if (hwnd != 0)
        {
            className = NativeMethods.GetClassName(hwnd);
            windowName = NativeMethods.GetWindowText(hwnd);
        }
        if (string.IsNullOrEmpty(className))
        {
            topLevel.get_CurrentClassName(out string uiaClass);
            className = uiaClass;
        }
        if (string.IsNullOrEmpty(windowName))
        {
            topLevel.get_CurrentName(out string uiaName);
            windowName = uiaName;
        }

        string? imagePath = NativeMethods.GetProcessImagePath((uint)pid);
        // MatchStrength は既定のまま (ProcessName=Required / ClassName・ProcessPath=Preferred /
        // WindowName=Ignored)。タイトルは記録するが照合には使わない — 使うと
        // 「文書名が変わっただけで解決不能」になるため (docs/DESIGN.md A4)
        return new WindowIdentity
        {
            ProcessName = imagePath is null ? string.Empty : Path.GetFileName(imagePath),
            ProcessPath = imagePath,
            ClassName = className,
            WindowName = windowName,
        };
    }

    /// <summary>
    /// 1 段分を記録する。
    ///
    /// 兄弟インデックスを素朴に兄弟 1 個ずつの CompareElements で特定すると、最大 2048 回の
    /// クロスプロセス往復になる (docs/DESIGN.md B9)。ここでは
    /// (a) 兄弟を CacheRequest で 1 往復にまとめ、
    /// (b) キャッシュ済みの ControlType/AutomationId/Name が一致した候補にだけ CompareElements を掛ける。
    /// 通常は CompareElements が 1 回で済む。
    ///
    /// 見つからなかった場合は 0 ではなく <see cref="ElementPathStep.UnknownSiblingIndex"/> を記録する。
    /// 0 を記録すると「先頭にあった」という誤った手掛かりが永続化され、以後の解決の
    /// タイブレークを静かに狂わせる (docs/DESIGN.md A7)。
    /// </summary>
    private static ElementPathStep BuildStep(UiaContext ctx, IUIAutomationElement parent, IUIAutomationElement child, TreeViewMode view)
    {
        child.get_CurrentControlType(out int controlType);
        child.get_CurrentAutomationId(out string automationId);
        child.get_CurrentName(out string name);
        child.get_CurrentClassName(out string className);

        int siblingIndex = ElementPathStep.UnknownSiblingIndex;
        var siblings = ctx.Tree.GetChildren(UiaElementNode.ForNavigation(parent), view, MaxSiblingScan);
        try
        {
            for (int i = 0; i < siblings.Count; i++)
            {
                IElementNode sibling = siblings[i];
                if (sibling.ControlType != controlType ||
                    !string.Equals(sibling.AutomationId, automationId, StringComparison.Ordinal) ||
                    !string.Equals(sibling.Name, name, StringComparison.Ordinal))
                {
                    continue;
                }
                if (ctx.AreSame(UiaElementNode.Unwrap(sibling), child))
                {
                    siblingIndex = i;
                    break;
                }
            }
        }
        finally
        {
            foreach (IElementNode sibling in siblings)
            {
                ctx.Tree.Release(sibling);
            }
        }

        return new ElementPathStep
        {
            ControlType = controlType,
            AutomationId = string.IsNullOrEmpty(automationId) ? null : automationId,
            Name = string.IsNullOrEmpty(name) ? null : name,
            // Win32 では極めて安定した識別子。AutomationId も Name も無い段
            // (汎用 Pane/Group) では唯一の手掛かりになることがある
            ClassName = string.IsNullOrEmpty(className) ? null : className,
            SiblingIndex = siblingIndex,
        };
    }
}
