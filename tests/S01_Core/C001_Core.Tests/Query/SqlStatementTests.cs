using Masque.Core.Query;

namespace Masque.Core.Tests.Query;

/// <summary>
/// Pins down the SQL rules Masque enforces before anything runs. Parsing: one SELECT, INSERT, UPDATE or DELETE,
/// nothing that changes schema, runs code or reaches another database, and every table it touches. Reviewing
/// against the columns masked for this caller: they may be selected, compared for equality, grouped and counted,
/// but never sorted, pattern-matched, range-tested, put through a function or copied elsewhere. Writes may set them
/// only to a single supplied value, and every written value is reported so the gate can handle tokens.
/// </summary>
public class SqlStatementTests
{
    private static readonly Dictionary<string, SensitiveDataType> Masked = new(StringComparer.OrdinalIgnoreCase)
    {
        ["SSN"] = SensitiveDataType.Ssn,
        ["Name"] = SensitiveDataType.Name,
    };

    private static StatementReview Review(string sql) => SqlStatement.Parse(sql).Review(Masked);

    private static QueryRejectedException Refused(string sql) =>
        Should.Throw<QueryRejectedException>(() => Review(sql));

    // ---- Parsing: statement kinds and tables ----

    /// <summary>A SELECT reports every table in FROM and JOIN, with schema-qualified and quoted names read correctly.</summary>
    [Fact]
    public void Parse_Select_ListsTables()
    {
        var statement = SqlStatement.Parse("SELECT p.Name FROM [dbo].[People] AS p JOIN \"Visits\" v ON v.PersonId = p.Id");

        statement.Kind.ShouldBe(StatementKind.Select);
        statement.Tables.Select(t => t.Qualified).ShouldBe(["dbo.People", "Visits"]);
    }

    /// <summary>Names defined by WITH are not tables; the tables inside them are.</summary>
    [Fact]
    public void Parse_CommonTableExpression_ListsOnlyRealTables()
    {
        SqlStatement.Parse("WITH recent AS (SELECT Id FROM Visits) SELECT * FROM recent JOIN People ON People.Id = recent.Id")
            .Tables.Select(t => t.Name).ShouldBe(["Visits", "People"], ignoreOrder: true);
    }

    /// <summary>Writes report their kind and target table.</summary>
    [Theory]
    [InlineData("INSERT INTO People (Status) VALUES ('A')", StatementKind.Insert)]
    [InlineData("UPDATE People SET Status = 'A' WHERE Id = 1", StatementKind.Update)]
    [InlineData("DELETE FROM People WHERE Id = 1", StatementKind.Delete)]
    [InlineData("WITH old AS (SELECT Id FROM People) DELETE FROM People WHERE Id IN (1, 2)", StatementKind.Delete)]
    public void Parse_Writes_ReportKindAndTarget(string sql, StatementKind kind)
    {
        var statement = SqlStatement.Parse(sql);

        statement.Kind.ShouldBe(kind);
        statement.Tables.Select(t => t.Name).ShouldContain("People");
    }

    /// <summary>The SQL Server form UPDATE alias ... FROM table alias names the real table, not the alias.</summary>
    [Fact]
    public void Parse_UpdateThroughAlias_ReportsTheTable()
    {
        SqlStatement.Parse("UPDATE p SET Status = 'A' FROM People p WHERE p.Id = 1")
            .Tables.Select(t => t.Name).ShouldBe(["People"]);
    }

    /// <summary>Keywords inside strings, quoted names and comments are not code; a trailing semicolon is fine.</summary>
    [Fact]
    public void Parse_KeywordsInStringsAndComments_AreIgnored()
    {
        SqlStatement.Parse("SELECT [drop] FROM People -- DELETE everything\nWHERE Status = 'x; DROP TABLE People';")
            .Kind.ShouldBe(StatementKind.Select);
    }

    /// <summary>
    /// Anything but one plain statement is refused before it runs, including a second statement without a semicolon
    /// (SQL Server accepts those) and nested comments, which SQLite and SQL Server end in different places.
    /// </summary>
    [Theory]
    [InlineData("")]
    [InlineData("SELECT 1; SELECT 2")]
    [InlineData("DROP TABLE People")]
    [InlineData("SELECT * FROM People; DROP TABLE People")]
    [InlineData("CREATE TABLE x (a INT)")]
    [InlineData("ALTER TABLE People ADD x INT")]
    [InlineData("TRUNCATE TABLE People")]
    [InlineData("EXEC sp_who")]
    [InlineData("ATTACH DATABASE 'x.db' AS x")]
    [InlineData("PRAGMA table_info(People)")]
    [InlineData("SELECT * INTO Copy FROM People")]
    [InlineData("MERGE People AS t USING x ON 1 = 1 WHEN MATCHED THEN DELETE;")]
    [InlineData("SELECT Id FROM People UNION SELECT Id FROM Visits")]
    [InlineData("INSERT OR REPLACE INTO People (Id) VALUES (1)")]
    [InlineData("REPLACE INTO People (Id) VALUES (1)")]
    [InlineData("INSERT INTO People (Id) VALUES (1) ON CONFLICT DO NOTHING")]
    [InlineData("DELETE FROM People RETURNING *")]
    [InlineData("DELETE FROM People OUTPUT deleted.Id")]
    [InlineData("SELECT * FROM Other.dbo.People")]
    [InlineData("SELECT * FROM read_csv('x.csv')")]
    [InlineData("WITH c (k) AS (SELECT Id FROM People) SELECT k FROM c")]
    [InlineData("SELECT q.k FROM (SELECT Id FROM People) AS q (k)")]
    [InlineData("SELECT 'unterminated FROM People")]
    [InlineData("SELECT * FROM People /* unterminated")]
    [InlineData("SELECT [unterminated FROM People")]
    [InlineData("UPDATE People SET Status = 'A'; DELETE FROM People")]
    [InlineData("SELECT Id FROM People SELECT SSN FROM People")]
    [InlineData("SELECT Id FROM People DELETE FROM People")]
    [InlineData("SELECT 1 PRINT 'x'")]
    [InlineData("SELECT 1 /* /* */ ' */ DELETE FROM People --'")]
    [InlineData("SELECT * FROM (People p JOIN Visits v ON v.Id = p.Id)")]
    public void Parse_NotOnePlainStatement_IsRefused(string sql)
    {
        Should.Throw<QueryRejectedException>(() => SqlStatement.Parse(sql));
    }

    // ---- Review: what masked columns may do ----

    /// <summary>Masked columns may be selected, compared for equality, grouped, counted and used to join.</summary>
    [Theory]
    [InlineData("SELECT SSN, Status FROM People")]
    [InlineData("SELECT DISTINCT p.SSN AS ssn_token FROM People p")]
    [InlineData("SELECT TOP (10) Name FROM People")]
    [InlineData("SELECT * FROM People WHERE SSN = '123-45-6789'")]
    [InlineData("SELECT * FROM People WHERE '123-45-6789' = SSN")]
    [InlineData("SELECT * FROM People p WHERE p.SSN = @ssn AND Status = 'A'")]
    [InlineData("SELECT * FROM People WHERE SSN <> 'x' OR SSN IS NULL OR Name IS NOT NULL")]
    [InlineData("SELECT * FROM People WHERE SSN IN ('a', 'b') AND Name NOT IN (@p1, @p2)")]
    [InlineData("SELECT * FROM People a JOIN Visits b ON a.SSN = b.SSN")]
    [InlineData("SELECT Name, COUNT(*) AS visit_count FROM People GROUP BY Name")]
    [InlineData("SELECT COUNT(DISTINCT SSN), COUNT(Name) FROM People")]
    [InlineData("SELECT CASE WHEN SSN = 'x' THEN 1 ELSE 0 END AS is_x FROM People")]
    [InlineData("SELECT * FROM People p WHERE EXISTS (SELECT 1 FROM Visits v WHERE v.SSN = p.SSN)")]
    [InlineData("SELECT q.k FROM (SELECT SSN AS k FROM People) q")]
    [InlineData("SELECT \"p\".\"Id\", \"p\".\"SSN\" FROM \"People\" AS \"p\" WHERE \"p\".\"SSN\" = @__ssn_0 LIMIT 1")]
    [InlineData("SELECT [p].[Id], [p].[Name] FROM [People] AS [p] ORDER BY (SELECT 1) OFFSET @p ROWS FETCH NEXT @q ROWS ONLY")]
    public void Review_AllowedUses_Pass(string sql)
    {
        Should.NotThrow(() => Review(sql));
    }

    /// <summary>
    /// Uses that can reveal part of a masked value (a character, a range, an order) or launder it into another
    /// column are refused.
    /// </summary>
    [Theory]
    [InlineData("SELECT * FROM People WHERE SSN LIKE '123%'")]
    [InlineData("SELECT * FROM People WHERE SSN < '5'")]
    [InlineData("SELECT * FROM People WHERE SSN BETWEEN '1' AND '2'")]
    [InlineData("SELECT * FROM People WHERE SUBSTR(SSN, 1, 1) = '1'")]
    [InlineData("SELECT * FROM People WHERE LOWER(Name) = 'pat'")]
    [InlineData("SELECT Id FROM People ORDER BY Name")]
    [InlineData("SELECT Name FROM People ORDER BY 1")]
    [InlineData("SELECT SSN || 'x' FROM People")]
    [InlineData("SELECT SSN + 0 AS total FROM People")]
    [InlineData("SELECT MAX(SSN) FROM People")]
    [InlineData("SELECT * FROM People WHERE SSN = Status")]
    [InlineData("SELECT * FROM People WHERE SSN = 'a' || Status")]
    [InlineData("SELECT * FROM People WHERE SSN IN (SELECT Code FROM Codes)")]
    [InlineData("SELECT (SELECT SSN FROM People) AS x")]
    [InlineData("SELECT x FROM (SELECT SSN AS x FROM People) q ORDER BY x")]
    [InlineData("SELECT ROW_NUMBER() OVER (ORDER BY SSN) FROM People")]
    [InlineData("SELECT * FROM People JOIN Visits USING (SSN)")]
    [InlineData("SELECT k = SSN FROM People")]
    [InlineData("SELECT CASE SSN WHEN 'x' THEN 1 END FROM People")]
    [InlineData("SELECT * FROM People WHERE (SSN) > '5'")]
    [InlineData("SELECT * FROM People WHERE SSN IS 'x'")]
    [InlineData("SELECT * FROM People WHERE SSN = 'x' COLLATE NOCASE")]
    [InlineData("SELECT * FROM People WHERE +SSN = 'x'")]
    [InlineData("SELECT SSN AS [Order] FROM People ORDER BY [Order]")]
    [InlineData("SELECT [SSN] FROM People ORDER BY [ssn]")]
    [InlineData("SELECT * FROM People ORDER BY 2")]
    [InlineData("SELECT IIF(SSN = 'x', 1, 0) FROM People")]
    [InlineData("SELECT Status FROM People GROUP BY Status HAVING MAX(SSN) > '5'")]
    [InlineData("SELECT * FROM People LIMIT (SELECT COUNT(*) FROM People WHERE SSN > '5')")]
    [InlineData("INSERT INTO Archive SELECT * FROM People")]
    public void Review_RevealingUses_AreRefused(string sql)
    {
        Refused(sql);
    }

    /// <summary>A refusal names the column and the rule, never a value from the query.</summary>
    [Fact]
    public void Review_Refusal_NeverQuotesTheQuery()
    {
        var error = Refused("SELECT * FROM People WHERE SSN LIKE '123-45%'");

        error.Message.ShouldContain("SSN");
        error.Message.ShouldNotContain("123-45");
    }

    // ---- Review: writes ----

    /// <summary>Inserting supplied values, updating unmasked columns and deleting rows are allowed.</summary>
    [Theory]
    [InlineData("INSERT INTO People (Name, SSN, Status) VALUES ('Pat Lee', '123-45-6789', 'A')")]
    [InlineData("INSERT INTO People (Name) VALUES (@n), (@m)")]
    [InlineData("UPDATE People SET Status = 'B' WHERE SSN = '123-45-6789'")]
    [InlineData("DELETE FROM People WHERE Name = 'Pat Lee'")]
    public void Review_AllowedWrites_Pass(string sql)
    {
        Should.NotThrow(() => Review(sql));
    }

    /// <summary>
    /// Masque only prevents exposure, so a masked column may be set to a value the caller supplies: a literal, a
    /// number, NULL or a named parameter. The SQL Server form <c>UPDATE alias SET alias.col = ...</c> counts too.
    /// </summary>
    [Theory]
    [InlineData("UPDATE People SET SSN = '123-45-6789' WHERE Id = 1")]
    [InlineData("UPDATE People SET SSN = @ssn, Name = 'Pat Lee' WHERE Id = 1")]
    [InlineData("UPDATE People SET Status = 'A', SSN = NULL")]
    [InlineData("UPDATE People SET SSN = 123456789")]
    [InlineData("UPDATE p SET p.SSN = @s FROM People p WHERE p.Id = 1")]
    public void Review_SettingMaskedColumnToSuppliedValue_Passes(string sql)
    {
        Should.NotThrow(() => Review(sql));
    }

    /// <summary>
    /// A masked column may be set only to a single supplied value: Masque can swap a token for its real value only when
    /// the token is the whole value, and copying from another column could launder a masked value. A positional
    /// <c>?</c> has no name to look its value up by, so it is refused for masked columns.
    /// </summary>
    [Theory]
    [InlineData("UPDATE People SET SSN = SSN")]
    [InlineData("UPDATE People SET SSN = Notes")]
    [InlineData("UPDATE People SET SSN = UPPER('x')")]
    [InlineData("UPDATE People SET SSN = '1' || '2'")]
    [InlineData("UPDATE People SET SSN = ?")]
    [InlineData("UPDATE People SET SSN = (SELECT SSN FROM People WHERE Id = 2)")]
    [InlineData("INSERT INTO People (SSN) VALUES (UPPER('x'))")]
    [InlineData("INSERT INTO People (Name, SSN) VALUES ('Pat Lee', ?)")]
    public void Review_SettingMaskedColumnToAnythingElse_IsRefused(string sql)
    {
        Refused(sql).Message.ShouldContain("single value");
    }

    /// <summary>A VALUES row must give one value per listed column, so each value's column is known.</summary>
    [Fact]
    public void Review_ValuesRowOfWrongLength_IsRefused()
    {
        Refused("INSERT INTO People (Name, SSN) VALUES ('Pat Lee')").Message.ShouldContain("one value per");
    }

    /// <summary>A masked column may never be copied into another column, where it would no longer be masked.</summary>
    [Theory]
    [InlineData("UPDATE People SET Notes = SSN")]
    [InlineData("UPDATE People SET Status = (SELECT SSN FROM People WHERE Id = 1)")]
    [InlineData("INSERT INTO Archive (Notes) SELECT SSN FROM People")]
    [InlineData("INSERT INTO Archive (Notes) VALUES ((SELECT Name FROM People WHERE Id = 1))")]
    [InlineData("WITH c AS (SELECT SSN AS k FROM People) INSERT INTO Archive (Notes) SELECT k FROM c")]
    public void Review_CopyingMaskedColumns_IsRefused(string sql)
    {
        Refused(sql);
    }

    // ---- Review: written values ----

    /// <summary>
    /// Every string literal and named parameter an UPDATE writes is reported, with the masked column's type when it
    /// lands in one (so the gate can swap a token for its real value) and no type otherwise (so the gate can refuse a
    /// token that would be stored as a fake). Filters in WHERE are not written values.
    /// </summary>
    [Fact]
    public void Review_UpdateValues_AreWrittenValues()
    {
        var review = Review("UPDATE People SET SSN = '900-11-2222', Status = 'A', Name = @n WHERE SSN = 'x'");

        review.WrittenValues.Select(v => (v.Text, v.Kind, v.Type)).ShouldBe(
        [
            ("900-11-2222", TokenFilterKind.Literal, (SensitiveDataType?)SensitiveDataType.Ssn),
            ("A", TokenFilterKind.Literal, null),
            ("@n", TokenFilterKind.Parameter, SensitiveDataType.Name),
        ]);
        review.TokenFilters.Select(f => f.Text).ShouldBe(["x"]);
    }

    /// <summary>
    /// INSERT values are matched to the column list by position, row by row. Positional <c>?</c> parameters into
    /// unmasked columns are allowed but not reported: they have no name to look up.
    /// </summary>
    [Fact]
    public void Review_InsertValues_AreMatchedToTheirColumns()
    {
        const string sql = "INSERT INTO People (Name, SSN, Status) VALUES ('Pat Lee', @s, 'A'), ('Lee Kim', '900-11-2222', ?)";

        var written = Review(sql).WrittenValues;

        written.Select(v => (v.Text, v.Type)).ShouldBe(
        [
            ("Pat Lee", (SensitiveDataType?)SensitiveDataType.Name),
            ("@s", SensitiveDataType.Ssn),
            ("A", null),
            ("Lee Kim", SensitiveDataType.Name),
            ("900-11-2222", SensitiveDataType.Ssn),
        ]);
        written.ShouldAllBe(v => sql.Substring(v.Start, v.Length).Contains(v.Text));
    }

    /// <summary>
    /// Values whose column isn't known (no column list, an INSERT ... SELECT, or inside an expression) are reported
    /// without a type, so a token among them can be refused rather than stored as a fake.
    /// </summary>
    [Theory]
    [InlineData("INSERT INTO People VALUES (1, 'Pat Lee')", "Pat Lee")]
    [InlineData("INSERT INTO Archive (Notes) SELECT 'Pat Lee' FROM People", "Pat Lee")]
    [InlineData("UPDATE People SET Notes = 'Call ' || @x", "Call ", "@x")]
    public void Review_ValuesWithoutAKnownColumn_HaveNoType(string sql, params string[] expected)
    {
        var written = Review(sql).WrittenValues;

        written.Select(v => v.Text).ShouldBe(expected);
        written.ShouldAllBe(v => v.Type == null);
    }

    /// <summary>A comparison inside a written CASE is a filter, not a written value; its result values are written.</summary>
    [Fact]
    public void Review_CaseConditionInSet_IsAFilterNotAWrittenValue()
    {
        var review = Review("UPDATE People SET Status = CASE WHEN SSN = 'x' THEN 'A' ELSE 'B' END");

        review.TokenFilters.Select(f => f.Text).ShouldBe(["x"]);
        review.WrittenValues.Select(v => v.Text).ShouldBe(["A", "B"]);
    }

    /// <summary>A SELECT writes nothing.</summary>
    [Fact]
    public void Review_Select_HasNoWrittenValues()
    {
        Review("SELECT * FROM People WHERE SSN = 'x' AND Status = 'A'").WrittenValues.ShouldBeEmpty();
    }

    // ---- Review: outputs and token filters ----

    /// <summary>Aliases of masked columns are tracked, so the output column is masked with the source's type.</summary>
    [Fact]
    public void Review_TracksAliasesOfMaskedColumns()
    {
        Review("SELECT q.k FROM (SELECT SSN AS k FROM People) q").MaskedNames["k"].ShouldBe(SensitiveDataType.Ssn);
    }

    /// <summary>String literals compared with a masked column are reported, so a token there can stand for its value.</summary>
    [Fact]
    public void Review_EqualityLiterals_AreTokenFilterCandidates()
    {
        const string sql = "SELECT * FROM People WHERE SSN = '900-11-2222' OR Name IN ('Avery Brooks', 'Lee Kim') OR Status = 'A'";

        var filters = Review(sql).TokenFilters;

        filters.Select(f => (f.Text, f.Type)).ShouldBe(
        [
            ("900-11-2222", SensitiveDataType.Ssn),
            ("Avery Brooks", SensitiveDataType.Name),
            ("Lee Kim", SensitiveDataType.Name),
        ]);
        filters.ShouldAllBe(f => sql.Substring(f.Start, f.Length).StartsWith('\''));
    }

    /// <summary>
    /// A comparison filter reports the whole condition, the column as written and whether it is negated, so the gate
    /// can turn it into <c>column [NOT] IN (...)</c> over every stored spelling, whichever side the column is on.
    /// </summary>
    [Fact]
    public void Review_ComparisonFilters_ReportTheCondition()
    {
        const string sql = "SELECT * FROM People p WHERE p.SSN = '900-11-2222' AND 'Avery Brooks' <> [Name]";

        var filters = Review(sql).TokenFilters;

        filters.Count.ShouldBe(2);
        var ssn = filters[0].Comparison.ShouldNotBeNull();
        sql.Substring(ssn.Start, ssn.Length).ShouldBe("p.SSN = '900-11-2222'");
        sql.Substring(ssn.ColumnStart, ssn.ColumnLength).ShouldBe("p.SSN");
        ssn.Negated.ShouldBeFalse();

        var name = filters[1].Comparison.ShouldNotBeNull();
        sql.Substring(name.Start, name.Length).ShouldBe("'Avery Brooks' <> [Name]");
        sql.Substring(name.ColumnStart, name.ColumnLength).ShouldBe("[Name]");
        name.Negated.ShouldBeTrue();
    }

    /// <summary>
    /// Named parameters compared with a masked column are reported too, since EF Core and Dapper pass values that way;
    /// the gate checks each one's value for a token. Positional <c>?</c> parameters have no name to look up, so they
    /// are not reported. String literals are reported as literals.
    /// </summary>
    [Fact]
    public void Review_NamedParameters_AreTokenFilterCandidates()
    {
        const string sql = "SELECT * FROM People WHERE SSN = @ssn AND Name IN (:a, $b) AND SSN <> ? AND Name = 'Lee Kim' AND Status = @s";

        var filters = Review(sql).TokenFilters;

        filters.Select(f => (f.Text, f.Kind, f.Type)).ShouldBe(
        [
            ("@ssn", TokenFilterKind.Parameter, SensitiveDataType.Ssn),
            (":a", TokenFilterKind.Parameter, SensitiveDataType.Name),
            ("$b", TokenFilterKind.Parameter, SensitiveDataType.Name),
            ("Lee Kim", TokenFilterKind.Literal, SensitiveDataType.Name),
        ]);
        filters[0].Comparison.ShouldNotBeNull();
        filters[1].Comparison.ShouldBeNull();
    }

    /// <summary>A literal in an IN list has no comparison of its own: the gate swaps it for a list of values in place.</summary>
    [Fact]
    public void Review_InListFilters_HaveNoComparison()
    {
        Review("SELECT * FROM People WHERE SSN NOT IN ('900-11-2222', '900-33-4444')").TokenFilters
            .ShouldAllBe(f => f.Comparison == null);
    }

    /// <summary>Values being inserted are never treated as tokens.</summary>
    [Fact]
    public void Review_InsertedValues_AreNotTokenFilters()
    {
        Review("INSERT INTO People (SSN) VALUES ('900-11-2222')").TokenFilters.ShouldBeEmpty();
    }
}
