namespace Masque.Core.Query;

/// <summary>
/// The masked-column rules. A masked column may be: selected as a whole column; compared with =, &lt;&gt;, IN (...)
/// or IS [NOT] NULL against a literal, parameter, NULL or another masked column (in WHERE, ON, HAVING or a CASE WHEN);
/// grouped; counted; or written, as an UPDATE's SET target or an INSERT column, with a single supplied value. Anything
/// else could reveal part of a value (sorting, patterns, ranges, functions, arithmetic) or copy it somewhere unmasked,
/// so it is refused.
/// </summary>
internal sealed partial class SqlWalker
{
    private static readonly HashSet<string> EqualityOperators = ["=", "==", "<>", "!="];

    private static readonly HashSet<string> PatternWords = new(StringComparer.OrdinalIgnoreCase)
    {
        "LIKE", "ILIKE", "GLOB", "REGEXP", "MATCH", "SIMILAR",
    };

    private static readonly HashSet<string> RangeOperators = ["<", ">", "<=", ">="];

    /// <summary>Words that can come right before a comparison.</summary>
    private static readonly HashSet<string> ConditionStarts = new(StringComparer.OrdinalIgnoreCase)
    {
        "WHERE", "ON", "HAVING", "AND", "OR", "NOT", "WHEN",
    };

    /// <summary>Words that can come right after a comparison.</summary>
    private static readonly HashSet<string> ConditionEnds = new(StringComparer.OrdinalIgnoreCase)
    {
        "AND", "OR", "THEN", "GROUP", "ORDER", "HAVING", "LIMIT", "OFFSET", "FETCH", "WINDOW", "WHERE",
        "JOIN", "INNER", "LEFT", "RIGHT", "FULL", "CROSS", "NATURAL", "OUTER", "APPLY",
    };

    /// <summary>Words that can come right after a GROUP BY item.</summary>
    private static readonly HashSet<string> GroupItemEnds = new(StringComparer.OrdinalIgnoreCase)
    {
        "HAVING", "ORDER", "LIMIT", "OFFSET", "FETCH", "WINDOW",
    };

    /// <summary>Handles a name: a function, an alias, an unmasked column (ignored) or a masked column (checked).</summary>
    private int OnName(int i)
    {
        var end = ChainEnd(_t, i);

        if (end + 1 < _t.Count && _t[end + 1].IsSymbol("("))
        {
            return end + 1; // A function's name; the parenthesis opens its scope.
        }

        if (i > 0 && _t[i - 1].IsWord("AS"))
        {
            return end + 1; // An alias being defined, or a type in CAST(x AS type).
        }

        var column = _t[end].Text;
        return _masked.TryGetValue(column, out var type) ? CheckMasked(i, end, column, type) : end + 1;
    }

    /// <summary>Allows a masked column reference at <paramref name="start"/>..<paramref name="end"/> or refuses it.</summary>
    private int CheckMasked(int start, int end, string column, SensitiveDataType type)
    {
        var scope = Current;
        var effective = scope.Effective;

        // An UPDATE's SET target: the caller may write it, with a single value they supply.
        if (scope.Kind == ScopeKind.Root && scope.Clause == Clause.Set && start == scope.ItemStart
            && end + 1 < _t.Count && _t[end + 1].IsSymbol("="))
        {
            var value = end + 2;
            if (!IsSingleValue(value, value) || !IsSetItemEnd(value + 1))
            {
                throw Refuse(column, SingleValueRule);
            }

            RecordWritten(value, type);
            return value + 1;
        }

        // A whole column in a select list.
        if (scope.Kind is ScopeKind.Root or ScopeKind.Cte or ScopeKind.Derived or ScopeKind.Exists
            && scope.Clause == Clause.Select && start == scope.ItemStart)
        {
            if (scope.InWriteSource)
            {
                throw Refuse(column, "can't be copied into another table or column");
            }

            var after = SelectItemEnd(end + 1);
            if (after >= 0)
            {
                scope.SelectsStarOrMasked = true;
                return after;
            }
        }

        // A GROUP BY item.
        if (scope.IsQuery && scope.Clause == Clause.GroupBy
            && (_t[start - 1].IsWord("BY") || _t[start - 1].IsSymbol(","))
            && (end + 1 >= _t.Count || _t[end + 1].IsSymbol(",") || _t[end + 1].IsSymbol(")") || IsWordIn(_t[end + 1], GroupItemEnds)))
        {
            return end + 1;
        }

        // COUNT(col) or COUNT(DISTINCT col).
        if (scope.Kind == ScopeKind.Function && string.Equals(scope.FunctionName, "COUNT", StringComparison.OrdinalIgnoreCase)
            && end + 1 == _match[scope.Open]
            && (start - 1 == scope.Open || (start - 2 == scope.Open && _t[start - 1].IsWord("DISTINCT"))))
        {
            return end + 1;
        }

        var compared = TryComparison(start, end, column, type, effective);
        return compared >= 0 ? compared : throw Refuse(column, Reason(scope, effective, start, end));
    }

    /// <summary>The index after a select item's optional alias, or -1 when the item doesn't end there.</summary>
    private int SelectItemEnd(int k)
    {
        if (IsSelectItemBoundary(k))
        {
            return k;
        }

        if (k < _t.Count && _t[k].IsWord("AS"))
        {
            return k + 1 < _t.Count && SqlKeywords.IsName(_t[k + 1]) && IsSelectItemBoundary(k + 2) ? k + 2 : -1;
        }

        return k < _t.Count && SqlKeywords.IsName(_t[k]) && IsSelectItemBoundary(k + 1) ? k + 1 : -1;
    }

    private bool IsSelectItemBoundary(int k) =>
        k >= _t.Count || _t[k].IsSymbol(",") || _t[k].IsSymbol(")") || _t[k].IsWord("FROM");

    /// <summary>
    /// Allows <c>col OP value</c>, <c>value OP col</c>, <c>col IS [NOT] NULL</c> and <c>col [NOT] IN (values)</c>
    /// as a whole condition, and records string literals as token filters.
    /// </summary>
    /// <returns>The index after the condition, or -1 when it isn't one of those.</returns>
    private int TryComparison(int start, int end, string column, SensitiveDataType type, Scope effective)
    {
        if (!(effective.Clause is Clause.Where or Clause.On or Clause.Having || effective.InWhen))
        {
            return -1;
        }

        var literals = new List<int>();

        // Masked column on the left.
        if (IsConditionStart(start - 1))
        {
            var last = ReadRightSide(end + 1, column, literals);
            if (last >= 0 && IsConditionEnd(last + 1, effective))
            {
                // After = or <> the one literal is the whole right side; after IN the literals are list items.
                var comparison = IsEqualityOperator(_t[end + 1]) ? Comparison(start, last, start, end, _t[end + 1]) : null;
                AddFilters(literals, type, comparison);
                return last + 1;
            }

            literals.Clear();
        }

        // Masked column on the right of = or <>.
        if (start >= 2 && IsEqualityOperator(_t[start - 1]))
        {
            var first = ReadLeftOperand(start - 2, literals);
            if (first >= 0 && IsConditionStart(first - 1) && IsConditionEnd(end + 1, effective))
            {
                AddFilters(literals, type, Comparison(first, end, start, end, _t[start - 1]));
                return end + 1;
            }
        }

        return -1;
    }

    /// <summary>Describes an equality condition by character positions: the whole condition and its masked column.</summary>
    /// <param name="first">The condition's first token.</param>
    /// <param name="last">The condition's last token.</param>
    /// <param name="columnFirst">The masked column's first token (its qualifier, if any).</param>
    /// <param name="columnLast">The masked column's last token.</param>
    /// <param name="op">The equality operator.</param>
    private TokenComparison Comparison(int first, int last, int columnFirst, int columnLast, SqlToken op)
    {
        var start = _t[first].Start;
        var columnStart = _t[columnFirst].Start;
        return new TokenComparison(
            start,
            _t[last].Start + _t[last].Length - start,
            columnStart,
            _t[columnLast].Start + _t[columnLast].Length - columnStart,
            Negated: op.Text is "<>" or "!=");
    }

    /// <summary>Reads the operator and right side after a masked column; returns the condition's last index or -1.</summary>
    private int ReadRightSide(int j, string column, List<int> literals)
    {
        if (j >= _t.Count)
        {
            return -1;
        }

        if (IsEqualityOperator(_t[j]))
        {
            return ReadRightOperand(j + 1, literals);
        }

        if (_t[j].IsWord("IS"))
        {
            var k = _t.Count > j + 1 && _t[j + 1].IsWord("NOT") ? j + 2 : j + 1;
            return k < _t.Count && _t[k].IsWord("NULL") ? k : -1;
        }

        var inAt = _t[j].IsWord("NOT") ? j + 1 : j;
        return inAt < _t.Count && _t[inAt].IsWord("IN") ? ReadInList(inAt + 1, column, literals) : -1;
    }

    /// <summary>A value, or another masked column, after an equality operator; returns its last index or -1.</summary>
    private int ReadRightOperand(int k, List<int> literals)
    {
        if (k >= _t.Count)
        {
            return -1;
        }

        if (IsValue(_t[k]))
        {
            literals.Add(k);
            return k;
        }

        if (!SqlKeywords.IsName(_t[k]))
        {
            return -1;
        }

        var last = ChainEnd(_t, k);
        var isFunction = last + 1 < _t.Count && _t[last + 1].IsSymbol("(");
        return !isFunction && _masked.ContainsKey(_t[last].Text) ? last : -1;
    }

    /// <summary>A value, or another masked column, ending at <paramref name="k"/>; returns its first index or -1.</summary>
    private int ReadLeftOperand(int k, List<int> literals)
    {
        if (IsValue(_t[k]))
        {
            literals.Add(k);
            return k;
        }

        if (_t[k].Kind is not (SqlTokenKind.Word or SqlTokenKind.QuotedName) || !_masked.ContainsKey(_t[k].Text))
        {
            return -1;
        }

        var first = k;
        while (first >= 2 && _t[first - 1].IsSymbol(".") && _t[first - 2].Kind is SqlTokenKind.Word or SqlTokenKind.QuotedName)
        {
            first -= 2;
        }

        return SqlKeywords.IsName(_t[first]) ? first : -1;
    }

    /// <summary>A parenthesised list of values after IN; returns the closing parenthesis's index or -1.</summary>
    private int ReadInList(int open, string column, List<int> literals)
    {
        if (open >= _t.Count || !_t[open].IsSymbol("("))
        {
            return -1;
        }

        if (open + 1 < _t.Count && (_t[open + 1].IsWord("SELECT") || _t[open + 1].IsWord("WITH")))
        {
            throw Refuse(column, "can't be compared with IN (SELECT ...), which would compare it with whatever the subquery " +
                "returns; use EXISTS or a JOIN on = instead");
        }

        var close = _match[open];
        if (close == open + 1)
        {
            return -1;
        }

        for (var j = open + 1; j < close; j++)
        {
            var isValuePosition = (j - open) % 2 == 1;
            if (isValuePosition && !IsValue(_t[j]))
            {
                return -1;
            }

            if (!isValuePosition && !_t[j].IsSymbol(","))
            {
                return -1;
            }

            if (isValuePosition)
            {
                literals.Add(j);
            }
        }

        return close;
    }

    /// <summary>
    /// Records the string literals and named parameters among <paramref name="indexes"/> as token filters, with the
    /// equality condition they belong to (null for IN-list items). Positional <c>?</c> parameters are skipped: they
    /// have no name to find their value by.
    /// </summary>
    private void AddFilters(List<int> indexes, SensitiveDataType type, TokenComparison? comparison)
    {
        foreach (var token in indexes.Select(i => _t[i]))
        {
            var kind = token.Kind switch
            {
                SqlTokenKind.String => TokenFilterKind.Literal,
                SqlTokenKind.Parameter when !token.Text.StartsWith('?') => TokenFilterKind.Parameter,
                _ => (TokenFilterKind?)null,
            };

            if (kind is not null)
            {
                _filters.Add(new TokenFilter(token.Text, kind.Value, type, token.Start, token.Length, comparison));
            }
        }
    }

    /// <summary>
    /// Matches one VALUES row to the INSERT's column list: it must give one value per column, and each masked column's
    /// value must be a single supplied value, recorded with the column's type.
    /// </summary>
    /// <param name="open">The row's opening parenthesis.</param>
    private void CheckValuesRow(int open)
    {
        var columns = _insertColumns!;
        var items = new List<(int First, int Last)>();
        var close = _match[open];
        if (close > open + 1)
        {
            var first = open + 1;
            for (var j = open + 1; j < close; j++)
            {
                if (_t[j].IsSymbol("("))
                {
                    j = _match[j]; // Skip a nested group, function call or subquery whole.
                }
                else if (_t[j].IsSymbol(","))
                {
                    items.Add((first, j - 1));
                    first = j + 1;
                }
            }

            items.Add((first, close - 1));
        }

        if (items.Count != columns.Count)
        {
            throw new QueryRejectedException("Each VALUES row needs one value per listed column, in the same order.");
        }

        for (var p = 0; p < columns.Count; p++)
        {
            if (columns[p].Type is not { } type)
            {
                continue;
            }

            var (itemFirst, itemLast) = items[p];
            if (!IsSingleValue(itemFirst, itemLast))
            {
                throw Refuse(columns[p].Name, SingleValueRule);
            }

            RecordWritten(itemFirst, type);
        }
    }

    /// <summary>
    /// Whether <paramref name="first"/>..<paramref name="last"/> is one supplied value a masked column may be written
    /// with: a string, a number, NULL or a named parameter. A positional <c>?</c> is not: its value can't be found by
    /// name, so a token in it couldn't be swapped for its real value.
    /// </summary>
    private bool IsSingleValue(int first, int last) =>
        first == last && first < _t.Count && IsValue(_t[first])
        && !(_t[first].Kind == SqlTokenKind.Parameter && _t[first].Text.StartsWith('?'));

    /// <summary>Whether a SET item ends at <paramref name="k"/>: the next item, WHERE, FROM or the end.</summary>
    private bool IsSetItemEnd(int k) =>
        k >= _t.Count || _t[k].IsSymbol(",") || _t[k].IsWord("WHERE") || _t[k].IsWord("FROM");

    private const string SingleValueRule =
        "can only be written with a single value: a quoted value, a number, NULL or a named parameter (@name). That " +
        "lets Masque swap a token for its real value, and keeps other columns from being copied into it";

    private bool IsConditionStart(int k)
    {
        if (k < 0)
        {
            return false;
        }

        // An opening parenthesis counts only when it is a plain group the column sits directly in.
        return IsWordIn(_t[k], ConditionStarts)
            || (_t[k].IsSymbol("(") && Current.Kind == ScopeKind.Group && Current.Open == k);
    }

    private bool IsConditionEnd(int k, Scope effective) =>
        k >= _t.Count
        || _t[k].IsSymbol(")")
        || (_t[k].IsSymbol(",") && effective.Clause == Clause.On)
        || IsWordIn(_t[k], ConditionEnds);

    private static bool IsValue(SqlToken token) =>
        token.Kind is SqlTokenKind.String or SqlTokenKind.Number or SqlTokenKind.Parameter || token.IsWord("NULL");

    private static bool IsEqualityOperator(SqlToken token) =>
        token.Kind == SqlTokenKind.Symbol && EqualityOperators.Contains(token.Text);

    private static bool IsWordIn(SqlToken token, HashSet<string> words) =>
        token.Kind == SqlTokenKind.Word && words.Contains(token.Text);

    /// <summary>Explains, without quoting any value, why a masked column use was refused.</summary>
    private string Reason(Scope scope, Scope effective, int start, int end)
    {
        var next = end + 1 < _t.Count ? _t[end + 1] : SqlToken.None;
        var afterNot = next.IsWord("NOT") && end + 2 < _t.Count ? _t[end + 2] : next;

        if (effective.Clause == Clause.Set)
        {
            return "can't be copied into another column";
        }

        if (effective.Clause == Clause.Values || scope.InWriteSource)
        {
            return "can't be copied into another table or column";
        }

        if (effective.Clause == Clause.OrderBy)
        {
            return "can't be sorted on (ORDER BY, PARTITION BY or a window), because the order reveals its values";
        }

        if (scope.Kind == ScopeKind.Function)
        {
            return $"can't be passed to {scope.FunctionName?.ToUpperInvariant()}(): only COUNT is allowed, because other " +
                "functions can reveal part of the value";
        }

        if (IsWordIn(afterNot, PatternWords))
        {
            return "can't be pattern-matched (LIKE, GLOB and similar), because a pattern reveals part of the value";
        }

        if (afterNot.IsWord("BETWEEN") || (afterNot.Kind == SqlTokenKind.Symbol && RangeOperators.Contains(afterNot.Text)))
        {
            return "can't be range-compared (<, >, BETWEEN), because ranges reveal the value one step at a time";
        }

        return "can only be selected as a whole column, compared with =, <>, IN (...) or IS NULL against a value, a " +
            "parameter or another masked column, grouped, counted, or joined on with =";
    }

    private static QueryRejectedException Refuse(string column, string reason) => new($"Masked column {column} {reason}.");
}
