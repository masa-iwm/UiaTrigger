using UiaTrigger.Models;
using UiaTrigger.Monitoring;
using Xunit;

namespace UiaTrigger.Tests;

/// <summary>
/// 定義の検証が **呼び出し元スレッドで** 起き、すべて <see cref="ArgumentException"/> として
/// 出ること。
///
/// 検証は UIA へ何も投げる前に済むので、この一群は実 UIA を必要としない。
/// ピッカー側の入力検証 (<see cref="TriggerDraftValidatorTests"/>) と対になっており、
/// 「ピッカーで確定できたのに監視開始で落ちる定義」が作れないことを両側から縛る。
/// </summary>
[Collection(RealUiaLiteTests.Name)]
public sealed class TriggerMonitorValidationTests
{
    private static TriggerDefinition Definition(PropertyClause clause) => new()
    {
        Id = "t",
        Window = new WindowIdentity { ProcessName = "notepad.exe" },
        On = TriggerOn.PropertyChanged,
        Clauses = [clause],
    };

    private static async Task<Exception?> StartAsync(TriggerDefinition definition)
    {
        await using var monitor = new TriggerMonitor();
        return await Record.ExceptionAsync(() => monitor.StartAsync([definition]));
    }

    [Fact]
    public async Task StartAsync_WithMalformedRegex_ThrowsArgumentException()
    {
        Exception? error = await StartAsync(Definition(
            new PropertyClause { Property = TriggerProperty.Name, Op = ComparisonOp.RegexMatch, Text = "[unclosed" }));

        Assert.IsType<ArgumentException>(error);
    }

    /// <summary>
    /// 後読みは文法としては正しいが <c>NonBacktracking</c> では使えず、
    /// <see cref="NotSupportedException"/> になる。これを捕まえ損ねると、
    /// 定義の誤りがローカライズされない別種の例外として呼び出し元へ漏れる。
    /// </summary>
    [Theory]
    [InlineData("(?<=a)b")]     // 後読み
    [InlineData("(?=a)b")]      // 先読み
    [InlineData(@"(a)\1")]      // 後方参照
    public async Task StartAsync_WithARegexNonBacktrackingCannotCompile_ThrowsArgumentException(string pattern)
    {
        Exception? error = await StartAsync(Definition(
            new PropertyClause { Property = TriggerProperty.Name, Op = ComparisonOp.RegexMatch, Text = pattern }));

        Assert.IsType<ArgumentException>(error);
        Assert.Contains("t", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task StartAsync_WithAValidRegex_DoesNotThrowOnValidation()
    {
        // 解決には失敗しうるが、検証で弾かれないことだけを見る
        Exception? error = await StartAsync(Definition(
            new PropertyClause { Property = TriggerProperty.Name, Op = ComparisonOp.RegexMatch, Text = "^ready$" }));

        Assert.Null(error);
    }

    // ---------- 複合条件 (docs/DESIGN.md §4) ----------

    private static PropertyClause Clause(string? name = null, bool watch = true) => new()
    {
        Name = name,
        Property = TriggerProperty.Name,
        Op = ComparisonOp.Equals,
        Text = "x",
        Watch = watch,
    };

    private static TriggerDefinition Composite(string? expression, params PropertyClause[] clauses) => new()
    {
        Id = "t",
        Window = new WindowIdentity { ProcessName = "notepad.exe" },
        On = TriggerOn.PropertyChanged,
        Expression = expression,
        Clauses = [.. clauses],
    };

    private static async Task<string> ErrorOf(TriggerDefinition definition)
    {
        Exception? error = await StartAsync(definition);
        ArgumentException typed = Assert.IsType<ArgumentException>(error);
        return typed.Message;
    }

    [Theory]
    [InlineData("a && ")]
    [InlineData("(a")]
    [InlineData("a & b")]
    public async Task StartAsync_WithAMalformedExpression_ThrowsArgumentException(string expression)
    {
        string message = await ErrorOf(Composite(expression, Clause("a"), Clause("b")));

        Assert.Contains("t", message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task StartAsync_WithAnExpressionNamingAnUnknownClause_ThrowsArgumentException()
    {
        string message = await ErrorOf(Composite("a && zzz", Clause("a"), Clause("b")));

        Assert.Contains("zzz", message, StringComparison.Ordinal);
    }

    /// <summary>
    /// 式が参照しない句は、解決も購読もされるのに結果に一切効かない。
    /// 黙って通すと「なぜか条件が効かない」として最も長く隠れる。
    /// </summary>
    [Fact]
    public async Task StartAsync_WithAClauseTheExpressionLeavesOut_ThrowsArgumentException()
    {
        string message = await ErrorOf(Composite("a", Clause("a"), Clause("unused")));

        Assert.Contains("unused", message, StringComparison.Ordinal);
    }

    /// <summary>
    /// 名前の重複は、式を書いていないトリガーでも弾く。あとから式を足したときに
    /// 「どちらの句を指しているのか」が決まらない定義が、すでに保存済みになっている状態を作らない。
    /// </summary>
    [Fact]
    public async Task StartAsync_WithDuplicateClauseNames_ThrowsEvenWithoutAnExpression()
    {
        string message = await ErrorOf(Composite(expression: null, Clause("same"), Clause("same")));

        Assert.Contains("same", message, StringComparison.Ordinal);
    }

    /// <summary>名前を付けなかった句に与えられる位置由来の名前も、重複の対象である。</summary>
    [Fact]
    public async Task StartAsync_WhenAnExplicitNameCollidesWithAPositionalOne_Throws()
    {
        // 2 つめの句は名前が無いので "c2" になり、1 つめの明示名とぶつかる
        string message = await ErrorOf(Composite(expression: null, Clause("c2"), Clause()));

        Assert.Contains("c2", message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("")]
    [InlineData("has space")]
    [InlineData("a&b")]
    [InlineData("(a)")]
    public async Task StartAsync_WithAnUnusableClauseName_ThrowsArgumentException(string name)
    {
        Exception? error = await StartAsync(Composite(expression: null, Clause(name)));

        Assert.IsType<ArgumentException>(error);
    }

    /// <summary>
    /// 全句が <see cref="PropertyClause.Watch"/> = false だと購読対象が 1 つも無く、
    /// 永久に発火しないトリガーになる。句が 0 個のときとまったく同じ形なので、同じ扱いにする。
    /// </summary>
    [Theory]
    [InlineData(TriggerOn.PropertyChanged)]
    [InlineData(TriggerOn.WhileMatching)]
    public async Task StartAsync_WithNoWatchedClause_ThrowsArgumentException(TriggerOn on)
    {
        TriggerDefinition definition = Composite(expression: null, Clause("a", watch: false));
        definition.On = on;

        Assert.IsType<ArgumentException>(await StartAsync(definition));
    }

    /// <summary>
    /// 出現・削除だけを見るトリガーは今日どおり句が 0 個でよく、
    /// 監視しない句だけを持つこともできる (要素の存在を要求するのに使う)。
    /// </summary>
    [Theory]
    [InlineData(TriggerOn.ElementAppeared)]
    [InlineData(TriggerOn.ElementRemoved)]
    public async Task StartAsync_WithOnlyUnwatchedClauses_IsAllowedForLifecycleTriggers(TriggerOn on)
    {
        TriggerDefinition definition = Composite(expression: null, Clause("a", watch: false));
        definition.On = on;

        Assert.Null(await StartAsync(definition));
    }

    [Fact]
    public async Task StartAsync_WithAValidExpression_DoesNotThrowOnValidation()
    {
        Assert.Null(await StartAsync(Composite(
            "(a || b) && !c", Clause("a"), Clause("b"), Clause("c", watch: false))));
    }

    /// <summary>空白だけの式は「式なし」として扱う (UI の空欄がそのまま例外にならないこと)。</summary>
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public async Task StartAsync_WithNoExpression_FallsBackToCombine(string? expression)
    {
        Assert.Null(await StartAsync(Composite(expression, Clause("a"), Clause("b"))));
    }

    // ---------- 出現・消滅の対象は 1 つに決まっていること (docs/DESIGN.md §4) ----------

    /// <summary>
    /// 出現・消滅で鳴るトリガーの句は、自前の要素を名指せないこと。
    ///
    /// <para>
    /// **「消えたのは誰か」と「イベントに載る値は誰のものか」が食い違う組み合わせである。**
    /// 出現・消滅の対象は定義の要素 (スロット 0) と決まっているのに、イベントの値は
    /// 先頭の句の要素から読む。句が別の要素を名指すとその 2 つが別物になり、
    /// **どちらを指しているのか言えないイベント**になる。
    /// </para>
    /// <para>
    /// <c>PollInterval</c> を同じ <c>On</c> で弾くのと同じ規律である —
    /// 黙って意味が変わる設定を残さない。
    /// </para>
    /// </summary>
    [Theory]
    [InlineData(TriggerOn.ElementAppeared)]
    [InlineData(TriggerOn.ElementRemoved)]
    public async Task StartAsync_WhenALifecycleTriggersClauseNamesItsOwnElement_Throws(TriggerOn on)
    {
        TriggerDefinition withWindow = Composite(expression: null, Clause("a"));
        withWindow.On = on;
        withWindow.Clauses[0].Window = new WindowIdentity { ProcessName = "other.exe" };
        Assert.Contains(on.ToString(), await ErrorOf(withWindow), StringComparison.Ordinal);

        TriggerDefinition withLocator = Composite(expression: null, Clause("a"));
        withLocator.On = on;
        withLocator.Clauses[0].Locator = new ElementLocator();
        Assert.Contains(on.ToString(), await ErrorOf(withLocator), StringComparison.Ordinal);
    }

    /// <summary>
    /// 対照: 同じ句でも <c>On</c> が値を見るものなら通ること。
    /// **多要素そのものを禁じたのではない** — 禁じたのは出現・消滅との組み合わせだけである。
    /// </summary>
    [Theory]
    [InlineData(TriggerOn.PropertyChanged)]
    [InlineData(TriggerOn.WhileMatching)]
    public async Task StartAsync_AClauseMayNameItsOwnElement_WhenTheTriggerWatchesValues(TriggerOn on)
    {
        TriggerDefinition definition = Composite(expression: null, Clause("a"), Clause("b"));
        definition.On = on;
        definition.Clauses[1].Window = new WindowIdentity { ProcessName = "other.exe" };

        Assert.Null(await StartAsync(definition));
    }
}
