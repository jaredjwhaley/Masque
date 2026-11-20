# Masque

Masque is a local layer between people or AI assistants and sensitive data. It replaces PII (personally identifiable information) and PHI (protected health information) with consistent fake values, called tokens, before anyone sees them. The same real value always becomes the same token, so joins, counts and "is this the same person?" still work. Real values never leave your machine.

> **Status:** early (0.1.0), Windows first. Three ways in:
> - the `masque` command masks CSV files;
> - the `Masque.Data` and `Masque.EntityFrameworkCore` libraries mask query results inside a .NET app (ADO.NET, Dapper, EF Core);
> - the `masque-mcp` server lets an AI assistant query CSV, Excel, SQLite and SQL Server tables and see only masked results.

> **Not a compliance certification.** Masque lowers the risk of exposing data; it does not by itself make data "de-identified" under HIPAA or any other law. Detection of free text is best-effort. Before you use it on real data, confirm with your compliance team that your organization allows tokenized data to reach your AI vendor.

## The rule

Masque sees everything; the caller (a person or an AI) never sees a sensitive value it isn't allowed to see. Masque prevents exposure, not change: whoever drives the assistant may read the raw data themselves, so writes are fine when the policy allows them.

- **Reads:** sensitive columns come back as tokens, unless the caller's policy shows that type raw.
- **Inserts and updates** may set a sensitive column to a single value the caller supplies (a quoted value, a number, `NULL` or a named parameter), if the policy allows writes.
- **Tokens written back** stand for their real value: a token written as the whole value of a sensitive column of its type is stored as the real value it stands for. A token written anywhere else (another column, inside an expression, an `INSERT` without a column list) is refused, since it would be stored as a fake.
- **Deletes:** allowed, if the policy allows writes.
- **Copying** a sensitive column into another column (`INSERT ... SELECT`, `SET Notes = SSN`) is refused, since it would launder the value into a readable column.

Which columns are sensitive is decided once per table shape, in a config a person reviews (see [Table configs](#table-configs)), never by the code that runs the query.

## Mask a CSV file

```bash
masque people.csv
```

The first time Masque sees a set of columns, it proposes a category for each one, saves the proposal as a config and stops (exit code 2):

```
Columns in people.csv (shape ab38dfa71c868656, proposed):
  EmployeeID    Identifier   header "EmployeeID" suggests Identifier
  First Name    Name         header "First Name" suggests Name
  SSN           Ssn          header "SSN" suggests Ssn
  GrossPay      Money        header "GrossPay" suggests Money
  Notes         FreeText     no header or value pattern matched; masked as free text
```

Edit any category in the config file it names, then mark it reviewed:

```bash
masque people.csv --accept
```

It writes `people.masque.csv`. Files with the same columns run without stopping from then on. Masque never prints a data value, only headers, categories, paths and counts, so its output is safe for an AI assistant to read.

| Option | Default |
| --- | --- |
| `--out <path>` | `<file>.masque.csv` |
| `--accept` | Mark the proposed config as reviewed |
| `--vault <path>` | `%LOCALAPPDATA%\Masque\vault.db` |
| `--configs <dir>` | `%LOCALAPPDATA%\Masque\configs` |

Exit codes: 0 done, 1 error, 2 review needed, 64 bad arguments.

### Install from source

```bash
dotnet pack src/S03_Tools/C004_Cli -o nupkg
dotnet tool install --global Masque --add-source nupkg
```

## Use it in a .NET app

### ADO.NET and Dapper

`Masque.Data` wraps any ADO.NET connection (decorators around `DbConnection`, `DbCommand` and `DbDataReader`). Code written against `DbConnection`, including Dapper, works unchanged and gets masked results.

```csharp
using var gate = MasqueGate.Create(new MasqueOptions { Dialects = { new SqliteDialect() } });
using var connection = new SqliteConnection("Data Source=clinic.db").WithMasque(gate);

var people = connection.Query<Person>("SELECT Id, FullName, Ssn FROM People"); // FullName and Ssn are tokens
```

With dependency injection, register one gate for the application and wrap each connection with it:

```csharp
services.AddMasque(o => o.Dialects.Add(new SqlServerDialect()));
// later: var connection = rawConnection.WithMasque(gate);
```

A **dialect** tells Masque about one kind of database: how to list its tables, how to trace a result column back to its table column, and how to guard the connection. `SqliteDialect` (in `Masque.Sqlite`) and `SqlServerDialect` (in `Masque.SqlServer`) ship today.

### EF Core

`Masque.EntityFrameworkCore` registers Masque as EF Core interceptors (hooks EF Core calls before it runs a command, opens a connection or saves):

```csharp
options.UseSqlite(connectionString).UseMasque(gate);
```

- LINQ and raw SQL results come back with sensitive properties holding tokens.
- A token you were shown works in a filter: `db.People.Where(p => p.Ssn == token)` finds the real row.
- Queries that would reveal masked values are refused with `QueryRejectedException`, such as sorting by a masked column.
- `SaveChanges` refuses to change a property mapped to a masked column, because the loaded entity holds a token, not the real value. `context.Update(entity)` marks every property changed, so it is refused too. Changing only unmasked properties, adding and removing entities all work.
- `ExecuteUpdate` and `ExecuteDelete` get the same checks as any SQL.
- Migrations and `EnsureCreated` don't run through a Masque context (schema changes are refused); run them on a plain context.

### Narrowing what one query sees

A caller's policy sets the most they may see raw. Code can only narrow it, never widen it:

- per connection or context: `WithMasque(gate, allowRaw: [SensitiveDataType.Email])`, `UseMasque(gate, allowRaw: [...])`;
- per command: `MaskingDbCommand.AllowedRawTypes`;
- per query, in the SQL itself: a `-- masque: allow Email` comment, which is what EF Core's `TagWith("masque: allow Email")` writes.

## Access policies

A policy file says, for each caller, which sensitive types they see raw and whether they may write. The caller is the signed-in Windows user and their groups (the MCP server uses the caller name `assistant`).

```json
{ "default": { "rawTypes": [], "canWrite": false },
  "rules": [ { "name": "hr", "users": [], "groups": ["CONTOSO\\HR"], "rawTypes": ["Name"], "canWrite": true } ] }
```

Masque reads the machine-wide file an administrator controls (`%ProgramData%\Masque\policy.json`) if it exists, else the user's own (`policy.json` in the Masque folder), else applies a locked policy: nothing raw, no writes. A file that can't be read stops Masque rather than being guessed at.

## Table configs

Each table shape (its set of column headers) gets a config file in the configs folder, listing every column's category and why. Masque proposes one the first time it sees a shape, from the headers and a sample of values; nothing is returned from that table until a person reviews it and sets `"reviewedByPerson": true` (or runs `masque --accept` for a CSV file). Anything the detectors can't place is masked as free text ("fail closed").

## Query tables through an assistant

`masque-mcp` is an MCP (Model Context Protocol) server: a local program an AI assistant calls as a tool. It runs every call through the same gate as the libraries, as the caller `assistant`.

| Tool | What it does |
| --- | --- |
| `masque_describe(source)` | Lists tables, columns and each column's category. No values. Proposes a config for any new table shape. |
| `masque_query(source, sql, maxRows, allowTypes)` | Runs one `SELECT` (or `WITH ... SELECT`) and returns the rows masked. `allowTypes` can narrow, never widen, what the policy shows raw. At most 1,000 rows. |
| `masque_execute(source, sql)` | Runs one `INSERT`, `UPDATE` or `DELETE` on a SQLite or SQL Server source, if the policy lets the assistant write. Returns a row count, never data. |

Keep `masque_execute` behind your assistant's approval prompt (in Claude Code, a permission rule that asks before each call), so a person sees every write.

A **source** is a CSV/TSV file, an Excel workbook (each sheet is a table), a SQLite database, a folder of CSV and Excel files (each file is a table, so they can be joined), or a name from `sources.json` (for SQL Server, so connection strings never pass through the assistant):

```json
{ "sources": {
    "hr":     { "type": "sqlServer", "connectionString": "Server=.;Database=Hr;Integrated Security=true;TrustServerCertificate=true" },
    "clinic": { "type": "sqlite",    "path": "C:\\data\\clinic.db" },
    "drops":  { "type": "file",      "path": "C:\\data\\drops" } } }
```

CSV and Excel tables are loaded into a private in-memory SQLite database for each call, so one SQL dialect covers every file type. They are read-only.

**Register it with Claude Code** (build first: `dotnet build src/S03_Tools/C008_Mcp -c Release`):

```bash
claude mcp add masque --scope user -e MASQUE_HOME=C:\Users\you\.masque -- dotnet <repo>/src/S03_Tools/C008_Mcp/bin/Release/net10.0/Masque.Mcp.dll
```

Set `MASQUE_HOME` to the same folder for the `masque` CLI too (a user environment variable), so both share one vault, one set of configs and one policy. Packaged apps such as Claude Desktop on Windows redirect `%LOCALAPPDATA%` to a private folder, so without it they would get a separate vault and different tokens.

## How a query is checked and masked

1. **Check.** The SQL is parsed before it runs. Masque accepts one `SELECT`, `INSERT`, `UPDATE` or `DELETE` (optionally after `WITH`), and refuses schema changes, procedures, `ATTACH`, `SELECT INTO`, set operators (`UNION` and the like) and `OUTPUT`/`RETURNING`. On a masked column it refuses anything that would reveal the value: sorting, `LIKE`, ranges, functions other than `COUNT`, arithmetic, comparison with an unmasked column, setting it to anything but a single supplied value, and copying into another table or column.
2. **Token filters.** A token compared with a masked column (`WHERE Ssn = '912-34-5678'`, as text or as a parameter) is swapped for the real value before the query runs, in every spelling Masque has seen it stored (`123-45-6789`, `123 45 6789`), so lookups and joins across sources line up.
3. **Run.** The database's own guard backs up the check: on SQLite, an authorizer refuses schema changes, `ATTACH` and any write Masque didn't allow. On SQL Server, use a login with least privilege (`DENY UPDATE` on sensitive columns).
4. **Mask.** The database reports, for each result column, the table column it came from (its *lineage*). That column's reviewed category decides the masking, so renaming a column in the query (`SELECT SSN AS x`) changes nothing. A computed column (an aggregate or expression) has no lineage: numbers are shown and text is masked. A readable column whose values look sensitive (an SSN in a "Status" column) is masked anyway.
5. **Errors.** Database errors that could quote a value are replaced with a safe message.

**Known limits.**
- A computed expression deliberately disguised as an amount (`SSN_number + 0 AS total`) can pass through; Masque guards against accidents, not a hostile query writer.
- A token filter matches only spellings Masque has already shown. Positional `?` parameters aren't swapped.
- Tokens are recognized only as a whole written value; a token inside longer text (`'Call <token> today'`) is stored as written. When a token was seen in several spellings, the first in ordinal order is written. A real value that happens to equal a token of the same type is treated as that token.
- SQL Server write checks rely on the token-level SQL check and database permissions; a full T-SQL parser (ScriptDom) is planned.
- Excel number formats are ignored (a ZIP stored as a number loses its leading zero), and a title row above the header isn't detected. Files are reloaded on every call.

## Token formats

| Category | Example token | Why it's safe |
| --- | --- | --- |
| SSN | `912-34-5678` | Numbers starting with 9 are never issued |
| Email | `user123456@example.invalid` | `.invalid` is a reserved domain |
| Phone | `012-345-6789` | North American area codes never start with 0 |
| Name | `Avery Brooks` | Drawn from a made-up name list |
| Identifier | `AB-1234` becomes `QX-8051` | Same length, case and punctuation |
| ZIP | `75201` becomes `752**` | HIPAA Safe Harbor: 3 digits; small areas become `000**` |
| Money, codes | unchanged | Calculations must still reproduce |
| Anything else | `[ADDRESS-1A2B3C4D]` | Redacted until a specific rule exists |

## The vault

The vault is a local SQLite file (`Masque.Vault`). No real value in it is readable:

| Column | Holds | Purpose |
| --- | --- | --- |
| `value_index` | HMAC-SHA256 of the value, under the vault's own key | Finds a value without storing it; can't be reversed or guessed without the key |
| `value_cipher` | The value encrypted with AES-GCM, bound to its row | Lets `reveal` recover the real value |
| `token` | The fake value | What the caller sees |

Values are stored normalized (an SSN as its digits). Each spelling seen in the data is kept in a `spellings` table, indexed and encrypted the same way and bound to its token, so token filters can match how the data is actually stored.

Each vault makes its own random 256-bit key and stores it wrapped by the OS secret store, so only the user who made the vault can open it. Windows uses DPAPI; macOS Keychain and Linux libsecret are planned. Tokens themselves are random, never derived from the real value. A size-limited in-memory cache sits in front of the vault.

```csharp
using var vault = SqliteTokenVault.Open(path, KeyProtectors.ForCurrentPlatform());
var tokenizer = Tokenizer.CreateDefault(vault, CryptoRandomSource.Shared);
tokenizer.Tokenize(SensitiveDataType.Ssn, "123-45-6789"); // same token every run
```

## Build and test

Requires the .NET 10 SDK.

```bash
dotnet build Masque.slnx
dotnet test Masque.slnx
```

## Layout

```
src/S01_Core/C001_Core/                        Masque.Core: types, detectors, configs, policy, SQL check, tokenizer, vault contract
src/S01_Core/C002_Vault/                       Masque.Vault: encrypted SQLite vault, OS key protection
src/S01_Core/C009_Data/                        Masque.Data: the gate and ADO.NET decorators, dialect contract, DI registration
src/S02_Adapters/C003_Csv/                     Masque.Csv: CSV sampling and masked copies (CsvHelper)
src/S02_Adapters/C005_Excel/                   Masque.Excel: workbook sheets as tables (ExcelDataReader)
src/S02_Adapters/C006_Sqlite/                  Masque.Sqlite: SQLite dialect; hosts CSV/Excel tables in memory
src/S02_Adapters/C007_SqlServer/               Masque.SqlServer: SQL Server dialect
src/S02_Adapters/C010_EntityFrameworkCore/     Masque.EntityFrameworkCore: UseMasque interceptors for EF Core
src/S03_Tools/C004_Cli/                        Masque.Cli: the `masque` command (packs as a dotnet tool)
src/S03_Tools/C008_Mcp/                        Masque.Mcp: the `masque-mcp` MCP server (packs as a dotnet tool)
tests/<same sections>/<project>.Tests/         xUnit v3 + Shouldly tests, one project per source project
```

SQL Server tests need LocalDB or a `MASQUE_TEST_SQLSERVER` connection string, and are skipped otherwise. Folders follow `<Area>/SNN_Section/CNNN_Name/`; assemblies keep clean names (`Masque.Core`).

## Roadmap

1. **Core** (in progress): tokenizer, rules, in-memory and encrypted SQLite vaults with Windows key protection, access policies. Next: macOS Keychain and Linux libsecret key protectors, date shifting per person, a vault a team can share.
2. **CSV and CLI** (done): `masque file.csv` writes `file.masque.csv`; header and value detectors; saved configs with a review step. Dates are fully redacted until date shifting lands.
3. **Data access** (first cut done): the ADO.NET gate, SQLite and SQL Server dialects, EF Core interceptors, token filters. Writes follow the exposure-only rule, with tokens swapped back to real values. Next: writes to CSV and Excel, ScriptDom for SQL Server writes, Excel title rows and number formats, caching loaded files.
4. **MCP server and hooks:** `masque_describe`, `masque_query` and `masque_execute` done. Next: hooks that block raw reads of data files, and a review step the assistant can't perform itself.
5. **Free text:** NER, a clipboard command, and a prompt-submit hook.
6. **PDF** extraction.
7. **Reveal** command: turns tokens in an assistant's answer back into real values, for you only.
8. **Gateway mode:** a local proxy for assistants that support a custom API base URL.

## Prior art

[PII Shield](https://github.com/UseJunior/PII-Shield), [PrivAiTe](https://pypi.org/project/privaite/), [LLM Guard](https://protectai.github.io/llm-guard/input_scanners/anonymize) and [Microsoft Presidio](https://microsoft.github.io/presidio/) each cover part of this. Masque's focus is what they leave out: column-aware tables (CSV, Excel, SQL), masking inside ordinary .NET data access, tokens that stay the same across files and sessions, realistic format-preserving fakes, HIPAA Safe Harbor rules, and enforcement through policies and hooks.

## License

[MIT](LICENSE)
