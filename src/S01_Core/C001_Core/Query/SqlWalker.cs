namespace Masque.Core.Query;

/// <summary>
/// Walks a statement's tokens once, left to right, keeping a stack of scopes (one per open parenthesis) so each token
/// is judged in context: which clause it is in, and whether it is inside a subquery, a function call or a plain
/// group. Enforces the statement-shape rules here and the masked-column rules in <c>SqlWalker.Masked.cs</c>.
/// </summary>
internal sealed partial class SqlWalker
{
    private readonly List<SqlToken> _t;
    private readonly IReadOnlyDictionary<string, SensitiveDataType> _masked;
    private readonly int[] _match;
    private readonly Stack<Scope> _scopes = new();
    private readonly List<TableReference> _tables = [];
    private readonly HashSet<string> _aliases = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _cteNames = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<int> _cteOpens = [];
    private readonly List<TokenFilter> _filters = [];
    private TableReference? _writeTarget;
    private int _mainIndex;
    private StatementKind _kind;

    /// <summary>Prepares a walk.</summary>
    /// <param name="tokens">The statement's tokens, trailing semicolons removed.</param>
    /// <param name="masked">Masked column names and aliases, with their types; empty to check shape only.</param>
    public SqlWalker(List<SqlToken> tokens, IReadOnlyDictionary<string, SensitiveDataType> masked)
    {
        _t = tokens;
        _masked = masked;
        _match = MatchParentheses(tokens);
    }

    /// <summary>Where a token sits.</summary>
    private enum ScopeKind
    {
        Root, Cte, Derived, Exists, InSubquery, Scalar, Group, Function, InList, ValuesRow, Window,
    }

    /// <summary>Which clause of its query a token is in.</summary>
    private enum Clause
    {
        None, Select, From, On, Where, GroupBy, Having, OrderBy, Limit, Set, InsertSource, Values,
    }

    /// <summary>Checks the statement.</summary>
    /// <returns>Its kind, tables and token-filter candidates.</returns>
    /// <exception cref="QueryRejectedException">A rule is broken.</exception>
    public WalkResult Walk()
    {
        CheckWords();
        ReadHeader();

        _scopes.Push(new Scope(ScopeKind.Root, -1, inWriteSource: false, functionName: null, parent: null));

        var i = 0;
        while (i < _t.Count)
        {
            i = Step(i);
        }

        if (_writeTarget is not null && !(_writeTarget.Schema is null && _aliases.Contains(_writeTarget.Name)))
        {
            _tables.Insert(0, _writeTarget);
        }

        var tables = _tables.DistinctBy(t => t.Qualified, StringComparer.OrdinalIgnoreCase).ToList();
        return new WalkResult(_kind, tables, _filters);
    }

    /// <summary>
    /// Adds aliases of masked columns (<c>SSN AS k</c> or <c>SSN k</c>) to the masked names, repeating until nothing
    /// new is found, so a renamed column stays masked through subqueries and WITH.
    /// </summary>
    /// <param name="tokens">The statement's tokens.</param>
    /// <param name="masked">The masked column names.</param>
    /// <returns>The masked names plus their aliases (case-insensitive).</returns>
    public static Dictionary<string, SensitiveDataType> TrackAliases(List<SqlToken> tokens, IReadOnlyDictionary<string, SensitiveDataType> masked)
    {
        var names = new Dictionary<string, SensitiveDataType>(StringComparer.OrdinalIgnoreCase);
        foreach (var (name, type) in masked)
        {
            names.TryAdd(name, type);
        }

        bool added;
        do
        {
            added = false;
            for (var i = 0; i < tokens.Count; i++)
            {
                if (!SqlKeywords.IsName(tokens[i]))
                {
                    continue;
                }

                var end = ChainEnd(tokens, i);
                if (names.TryGetValue(tokens[end].Text, out var type))
                {
                    var k = end + 1;
                    var alias = k + 1 < tokens.Count && tokens[k].IsWord("AS") && SqlKeywords.IsName(tokens[k + 1]) ? tokens[k + 1].Text
                        : k < tokens.Count && SqlKeywords.IsName(tokens[k]) ? tokens[k].Text
                        : null;

                    added |= alias is not null && names.TryAdd(alias, type);
                }

                i = end;
            }
        }
        while (added);

        return names;
    }

    // ---- Statement shape ----

    /// <summary>Refuses semicolons between statements and forbidden words.</summary>
    private void CheckWords()
    {
        if (_t.Count == 0)
        {
            throw new QueryRejectedException("The query is empty. Send one SELECT, INSERT, UPDATE or DELETE statement.");
        }

        if (_t.Any(t => t.IsSymbol(";")))
        {
            throw new QueryRejectedException("Send one statement at a time; the query contains more than one.");
        }

        foreach (var token in _t.Where(t => t.Kind == SqlTokenKind.Word))
        {
            if (SqlKeywords.SetOperators.Contains(token.Text))
            {
                throw new QueryRejectedException(
                    "UNION, INTERSECT and EXCEPT are not allowed: Masque traces each result column to one source column, " +
                    "and set operators hide where rows came from. Run each part as its own query.");
            }

            if (SqlKeywords.Forbidden.Contains(token.Text))
            {
                throw new QueryRejectedException(
                    $"The query uses {token.Text.ToUpperInvariant()}, which Masque doesn't allow (it changes schema or " +
                    "settings, runs code, starts another statement or hides where results come from). If it is a name, quote it.");
            }
        }
    }

    /// <summary>Reads the optional WITH list and finds the main statement's keyword.</summary>
    private void ReadHeader()
    {
        var i = 0;

        if (_t[0].IsWord("WITH"))
        {
            i = 1;
            if (i < _t.Count && _t[i].IsWord("RECURSIVE"))
            {
                i++;
            }

            while (true)
            {
                if (i >= _t.Count || !SqlKeywords.IsName(_t[i]))
                {
                    throw new QueryRejectedException("Each WITH entry needs a name.");
                }

                _cteNames.Add(_t[i].Text);
                i++;

                if (i < _t.Count && _t[i].IsSymbol("("))
                {
                    throw new QueryRejectedException(
                        "Column lists after a WITH name aren't supported; name the columns inside the query with AS instead.");
                }

                if (i >= _t.Count || !_t[i].IsWord("AS"))
                {
                    throw new QueryRejectedException("Each WITH entry needs AS (SELECT ...).");
                }

                i++;
                if (i < _t.Count && _t[i].IsWord("NOT"))
                {
                    i++;
                }

                if (i < _t.Count && _t[i].IsWord("MATERIALIZED"))
                {
                    i++;
                }

                if (i + 1 >= _t.Count || !_t[i].IsSymbol("(") || !_t[i + 1].IsWord("SELECT"))
                {
                    throw new QueryRejectedException("Each WITH entry must hold one SELECT in parentheses.");
                }

                _cteOpens.Add(i);
                i = _match[i] + 1;

                if (i < _t.Count && _t[i].IsSymbol(","))
                {
                    i++;
                    continue;
                }

                break;
            }
        }

        _mainIndex = i;
        _kind = i < _t.Count && _t[i].Kind == SqlTokenKind.Word
            ? _t[i].Text.ToUpperInvariant() switch
            {
                "SELECT" => StatementKind.Select,
                "INSERT" => StatementKind.Insert,
                "UPDATE" => StatementKind.Update,
                "DELETE" => StatementKind.Delete,
                _ => throw NotAStatement(),
            }
            : throw NotAStatement();

        static QueryRejectedException NotAStatement() =>
            new("Masque runs one SELECT, INSERT, UPDATE or DELETE statement (optionally after WITH).");
    }

    /// <summary>Handles the token at <paramref name="i"/> and returns the index of the next one to handle.</summary>
    private int Step(int i)
    {
        var token = _t[i];

        if (Current.Kind == ScopeKind.Root && i < _mainIndex)
        {
            // The WITH header: its names were read already; only the bodies need walking.
            return _cteOpens.Contains(i) ? Push(ScopeKind.Cte, i) : i + 1;
        }

        if (i == _mainIndex)
        {
            return _kind switch
            {
                StatementKind.Insert => StartInsert(i),
                StatementKind.Update => StartUpdate(i),
                StatementKind.Delete => StartDelete(i),
                _ => OnKeyword(i),
            };
        }

        return token.Kind switch
        {
            SqlTokenKind.Symbol => OnSymbol(i),
            SqlTokenKind.Word when !SqlKeywords.IsName(token) => OnKeyword(i),
            SqlTokenKind.Word or SqlTokenKind.QuotedName => OnName(i),
            SqlTokenKind.Number => OnNumber(i),
            _ => i + 1,
        };
    }

    private Scope Current => _scopes.Peek();

    private int StartInsert(int i)
    {
        var k = i + 1;
        if (k < _t.Count && _t[k].IsWord("OR"))
        {
            throw new QueryRejectedException("INSERT OR ... isn't allowed: it can replace or change existing rows.");
        }

        if (k < _t.Count && _t[k].IsWord("INTO"))
        {
            k++;
        }

        var end = ReadTableName(k, "INSERT");
        _tables.Add(ToTable(k, end));
        k = end + 1;

        if (k < _t.Count && _t[k].IsSymbol("("))
        {
            for (var j = k + 1; j < _match[k]; j++)
            {
                var expected = (j - k) % 2 == 1 ? SqlKeywords.IsName(_t[j]) : _t[j].IsSymbol(",");
                if (!expected)
                {
                    throw new QueryRejectedException("An INSERT column list may hold only column names.");
                }
            }

            k = _match[k] + 1;
        }

        Current.InWriteSource = true;
        Current.Clause = Clause.InsertSource;

        if (k + 1 < _t.Count && _t[k].IsWord("DEFAULT") && _t[k + 1].IsWord("VALUES"))
        {
            return k + 2;
        }

        if (k >= _t.Count || !(_t[k].IsWord("VALUES") || _t[k].IsWord("SELECT")))
        {
            throw new QueryRejectedException("INSERT needs VALUES (...) or a SELECT after its table and columns.");
        }

        return k;
    }

    private int StartUpdate(int i)
    {
        var k = i + 1;
        if (k < _t.Count && _t[k].IsWord("OR"))
        {
            throw new QueryRejectedException("UPDATE OR ... isn't allowed: it can replace rows.");
        }

        k = SkipTop(k);
        var end = ReadTableName(k, "UPDATE");
        _writeTarget = ToTable(k, end);
        k = SkipHint(SkipAlias(end + 1));

        if (k >= _t.Count || !_t[k].IsWord("SET"))
        {
            throw new QueryRejectedException("UPDATE needs SET after its table.");
        }

        Current.Clause = Clause.Set;
        Current.ItemStart = k + 1;
        return k + 1;
    }

    private int StartDelete(int i)
    {
        var k = SkipTop(i + 1);
        if (k < _t.Count && _t[k].IsWord("FROM"))
        {
            return k;
        }

        // SQL Server's DELETE alias FROM table alias ... names the target before FROM.
        var end = ReadTableName(k, "DELETE");
        _writeTarget = ToTable(k, end);
        return end + 1;
    }

    private int OnKeyword(int i)
    {
        var scope = Current;
        var word = _t[i].Text.ToUpperInvariant();

        switch (word)
        {
            case "SELECT":
                RequireQueryScope(scope, word);
                if (scope.HasSelect)
                {
                    throw new QueryRejectedException("Send one statement at a time; the query contains more than one SELECT at the same level.");
                }

                scope.HasSelect = true;
                scope.Clause = Clause.Select;
                scope.ItemStart = SkipSelectModifiers(i + 1);
                return scope.ItemStart;

            case "FROM":
            case "JOIN":
            case "APPLY":
                RequireQueryScope(scope, word);
                scope.Clause = Clause.From;
                return ReadTableSource(i + 1);

            case "USING":
                RequireQueryScope(scope, word);
                return SkipUsingList(i + 1);

            case "ON":
                return SetClause(scope, word, Clause.On, i + 1);

            case "WHERE":
                return SetClause(scope, word, Clause.Where, i + 1);

            case "HAVING":
                return SetClause(scope, word, Clause.Having, i + 1);

            case "GROUP":
                RequireBy(i, word);
                return SetClause(scope, word, Clause.GroupBy, i + 2);

            case "ORDER":
            case "PARTITION":
                RequireBy(i, word);
                if (scope.Kind is not (ScopeKind.Window or ScopeKind.Function))
                {
                    RequireQueryScope(scope, word);
                }

                scope.Clause = Clause.OrderBy;
                return i + 2;

            case "WINDOW":
                return SetClause(scope, word, Clause.OrderBy, i + 1);

            case "LIMIT":
            case "OFFSET":
            case "FETCH":
                return SetClause(scope, word, Clause.Limit, i + 1);

            case "WHEN":
                scope.Effective.InWhen = true;
                return i + 1;

            case "THEN":
                scope.Effective.InWhen = false;
                return i + 1;

            case "VALUES":
                if (scope.Kind != ScopeKind.Root || scope.Clause != Clause.InsertSource)
                {
                    throw new QueryRejectedException("VALUES is only allowed as the rows of an INSERT.");
                }

                scope.Clause = Clause.Values;
                return i + 1;

            case "SET":
                throw new QueryRejectedException("SET is only allowed in an UPDATE's SET clause.");

            case "INTO":
                throw new QueryRejectedException("SELECT INTO isn't allowed: it creates a table. Use INSERT INTO ... SELECT instead.");

            case "WITH":
                throw new QueryRejectedException("WITH is only allowed at the start of the statement (or as a table hint).");

            case "INSERT":
            case "UPDATE":
            case "DELETE":
                throw new QueryRejectedException("Send one statement at a time; the query contains more than one.");

            default:
                return i + 1;
        }
    }

    private int OnSymbol(int i)
    {
        var scope = Current;

        switch (_t[i].Text)
        {
            case "(":
                return OpenParenthesis(i);

            case ")":
                _scopes.Pop();
                return scope.Kind == ScopeKind.Derived ? SkipAlias(i + 1) : i + 1;

            case "," when scope.IsQuery && scope.Clause == Clause.From:
                return ReadTableSource(i + 1);

            case "," when scope.IsQuery && scope.Clause is Clause.Select or Clause.Set:
                scope.ItemStart = i + 1;
                return i + 1;

            case "*" when scope.IsQuery && scope.Clause == Clause.Select && (i == scope.ItemStart || _t[i - 1].IsSymbol(".")):
                if (scope.InWriteSource)
                {
                    throw new QueryRejectedException(
                        "SELECT * can't be the source of an INSERT or UPDATE: it could copy masked columns. Name the columns.");
                }

                scope.SelectsStarOrMasked = true;
                return i + 1;

            default:
                return i + 1;
        }
    }

    /// <summary>Refuses ORDER BY a column position when the select list holds * or a masked column.</summary>
    private int OnNumber(int i)
    {
        var scope = Current;
        if (scope.IsQuery && scope.Clause == Clause.OrderBy && scope.SelectsStarOrMasked
            && (_t[i - 1].IsWord("BY") || _t[i - 1].IsSymbol(",")))
        {
            throw new QueryRejectedException(
                "ORDER BY a column number isn't allowed when the query returns * or a masked column: sorting on a masked " +
                "column reveals its values. Order by an unmasked column's name instead.");
        }

        return i + 1;
    }

    /// <summary>Opens a scope for a parenthesis, judging what it holds from its neighbours.</summary>
    private int OpenParenthesis(int i)
    {
        var previous = i > 0 ? _t[i - 1] : SqlToken.None;
        var next = i + 1 < _t.Count ? _t[i + 1] : SqlToken.None;

        if (next.IsWord("WITH"))
        {
            throw new QueryRejectedException("WITH is only allowed at the start of the statement.");
        }

        var kind = next.IsWord("SELECT") ? (previous.IsWord("EXISTS") ? ScopeKind.Exists : previous.IsWord("IN") ? ScopeKind.InSubquery : ScopeKind.Scalar)
            : previous.IsWord("IN") ? ScopeKind.InList
            : previous.IsWord("OVER") ? ScopeKind.Window
            : previous.IsWord("VALUES") || (previous.IsSymbol(",") && Current.Clause == Clause.Values) ? ScopeKind.ValuesRow
            : SqlKeywords.IsName(previous) || previous.IsWord("LEFT") || previous.IsWord("RIGHT") ? ScopeKind.Function
            : ScopeKind.Group;

        return Push(kind, i, kind == ScopeKind.Function ? previous.Text : null);
    }

    private int Push(ScopeKind kind, int open, string? functionName = null)
    {
        var parent = Current;

        // A subquery feeds what is being written when its parent does, or when it sits in SET or VALUES.
        // EXISTS only tests for rows, so nothing it selects is copied.
        var inWriteSource = kind != ScopeKind.Exists
            && (parent.InWriteSource || parent.Effective.Clause is Clause.Set or Clause.Values);

        _scopes.Push(new Scope(kind, open, inWriteSource, functionName, parent));
        return open + 1;
    }

    // ---- Tables, aliases and hints ----

    /// <summary>Reads a table (with alias and hint) or opens a subquery at a FROM/JOIN position.</summary>
    private int ReadTableSource(int i)
    {
        if (i < _t.Count && _t[i].IsSymbol("("))
        {
            if (i + 1 < _t.Count && _t[i + 1].IsWord("SELECT"))
            {
                return Push(ScopeKind.Derived, i);
            }

            throw new QueryRejectedException("Only a subquery (SELECT ...) may appear in parentheses after FROM or JOIN.");
        }

        var end = ReadTableName(i, "FROM or JOIN");
        if (end + 1 < _t.Count && _t[end + 1].IsSymbol("("))
        {
            throw new QueryRejectedException(
                "Table-valued functions aren't allowed after FROM or JOIN: they can read files or other sources Masque can't trace.");
        }

        if (!(end == i && _cteNames.Contains(_t[i].Text)))
        {
            _tables.Add(ToTable(i, end));
        }

        return SkipHint(SkipAlias(end + 1));
    }

    /// <summary>Reads a table name of one or two parts and returns its last token's index.</summary>
    private int ReadTableName(int i, string after)
    {
        if (i >= _t.Count || !SqlKeywords.IsName(_t[i]))
        {
            throw new QueryRejectedException($"Expected a table name after {after}.");
        }

        var end = ChainEnd(_t, i);
        if (end - i > 2)
        {
            throw new QueryRejectedException(
                "Names with a database or server part aren't allowed: Masque queries one database. Use schema.table or table.");
        }

        return end;
    }

    private TableReference ToTable(int start, int end) =>
        end == start ? new TableReference(null, _t[start].Text) : new TableReference(_t[start].Text, _t[end].Text);

    /// <summary>Skips an alias (<c>AS name</c> or <c>name</c>) and refuses a column list after it.</summary>
    private int SkipAlias(int k)
    {
        if (k < _t.Count && _t[k].IsWord("AS"))
        {
            if (k + 1 >= _t.Count || !SqlKeywords.IsName(_t[k + 1]))
            {
                throw new QueryRejectedException("AS needs a name after it.");
            }

            _aliases.Add(_t[k + 1].Text);
            k += 2;
        }
        else if (k < _t.Count && SqlKeywords.IsName(_t[k]))
        {
            _aliases.Add(_t[k].Text);
            k++;
        }

        if (k < _t.Count && _t[k].IsSymbol("("))
        {
            throw new QueryRejectedException(
                "Column lists after an alias aren't supported; name the columns inside the subquery with AS instead.");
        }

        return k;
    }

    /// <summary>Skips a SQL Server table hint such as <c>WITH (NOLOCK)</c>; hints don't read data.</summary>
    private int SkipHint(int k) =>
        k + 1 < _t.Count && _t[k].IsWord("WITH") && _t[k + 1].IsSymbol("(") ? _match[k + 1] + 1 : k;

    /// <summary>Skips <c>TOP n</c>, <c>TOP (n)</c> and <c>PERCENT</c>.</summary>
    private int SkipTop(int k)
    {
        if (k >= _t.Count || !_t[k].IsWord("TOP"))
        {
            return k;
        }

        k++;
        if (k < _t.Count && _t[k].IsSymbol("("))
        {
            k = _match[k] + 1;
        }
        else if (k < _t.Count && _t[k].Kind is SqlTokenKind.Number or SqlTokenKind.Parameter)
        {
            k++;
        }

        return k < _t.Count && _t[k].IsWord("PERCENT") ? k + 1 : k;
    }

    /// <summary>Skips DISTINCT, ALL and TOP ... [WITH TIES] at the start of a select list.</summary>
    private int SkipSelectModifiers(int k)
    {
        while (k < _t.Count)
        {
            if (_t[k].IsWord("DISTINCT") || _t[k].IsWord("ALL"))
            {
                k++;
            }
            else if (_t[k].IsWord("TOP"))
            {
                k = SkipTop(k);
                if (k + 1 < _t.Count && _t[k].IsWord("WITH") && _t[k + 1].IsWord("TIES"))
                {
                    k += 2;
                }
            }
            else
            {
                break;
            }
        }

        return k;
    }

    /// <summary>Checks a <c>USING (a, b)</c> join list, which may not name a masked column.</summary>
    private int SkipUsingList(int i)
    {
        if (i >= _t.Count || !_t[i].IsSymbol("("))
        {
            throw new QueryRejectedException("USING needs a list of columns in parentheses.");
        }

        for (var j = i + 1; j < _match[i]; j++)
        {
            if (_masked.ContainsKey(_t[j].Text) && SqlKeywords.IsName(_t[j]))
            {
                throw Refuse(_t[j].Text, "can't be joined with USING (it merges the column from both tables); write ON a.col = b.col instead");
            }
        }

        return _match[i] + 1;
    }

    // ---- Helpers ----

    private int SetClause(Scope scope, string word, Clause clause, int next)
    {
        RequireQueryScope(scope, word);
        scope.Clause = clause;
        return next;
    }

    private static void RequireQueryScope(Scope scope, string word)
    {
        if (!scope.IsQuery)
        {
            throw new QueryRejectedException($"{word} isn't allowed inside a function call or plain parentheses.");
        }
    }

    private void RequireBy(int i, string word)
    {
        if (i + 1 >= _t.Count || !_t[i + 1].IsWord("BY"))
        {
            throw new QueryRejectedException($"{word} must be followed by BY.");
        }
    }

    /// <summary>The last index of a dotted name such as <c>dbo.People</c> or <c>[p].[SSN]</c> starting at <paramref name="i"/>.</summary>
    private static int ChainEnd(List<SqlToken> tokens, int i)
    {
        var end = i;
        while (end + 2 < tokens.Count && tokens[end + 1].IsSymbol(".")
            && tokens[end + 2].Kind is SqlTokenKind.Word or SqlTokenKind.QuotedName)
        {
            end += 2;
        }

        return end;
    }

    /// <summary>Pairs each parenthesis with its partner; refuses unbalanced ones.</summary>
    private static int[] MatchParentheses(List<SqlToken> tokens)
    {
        var match = new int[tokens.Count];
        var open = new Stack<int>();

        for (var i = 0; i < tokens.Count; i++)
        {
            match[i] = -1;
            if (tokens[i].IsSymbol("("))
            {
                open.Push(i);
            }
            else if (tokens[i].IsSymbol(")"))
            {
                if (open.Count == 0)
                {
                    throw Unbalanced();
                }

                var start = open.Pop();
                match[start] = i;
                match[i] = start;
            }
        }

        return open.Count == 0 ? match : throw Unbalanced();

        static QueryRejectedException Unbalanced() => new("The query's parentheses don't balance.");
    }

    /// <summary>What a walk found.</summary>
    /// <param name="Kind">The statement's kind.</param>
    /// <param name="Tables">The tables it touches.</param>
    /// <param name="TokenFilters">Literals compared with masked columns.</param>
    internal sealed record WalkResult(StatementKind Kind, IReadOnlyList<TableReference> Tables, IReadOnlyList<TokenFilter> TokenFilters);

    /// <summary>One level of parentheses (or the whole statement) and what has been seen in it.</summary>
    private sealed class Scope(ScopeKind kind, int open, bool inWriteSource, string? functionName, Scope? parent)
    {
        public ScopeKind Kind { get; } = kind;

        /// <summary>The index of the opening parenthesis; -1 for the whole statement.</summary>
        public int Open { get; } = open;

        public string? FunctionName { get; } = functionName;

        public Scope? Parent { get; } = parent;

        /// <summary>Whether what this scope selects ends up written to a table.</summary>
        public bool InWriteSource { get; set; } = inWriteSource;

        public Clause Clause { get; set; }

        /// <summary>The index where the current select-list or SET item starts.</summary>
        public int ItemStart { get; set; } = -1;

        /// <summary>Between WHEN and THEN of a CASE.</summary>
        public bool InWhen { get; set; }

        public bool HasSelect { get; set; }

        public bool SelectsStarOrMasked { get; set; }

        /// <summary>Whether this scope is a whole query with its own clauses.</summary>
        public bool IsQuery => Kind is ScopeKind.Root or ScopeKind.Cte or ScopeKind.Derived
            or ScopeKind.Exists or ScopeKind.InSubquery or ScopeKind.Scalar;

        /// <summary>The nearest scope that isn't a plain group: plain parentheses take their clause from outside.</summary>
        public Scope Effective
        {
            get
            {
                var scope = this;
                while (scope.Kind == ScopeKind.Group && scope.Parent is not null)
                {
                    scope = scope.Parent;
                }

                return scope;
            }
        }
    }
}
