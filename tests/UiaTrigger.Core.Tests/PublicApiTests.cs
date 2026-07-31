using System.Reflection;
using System.Runtime.CompilerServices;
using UiaTrigger.Monitoring;
using Xunit;

namespace UiaTrigger.Tests;

/// <summary>
/// 公開 API の形を固定する (docs/DESIGN.md §3 / C7 / §12)。
///
/// 「Picker が公開 API だけで書ける」「COM 型が漏れていない」は、
/// どちらも <b>足し戻せば静かに元に戻る</b> 類の性質である。ここで縛る。
/// </summary>
public sealed class PublicApiTests
{
    private static readonly Assembly Core = typeof(UiaSession).Assembly;

    /// <summary>
    /// InternalsVisibleTo はテストアセンブリ 2 つだけであること。
    ///
    /// 製品アセンブリ (Picker / TestHost) への公開は無い。ここに製品アセンブリを
    /// 足し戻すのは「公開 API が足りないので内部を覗く」に戻ることであり、それは
    /// 第三者が同じことをできない = ライブラリとして使えない状態そのものである (docs/DESIGN.md §12)。
    /// </summary>
    [Fact]
    public void InternalsAreVisibleOnlyToTheTestAssemblies()
    {
        IEnumerable<string> visibleTo = Core
            .GetCustomAttributes<InternalsVisibleToAttribute>()
            .Select(a => a.AssemblyName)
            .Order(StringComparer.Ordinal);

        Assert.Equal(["UiaTrigger.Core.Tests", "UiaTrigger.RealUia.Tests"], visibleTo);
    }

    /// <summary>
    /// 公開型のシグネチャに COM interop 型が現れないこと。
    ///
    /// <see cref="UiaElement"/> が不透明ハンドルである意味は「COM 型を公開しない」ことに尽きる。
    /// 1 つでも漏れると、利用者は <c>UiaTrigger.Interop</c> の型を掴めるようになり、
    /// interop の変更が破壊的変更に変わる。
    /// </summary>
    [Fact]
    public void NoPublicMemberExposesAnInteropType()
    {
        var leaks = new List<string>();
        foreach (Type type in Core.GetExportedTypes())
        {
            foreach (MethodInfo method in type.GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly))
            {
                Check(leaks, $"{type.Name}.{method.Name} (return)", method.ReturnType);
                foreach (ParameterInfo parameter in method.GetParameters())
                {
                    Check(leaks, $"{type.Name}.{method.Name}({parameter.Name})", parameter.ParameterType);
                }
            }
            foreach (PropertyInfo property in type.GetProperties(BindingFlags.Public | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly))
            {
                Check(leaks, $"{type.Name}.{property.Name}", property.PropertyType);
            }
        }

        Assert.Empty(leaks);

        static void Check(List<string> leaks, string where, Type type)
        {
            foreach (Type part in Unwrap(type))
            {
                if (part.Namespace?.StartsWith("UiaTrigger.Interop", StringComparison.Ordinal) == true ||
                    part.Namespace?.StartsWith("UiaTrigger.Threading", StringComparison.Ordinal) == true)
                {
                    leaks.Add($"{where}: {part.FullName}");
                }
            }
        }

        // Task<T> / IReadOnlyList<T> の中身まで見る (T が漏れていては意味がない)
        static IEnumerable<Type> Unwrap(Type type)
        {
            Type element = type.IsByRef || type.IsArray ? type.GetElementType() ?? type : type;
            yield return element;
            foreach (Type argument in element.IsGenericType ? element.GetGenericArguments() : [])
            {
                foreach (Type nested in Unwrap(argument))
                {
                    yield return nested;
                }
            }
        }
    }

    /// <summary>
    /// C7: 名前空間ごとに「公開してよいもの」を決めたことの固定。
    ///
    /// <c>UiaDispatcher</c> だけが public で <c>UiaContext</c> は internal、
    /// <c>UiaControlTypeNames</c> が <c>UiaTrigger.Interop</c> に居るような形は、
    /// 「単体では何もできない実装詳細が公開され、モデルの語彙が interop に混ざる」状態である。
    /// </summary>
    [Fact]
    public void InteropAndThreadingNamespacesExportNothing()
    {
        IEnumerable<string> exported = Core.GetExportedTypes()
            .Where(t => t.Namespace is "UiaTrigger.Interop" or "UiaTrigger.Threading")
            .Select(t => t.FullName!);

        Assert.Empty(exported);
    }

    /// <summary>
    /// ピッカーを公開 API だけで書けること。実際に Picker から使うメンバーが揃っているかを
    /// 名前で押さえる (Picker は WinUI3 / x64 なのでこのテストからは参照できない — docs/DESIGN.md §12)。
    /// </summary>
    [Theory]
    [InlineData(nameof(UiaSession.ElementFromPointAsync))]
    [InlineData(nameof(UiaSession.ElementFromCursorAsync))]
    [InlineData(nameof(UiaSession.GetRootAsync))]
    [InlineData(nameof(UiaSession.GetChildrenAsync))]
    [InlineData(nameof(UiaSession.GetAncestorChainAsync))]
    [InlineData(nameof(UiaSession.GetOverlapStackAsync))]
    [InlineData(nameof(UiaSession.IndexOfAsync))]
    [InlineData(nameof(UiaSession.AreSameAsync))]
    [InlineData(nameof(UiaSession.ReadSnapshotAsync))]
    [InlineData(nameof(UiaSession.BuildDefinitionAsync))]
    [InlineData(nameof(UiaSession.BuildDefinitionFromPointAsync))]
    [InlineData(nameof(UiaSession.BuildDefinitionFromCursorAsync))]
    [InlineData(nameof(UiaSession.CreateMonitor))]
    public void UiaSession_ExposesWhatAPickerNeeds(string memberName)
    {
        Assert.NotNull(typeof(UiaSession).GetMethod(memberName));
    }

    /// <summary>
    /// 診断カウンタは公開 API であること (docs/DESIGN.md §3 の判断)。
    /// internal + InternalsVisibleTo に戻すと、購読スコープの検証が
    /// 「テストのためだけに本体へ穴を空ける」形に逆戻りする。
    /// </summary>
    [Fact]
    public void TriggerMonitor_ExposesItsDiagnostics()
    {
        MethodInfo? method = typeof(TriggerMonitor).GetMethod(nameof(TriggerMonitor.GetDiagnostics));

        Assert.NotNull(method);
        Assert.Equal(typeof(TriggerMonitorDiagnostics), method.ReturnType);
        Assert.Contains(typeof(TriggerMonitorDiagnostics), Core.GetExportedTypes());
    }

    /// <summary>
    /// public にすると決めた型が実際に公開されていること (docs/DESIGN.md §3 / §4)。
    /// </summary>
    [Theory]
    [InlineData("UiaTrigger.UiaSession")]
    [InlineData("UiaTrigger.UiaSessionOptions")]
    [InlineData("UiaTrigger.UiaElement")]
    [InlineData("UiaTrigger.DpiAwareness")]
    [InlineData("UiaTrigger.UiaControlTypeNames")]
    [InlineData("UiaTrigger.Models.TriggerDraftValidator")]
    [InlineData("UiaTrigger.Models.TriggerDraft")]
    // 複合条件を組む・ほどく規則 (docs/DESIGN.md §4) も TriggerDraftValidator と同じ理由で公開
    [InlineData("UiaTrigger.Models.TriggerComposer")]
    [InlineData("UiaTrigger.Models.TriggerCompositionResult")]
    [InlineData("UiaTrigger.Models.ElementSearch")]
    [InlineData("UiaTrigger.Monitoring.TriggerMonitorDiagnostics")]
    // 句ごとの値と行き先 (docs/DESIGN.md §4)。複合条件で他の句が読めるようになる唯一の口
    [InlineData("UiaTrigger.Monitoring.ClauseReading")]
    [InlineData("UiaTrigger.Monitoring.ClauseOutcome")]
    public void TypesPromotedInPhase4Arepublic(string fullName)
    {
        Type? type = Core.GetType(fullName);

        Assert.NotNull(type);
        Assert.True(type.IsPublic, $"{fullName} は public であるべきです。");
    }
}
