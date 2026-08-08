// 句を組み合わせるブール式 (docs/DESIGN.md §4)。
//
// 入れ子は「POCO を入れ子にする」のではなく「1 本の文字列を解析する」ことで得る。
// モデルは属性もコンバータも要らない素の POCO のままでいられ、STJ source-gen / AOT に
// 何の影響も無い。木はここで組んで永続化しない。
//
// ClauseEvaluator / SubscriptionHealth と同じく COM に一切触らない純ロジックなので、
// 構文と優先順位は実 UIA 無しで単体テストできる。
//
// 解析の時点で名前は句の添字に解決する。実行時に文字列を引き直す経路は残さない。
using System.Globalization;
using UiaTrigger.Models;
using UiaTrigger.Resources;

namespace UiaTrigger.Monitoring;

/// <summary>解析済みのブール式。葉は句の添字。</summary>
internal abstract class ClauseExpressionNode
{
    /// <param name="holds">句の添字を受け取り、その句が成立しているかを返す。</param>
    /// <remarks>
    /// 短絡する。<see cref="TriggerProperty.Custom"/> は評価のたびにクロスプロセス呼び出しが
    /// 1 回走るので、評価されない側を読まないことは費用に直結する。
    /// </remarks>
    public abstract bool Evaluate(Func<int, bool> holds);

    public sealed class Clause(int index) : ClauseExpressionNode
    {
        public override bool Evaluate(Func<int, bool> holds) => holds(index);
    }

    public sealed class Not(ClauseExpressionNode operand) : ClauseExpressionNode
    {
        public override bool Evaluate(Func<int, bool> holds) => !operand.Evaluate(holds);
    }

    public sealed class And(ClauseExpressionNode left, ClauseExpressionNode right) : ClauseExpressionNode
    {
        public override bool Evaluate(Func<int, bool> holds) =>
            left.Evaluate(holds) && right.Evaluate(holds);
    }

    public sealed class Or(ClauseExpressionNode left, ClauseExpressionNode right) : ClauseExpressionNode
    {
        public override bool Evaluate(Func<int, bool> holds) =>
            left.Evaluate(holds) || right.Evaluate(holds);
    }
}

/// <summary>解析の結果。</summary>
/// <param name="Root">解析できた木。失敗したときは null。</param>
/// <param name="Error">失敗の理由 (ローカライズ済み・位置つき)。成功したときは null。</param>
/// <param name="ReferencedIndices">
/// 式が参照した句の添字。「どの句も参照されていること」を呼び出し元が確かめるために返す。
/// </param>
internal readonly record struct ClauseExpressionResult(
    ClauseExpressionNode? Root, string? Error, IReadOnlyList<int> ReferencedIndices)
{
    public bool IsValid => Error is null;
}

internal static class ClauseExpression
{
    /// <summary>
    /// 入れ子の深さの上限。
    ///
    /// 再帰下降なので、括弧を深く重ねた入力はそのままスタックの深さになる。
    /// 正規表現に NonBacktracking を選んだのと同じ理由で、敵対的な入力を想定して頭打ちにする。
    /// 人が書く式がこの深さに達することはまず無い。
    /// </summary>
    public const int MaxDepth = 32;

    /// <summary>
    /// <see cref="PropertyClause.Name"/> が null のときに使う位置由来の名前。
    /// 1 始まりなのは、式を読む人にとって「1 つめの句」のほうが自然なため。
    /// </summary>
    public static string EffectiveName(PropertyClause clause, int index)
    {
        ArgumentNullException.ThrowIfNull(clause);
        return clause.Name ?? "c" + (index + 1).ToString(CultureInfo.InvariantCulture);
    }

    /// <summary>
    /// 式の中で名前として書ける文字か。
    /// 演算子・括弧・空白を名前に許すと、式が名前の途中で切れて別の意味に読めてしまう。
    /// </summary>
    /// <remarks>
    /// カンマも許さない。式の文法には要らないが、**名前の一覧はカンマ区切りで受け渡される**
    /// (エディタの「絞るだけ」欄。<see cref="Models.TriggerComposer.UnwatchedNames"/> が返す
    /// 一覧をそのまま <see cref="Models.TriggerComposer.Update"/> へ戻す経路 — C17 の恒等)。
    /// 名前にカンマが入ると、一覧が別の 2 つの名前として読み直され、恒等が UI 経由で静かに
    /// 破れる。区切りに使う文字は名前に許さない、が唯一ずれない解き方である。
    /// </remarks>
    public static bool IsNameChar(char c) =>
        !char.IsWhiteSpace(c) && c is not ('(' or ')' or '!' or '&' or '|' or ',');

    /// <summary>句の名前として使えるか。</summary>
    public static bool IsValidName(string? name)
    {
        if (string.IsNullOrEmpty(name))
        {
            return false;
        }
        foreach (char c in name)
        {
            if (!IsNameChar(c))
            {
                return false;
            }
        }
        return true;
    }

    /// <summary>
    /// 式を解析する。演算子は <c>&amp;&amp;</c> / <c>||</c> / <c>!</c> と括弧で、
    /// 優先順位は <c>!</c> &gt; <c>&amp;&amp;</c> &gt; <c>||</c>。二項演算子は左結合。
    /// </summary>
    /// <param name="text">式。</param>
    /// <param name="names">句の名前 (添字が句の位置に対応する)。序数比較で照合する。</param>
    public static ClauseExpressionResult Parse(string text, IReadOnlyList<string> names)
    {
        ArgumentNullException.ThrowIfNull(text);
        ArgumentNullException.ThrowIfNull(names);
        var parser = new Parser(text, names);
        return parser.ParseAll();
    }

    private sealed class Parser(string text, IReadOnlyList<string> names)
    {
        private readonly List<int> _referenced = [];
        private int _position;

        public ClauseExpressionResult ParseAll()
        {
            SkipWhitespace();
            if (_position >= text.Length)
            {
                return Fail(Strings.Error_ExpressionEmpty);
            }

            ClauseExpressionResult result = ParseOr(depth: 1);
            if (!result.IsValid)
            {
                return result;
            }

            SkipWhitespace();
            if (_position < text.Length)
            {
                // 余った文字にはよくある形が 2 つある。どちらも「予期しないトークン」で
                // 済ませられるが、それだと直し方が分からない
                return text[_position] switch
                {
                    ')' => Fail(Message.Format(Strings.Error_ExpressionUnbalancedParen, _position + 1)),
                    '&' or '|' => Fail(Message.Format(
                        Strings.Error_ExpressionSingleOperatorChar, _position + 1, text[_position])),
                    _ => Fail(Message.Format(
                        Strings.Error_ExpressionUnexpectedToken, _position + 1, ReadTokenText())),
                };
            }
            return new ClauseExpressionResult(result.Root, Error: null, _referenced);
        }

        private ClauseExpressionResult ParseOr(int depth)
        {
            ClauseExpressionResult left = ParseAnd(depth);
            while (left.IsValid)
            {
                SkipWhitespace();
                if (!TryTakeOperator('|'))
                {
                    break;
                }
                ClauseExpressionResult right = ParseAnd(depth);
                if (!right.IsValid)
                {
                    return right;
                }
                left = Ok(new ClauseExpressionNode.Or(left.Root!, right.Root!));
            }
            return left;
        }

        private ClauseExpressionResult ParseAnd(int depth)
        {
            ClauseExpressionResult left = ParseUnary(depth);
            while (left.IsValid)
            {
                SkipWhitespace();
                if (!TryTakeOperator('&'))
                {
                    break;
                }
                ClauseExpressionResult right = ParseUnary(depth);
                if (!right.IsValid)
                {
                    return right;
                }
                left = Ok(new ClauseExpressionNode.And(left.Root!, right.Root!));
            }
            return left;
        }

        private ClauseExpressionResult ParseUnary(int depth)
        {
            if (depth > MaxDepth)
            {
                return Fail(Message.Format(Strings.Error_ExpressionTooDeep, MaxDepth));
            }
            SkipWhitespace();
            if (_position >= text.Length)
            {
                return Fail(Strings.Error_ExpressionUnexpectedEnd);
            }

            if (text[_position] == '!')
            {
                _position++;
                ClauseExpressionResult operand = ParseUnary(depth + 1);
                return operand.IsValid ? Ok(new ClauseExpressionNode.Not(operand.Root!)) : operand;
            }

            if (text[_position] == '(')
            {
                int open = _position;
                _position++;
                ClauseExpressionResult inner = ParseOr(depth + 1);
                if (!inner.IsValid)
                {
                    return inner;
                }
                SkipWhitespace();
                if (_position >= text.Length || text[_position] != ')')
                {
                    return Fail(Message.Format(Strings.Error_ExpressionUnclosedParen, open + 1));
                }
                _position++;
                return inner;
            }

            return ParseName();
        }

        private ClauseExpressionResult ParseName()
        {
            int start = _position;
            // 単独の '&' / '|' はまず打ち間違いなので、名前として読んで
            // 「そんな句は無い」と言うより、書き方をそのまま示したほうが早い
            if (text[_position] is '&' or '|')
            {
                return Fail(Message.Format(
                    Strings.Error_ExpressionSingleOperatorChar, start + 1, text[_position]));
            }
            if (text[_position] == ')')
            {
                return Fail(Message.Format(Strings.Error_ExpressionUnbalancedParen, start + 1));
            }

            while (_position < text.Length && IsNameChar(text[_position]))
            {
                _position++;
            }
            string name = text[start.._position];
            for (int i = 0; i < names.Count; i++)
            {
                if (string.Equals(names[i], name, StringComparison.Ordinal))
                {
                    if (!_referenced.Contains(i))
                    {
                        _referenced.Add(i);
                    }
                    return Ok(new ClauseExpressionNode.Clause(i));
                }
            }
            return Fail(Message.Format(Strings.Error_ExpressionUnknownName, start + 1, name));
        }

        /// <summary>
        /// <c>&amp;&amp;</c> / <c>||</c> を 1 つ読む。1 文字しか無いときは読まずに戻る —
        /// そちらは <see cref="ParseName"/> が打ち間違いとして拾う。
        /// </summary>
        private bool TryTakeOperator(char c)
        {
            if (_position + 1 < text.Length && text[_position] == c && text[_position + 1] == c)
            {
                _position += 2;
                return true;
            }
            return false;
        }

        private void SkipWhitespace()
        {
            while (_position < text.Length && char.IsWhiteSpace(text[_position]))
            {
                _position++;
            }
        }

        /// <summary>エラー文面に載せるぶんだけトークンを読む (位置は進めない)。</summary>
        private string ReadTokenText()
        {
            int end = _position;
            while (end < text.Length && IsNameChar(text[end]))
            {
                end++;
            }
            return end == _position ? text[_position].ToString() : text[_position..end];
        }

        private ClauseExpressionResult Ok(ClauseExpressionNode node) =>
            new(node, Error: null, _referenced);

        private static ClauseExpressionResult Fail(string error) => new(Root: null, error, []);
    }
}
