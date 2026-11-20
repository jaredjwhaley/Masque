using System.Text.Json;
using System.Text.Json.Nodes;
using ClosedXML.Excel;
using Masque.Core;
using Masque.Vault.Tests.TestSupport;
using Microsoft.Data.Sqlite;

namespace Masque.Mcp.Tests;

/// <summary>
/// Pins down what the assistant actually sees through the Masque tools: table and column descriptions, a review
/// gate on new table shapes, masked query results, token filters, per-query narrowing, policy-controlled writes and
/// safe errors. The overriding rule, checked in nearly every test, is that no synthetic real value appears in a
/// reply unless the policy lets the assistant see that type raw.
/// </summary>
public sealed class MasqueToolServiceTests : IDisposable
{
    // Synthetic values only. Each is distinctive so a leak is easy to spot.
    private const string Ssn = "123-45-6789";
    private const string FirstName = "Zebulon";
    private const string Email = "zebulon.quark@example.com";
    private const string Note = "allergic to penicillin";

    private const string AssistantSeesEmail =
        """{ "default": { "rawTypes": [], "canWrite": false }, "rules": [ { "name": "ai", "users": ["assistant"], "groups": [], "rawTypes": ["Email"], "canWrite": false } ] }""";

    private const string AssistantCanWrite =
        """{ "default": { "rawTypes": [], "canWrite": false }, "rules": [ { "name": "ai", "users": ["assistant"], "groups": [], "rawTypes": [], "canWrite": true } ] }""";

    private static readonly string[] RealValues = [Ssn, FirstName, Email, Note, "987-65-4321", "Lee", "lee@example.org"];

    private readonly string _folder = Path.Combine(Path.GetTempPath(), "masque-tests", $"{Guid.NewGuid():N}");
    private readonly string _data;
    private readonly string _policyPath;
    private readonly MasqueToolService _service;

    public MasqueToolServiceTests()
    {
        _data = Path.Combine(_folder, "data");
        Directory.CreateDirectory(_data);
        _policyPath = Path.Combine(_folder, "policy.json");
        _service = NewService();
    }

    public void Dispose()
    {
        _service.Dispose();
        SqliteConnection.ClearAllPools();
        Directory.Delete(_folder, recursive: true);
    }

    /// <summary>Builds a service over this test's folder. The policy is read when the gate is built.</summary>
    private MasqueToolService NewService() => MasqueToolService.Create(
        configFolder: Path.Combine(_folder, "configs"),
        vaultPath: Path.Combine(_folder, "vault.db"),
        sourcesPath: Path.Combine(_folder, "sources.json"),
        keyProtector: new FakeKeyProtector("tester"),
        policyPath: _policyPath,
        machinePolicyPath: Path.Combine(_folder, "no-machine-policy.json"));

    /// <summary>Writes the user's policy file and builds a second service that reads it.</summary>
    private MasqueToolService ServiceWithPolicy(string json)
    {
        File.WriteAllText(_policyPath, json);
        return NewService();
    }

    private string PeopleCsv()
    {
        var path = Path.Combine(_data, "people.csv");
        File.WriteAllText(path,
            "PersonId,FirstName,SSN,Email,GrossPay,Status,Notes\n" +
            $"1,{FirstName},{Ssn},{Email},\"1,234.56\",A,{Note}\n" +
            "2,Lee,987-65-4321,lee@example.org,980.00,T,\n");
        return path;
    }

    private string VisitsWorkbook()
    {
        var path = Path.Combine(_data, "visits.xlsx");
        using var workbook = new XLWorkbook();
        var sheet = workbook.AddWorksheet("Visits");
        sheet.Cell("A1").Value = "PersonId";
        sheet.Cell("B1").Value = "VisitDate";
        sheet.Cell("A2").Value = 1;
        sheet.Cell("B2").Value = new DateTime(2026, 1, 5);
        sheet.Cell("A3").Value = 1;
        sheet.Cell("B3").Value = new DateTime(2026, 2, 9);
        workbook.SaveAs(path);
        return path;
    }

    /// <summary>A SQLite database named "clinic" in sources.json, with one synthetic patient.</summary>
    private void ClinicSource()
    {
        var dbPath = Path.Combine(_data, "clinic.db");
        using (var connection = new SqliteConnection($"Data Source={dbPath};Pooling=false"))
        {
            connection.Open();
            using var command = connection.CreateCommand();
            command.CommandText =
                "CREATE TABLE patients (Mrn TEXT, FirstName TEXT, Email TEXT, Visits INTEGER); " +
                $"INSERT INTO patients VALUES ('A1', '{FirstName}', '{Email}', 3);";
            command.ExecuteNonQuery();
        }

        File.WriteAllText(Path.Combine(_folder, "sources.json"), JsonSerializer.Serialize(new
        {
            sources = new Dictionary<string, object> { ["clinic"] = new { type = "sqlite", path = dbPath } },
        }));
    }

    /// <summary>
    /// Marks every config file reviewed, standing in for the person who reviews them, after applying their
    /// corrections: each override sets the category of the column with that header.
    /// </summary>
    private void ReviewAllConfigs(params (string Header, SensitiveDataType Category)[] overrides)
    {
        foreach (var file in Directory.GetFiles(Path.Combine(_folder, "configs"), "*.json"))
        {
            var json = JsonNode.Parse(File.ReadAllText(file))!;
            foreach (var column in json["columns"]!.AsArray())
            {
                var header = column!["header"]!.GetValue<string>();
                foreach (var (overrideHeader, category) in overrides.Where(o => o.Header.Equals(header, StringComparison.OrdinalIgnoreCase)))
                {
                    column["category"] = category.ToString();
                }
            }

            json["reviewedByPerson"] = true;
            File.WriteAllText(file, json.ToJsonString());
        }
    }

    /// <summary>Describes a source and marks its configs reviewed (with any corrections), so queries return rows.</summary>
    private void Reviewed(MasqueToolService service, string source, params (string Header, SensitiveDataType Category)[] overrides)
    {
        service.Describe(source);
        ReviewAllConfigs(overrides);
    }

    /// <summary>The last Markdown table row of a reply.</summary>
    private static string LastRow(ToolReply reply) => reply.Text.Split('\n').Last(l => l.StartsWith('|')).Trim();

    private static void ShouldHoldNoRealValues(ToolReply reply)
    {
        foreach (var value in RealValues)
        {
            reply.Text.ShouldNotContain(value, Case.Insensitive);
        }
    }

    /// <summary>Describing a CSV lists its table, columns and proposed categories, and asks for review, with no values.</summary>
    [Fact]
    public void Describe_Csv_ListsColumnsAndCategoriesWithoutValues()
    {
        var reply = _service.Describe(PeopleCsv());

        reply.IsError.ShouldBeFalse();
        reply.Text.ShouldContain("people");
        reply.Text.ShouldContain("SSN");
        reply.Text.ShouldContain("needs review");
        ShouldHoldNoRealValues(reply);
    }

    /// <summary>A query on a table nobody has reviewed returns review instructions, not rows.</summary>
    [Fact]
    public void Query_BeforeReview_ReturnsReviewInstructionsOnly()
    {
        var reply = _service.Query(PeopleCsv(), "SELECT * FROM people", 50);

        reply.IsError.ShouldBeFalse();
        reply.Text.ShouldContain("review");
        reply.Text.ShouldContain(".json");
        ShouldHoldNoRealValues(reply);
    }

    /// <summary>After review, the query returns masked rows; amounts and codes stay readable.</summary>
    [Fact]
    public void Query_AfterReview_ReturnsMaskedRows()
    {
        var path = PeopleCsv();
        Reviewed(_service, path);

        var reply = _service.Query(path, "SELECT FirstName, SSN, Email, GrossPay, Status, Notes FROM people", 50);

        reply.IsError.ShouldBeFalse();
        reply.Text.ShouldContain("1234.56");
        reply.Text.ShouldContain("| A ");
        reply.Text.ShouldContain("2 rows");
        ShouldHoldNoRealValues(reply);
    }

    /// <summary>Aggregates come back readable: computed numbers pass through.</summary>
    [Fact]
    public void Query_Aggregates_AreReadable()
    {
        var path = PeopleCsv();
        Reviewed(_service, path);

        var reply = _service.Query(path, "SELECT Status, COUNT(*) AS people_count, SUM(GrossPay) AS total_pay FROM people GROUP BY Status ORDER BY Status", 50);

        reply.Text.ShouldContain("| A | 1 | 1234.56 |");
        reply.Text.ShouldContain("| T | 1 | 980 |");
    }

    /// <summary>The same real value gets the same token in every call, so results can be compared across queries.</summary>
    [Fact]
    public void Query_TokensAreStableAcrossCalls()
    {
        var path = PeopleCsv();
        Reviewed(_service, path);

        var first = _service.Query(path, "SELECT SSN FROM people WHERE PersonId = 1", 5);
        var second = _service.Query(path, "SELECT SSN AS again FROM people WHERE PersonId = 1", 5);

        LastRow(first).ShouldBe(LastRow(second));
    }

    /// <summary>An Excel workbook's sheets are tables; dates are masked.</summary>
    [Fact]
    public void Query_ExcelWorkbook_QueriesSheets()
    {
        var path = VisitsWorkbook();
        Reviewed(_service, path);

        var reply = _service.Query(path, "SELECT PersonId, VisitDate FROM Visits", 10);

        reply.IsError.ShouldBeFalse();
        reply.Text.ShouldNotContain("2026-01-05");
        reply.Text.ShouldContain("2 rows");
    }

    /// <summary>A folder is one source whose files are tables, so CSV and Excel files can be joined.</summary>
    [Fact]
    public void Query_Folder_JoinsAcrossFiles()
    {
        PeopleCsv();
        VisitsWorkbook();
        Reviewed(_service, _data);

        var reply = _service.Query(_data,
            "SELECT p.FirstName, COUNT(*) AS visit_count FROM people p JOIN visits v ON v.PersonId = p.PersonId GROUP BY p.FirstName", 10);

        reply.IsError.ShouldBeFalse();
        reply.Text.ShouldContain("| 2 |");
        ShouldHoldNoRealValues(reply);
    }

    /// <summary>A named source from sources.json is opened by name, so connection details never pass through the assistant.</summary>
    [Fact]
    public void Query_NamedSource_UsesSourcesFile()
    {
        ClinicSource();
        Reviewed(_service, "clinic");

        var reply = _service.Query("clinic", "SELECT FirstName FROM patients", 10);

        reply.IsError.ShouldBeFalse();
        reply.Text.ShouldContain("1 row");
        ShouldHoldNoRealValues(reply);
    }

    /// <summary>masque_query only reads; a write sent to it points the assistant at masque_execute and nothing runs.</summary>
    [Fact]
    public void Query_Write_PointsToExecute()
    {
        var reply = _service.Query(PeopleCsv(), $"DELETE FROM people WHERE SSN = '{Ssn}'", 10);

        reply.IsError.ShouldBeTrue();
        reply.Text.ShouldContain("masque_execute");
        ShouldHoldNoRealValues(reply);
    }

    /// <summary>A token the assistant was shown can be used in an = filter: Masque swaps it for the real value.</summary>
    [Fact]
    public void Query_TokenFilter_FindsTheRow()
    {
        var path = PeopleCsv();
        Reviewed(_service, path);
        var token = LastRow(_service.Query(path, "SELECT SSN FROM people WHERE PersonId = 1", 5)).Trim('|', ' ');

        var reply = _service.Query(path, $"SELECT PersonId FROM people WHERE SSN = '{token}'", 5);

        reply.IsError.ShouldBeFalse(reply.Text);
        reply.Text.ShouldContain("1 row", customMessage: $"token '{token}'");
        ShouldHoldNoRealValues(reply);
    }

    /// <summary>Sorting by a masked column would reveal its order, so it is refused without showing values.</summary>
    [Fact]
    public void Query_OrderByMaskedColumn_IsRefused()
    {
        var path = PeopleCsv();
        Reviewed(_service, path);

        var reply = _service.Query(path, "SELECT PersonId FROM people ORDER BY SSN", 5);

        reply.IsError.ShouldBeTrue();
        ShouldHoldNoRealValues(reply);
    }

    /// <summary>A type the policy lets the assistant see comes back raw.</summary>
    [Fact]
    public void Query_PolicyAllowsType_ShowsItRaw()
    {
        ClinicSource();
        using var service = ServiceWithPolicy(AssistantSeesEmail);
        Reviewed(service, "clinic");

        service.Query("clinic", "SELECT Email FROM patients", 5).Text.ShouldContain(Email);
    }

    /// <summary>allowTypes narrows the policy for one query: a type left out of the list is masked again.</summary>
    [Fact]
    public void Query_AllowTypes_NarrowsThePolicy()
    {
        ClinicSource();
        using var service = ServiceWithPolicy(AssistantSeesEmail);
        Reviewed(service, "clinic");

        var reply = service.Query("clinic", "SELECT Email FROM patients", 5, ["Ssn"]);

        reply.IsError.ShouldBeFalse();
        ShouldHoldNoRealValues(reply);
    }

    /// <summary>allowTypes never widens: asking for a type the policy doesn't grant still gets it masked.</summary>
    [Fact]
    public void Query_AllowTypes_CannotWiden()
    {
        ClinicSource();
        Reviewed(_service, "clinic");

        var reply = _service.Query("clinic", "SELECT FirstName, Email FROM patients", 5, ["Email", "Name"]);

        reply.IsError.ShouldBeFalse();
        ShouldHoldNoRealValues(reply);
    }

    /// <summary>An unknown name in allowTypes is an error, so a typo isn't silently ignored.</summary>
    [Fact]
    public void Query_UnknownAllowType_IsAnError()
    {
        var reply = _service.Query(PeopleCsv(), "SELECT PersonId FROM people", 5, ["Emial"]);

        reply.IsError.ShouldBeTrue();
        reply.Text.ShouldContain("Emial");
    }

    /// <summary>With no policy file the assistant is locked: writes are refused and nothing changes.</summary>
    [Fact]
    public void Execute_LockedPolicy_IsRefused()
    {
        ClinicSource();
        Reviewed(_service, "clinic");

        var reply = _service.Execute("clinic", "DELETE FROM patients");

        reply.IsError.ShouldBeTrue();
        reply.Text.ShouldContain("may not write");
        _service.Query("clinic", "SELECT COUNT(*) AS patient_count FROM patients", 5).Text.ShouldContain("| 1 |");
    }

    /// <summary>
    /// When the policy lets the assistant write, a write runs and reports the rows changed, never data. Visits holds
    /// bare numbers, which no detector recognizes, so it is proposed as free text (fail closed); the reviewer marks it
    /// Safe, as a person would.
    /// </summary>
    [Fact]
    public void Execute_PolicyAllowsWrites_Runs()
    {
        ClinicSource();
        using var service = ServiceWithPolicy(AssistantCanWrite);
        Reviewed(service, "clinic", ("Visits", SensitiveDataType.Safe));

        var reply = service.Execute("clinic", "UPDATE patients SET Visits = Visits + 1");

        reply.IsError.ShouldBeFalse(reply.Text);
        reply.Text.ShouldContain("1 row changed");
        ShouldHoldNoRealValues(reply);
        service.Query("clinic", "SELECT SUM(Visits) AS total_visits FROM patients", 5).Text.ShouldContain("| 4 |");
    }

    /// <summary>
    /// Masque only prevents exposure, so with write access the assistant may set a masked column to a value it
    /// supplies; the reply reports the count, never data.
    /// </summary>
    [Fact]
    public void Execute_UpdateOfMaskedColumnWithSuppliedValue_Runs()
    {
        ClinicSource();
        using var service = ServiceWithPolicy(AssistantCanWrite);
        Reviewed(service, "clinic");

        var reply = service.Execute("clinic", "UPDATE patients SET Email = 'x@example.net'");

        reply.IsError.ShouldBeFalse(reply.Text);
        reply.Text.ShouldContain("1 row changed");
        ShouldHoldNoRealValues(reply);
    }

    /// <summary>Copying a masked column into a readable one is still refused: that would expose it.</summary>
    [Fact]
    public void Execute_CopyOfMaskedColumn_IsRefused()
    {
        ClinicSource();
        using var service = ServiceWithPolicy(AssistantCanWrite);
        Reviewed(service, "clinic", ("Visits", SensitiveDataType.Safe));

        var reply = service.Execute("clinic", "UPDATE patients SET Visits = Email");

        reply.IsError.ShouldBeTrue();
        ShouldHoldNoRealValues(reply);
    }

    /// <summary>CSV and Excel sources are an in-memory copy, so writes to them are refused with a pointer to the file.</summary>
    [Fact]
    public void Execute_FileCopy_IsRefused()
    {
        using var service = ServiceWithPolicy(AssistantCanWrite);

        var reply = service.Execute(PeopleCsv(), "DELETE FROM people");

        reply.IsError.ShouldBeTrue();
        reply.Text.ShouldContain("CSV or Excel");
    }

    /// <summary>masque_execute doesn't read; a SELECT sent to it points the assistant at masque_query.</summary>
    [Fact]
    public void Execute_Select_PointsToQuery()
    {
        var reply = _service.Execute(PeopleCsv(), "SELECT * FROM people");

        reply.IsError.ShouldBeTrue();
        reply.Text.ShouldContain("masque_query");
    }

    /// <summary>An unknown source is a clear error that lists what a source can be.</summary>
    [Fact]
    public void Describe_UnknownSource_IsAClearError()
    {
        var reply = _service.Describe(Path.Combine(_folder, "missing.csv"));

        reply.IsError.ShouldBeTrue();
        reply.Text.ShouldContain("source");
    }

    /// <summary>A query mistake comes back as an error the assistant can act on.</summary>
    [Fact]
    public void Query_Mistake_ReturnsReadableError()
    {
        var path = PeopleCsv();
        Reviewed(_service, path);

        var reply = _service.Query(path, "SELECT nope FROM people", 10);

        reply.IsError.ShouldBeTrue();
        reply.Text.ShouldContain("nope");
    }

    /// <summary>A file whose first row is data (no header row) is refused without printing that row.</summary>
    [Fact]
    public void Describe_HeaderlessFile_IsRefusedWithoutValues()
    {
        var path = Path.Combine(_data, "raw.csv");
        File.WriteAllText(path, $"{Ssn},{Email}\n987-65-4321,lee@example.org\n");

        var reply = _service.Describe(path);

        reply.IsError.ShouldBeTrue();
        ShouldHoldNoRealValues(reply);
    }
}
