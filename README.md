# Masque

Masque is a local layer between an AI assistant and sensitive data. It replaces PII (personally identifiable information) and PHI (protected health information) with consistent fake values before the assistant sees them. The same real value always becomes the same fake one, so joins, counts and "is this the same person?" still work. Real values never leave your machine.

> **Status:** early (0.1.0). The `masque` command masks CSV files end to end on Windows. The `masque-mcp` server lets an AI assistant run read-only SQL over CSV, Excel, SQLite and SQL Server tables and see only masked results (see [Query tables through an assistant](#query-tables-through-an-assistant)).

> **Not a compliance certification.** Masque lowers the risk of exposing data; it does not by itself make data "de-identified" under HIPAA or any other law. Detection of free text is best-effort. Before you use it on real data, confirm with your compliance team that your organization allows tokenized data to reach your AI vendor.

## Usage

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

Edit any category in the config file it names, then confirm:

```bash
masque people.csv --accept
```

It writes `people.masque.csv`. Files with the same columns run without stopping from then on. Masque never prints a data value, only headers, categories, paths and counts, so its output is safe for an AI assistant to read.

| Option | Default |
| --- | --- |
| `--out <path>` | `<file>.masque.csv` |
| `--accept` | Confirm a proposed config |
| `--vault <path>` | `%LOCALAPPDATA%\Masque\vault.db` |
| `--configs <dir>` | `%LOCALAPPDATA%\Masque\configs` |

Exit codes: 0 done, 1 error, 2 review needed, 64 bad arguments.

### Install from source

```bash
dotnet pack src/S03_Tools/C004_Cli -o nupkg
dotnet tool install --global Masque --add-source nupkg
```

## Query tables through an assistant

`masque-mcp` is an MCP (Model Context Protocol) server: a local program an AI assistant calls as a tool. It gives the assistant two tools:

| Tool | What it does |
| --- | --- |
| `masque_describe(source)` | Lists tables, columns and each column's category. No values. Proposes a config for any new table shape. |
| `masque_query(source, sql, maxRows)` | Runs one read-only `SELECT` and returns the rows masked: tokens for sensitive values, real values for amounts, counts and codes. |

A **source** is a CSV/TSV file, an Excel workbook (each sheet is a table), a SQLite database, a folder of CSV and Excel files (each file is a table, so they can be joined), or a name from `sources.json` (for SQL Server, so connection strings never pass through the assistant):

```json
{ "sources": {
    "hr":     { "type": "sqlServer", "connectionString": "Server=.;Database=Hr;Integrated Security=true;TrustServerCertificate=true" },
    "clinic": { "type": "sqlite",    "path": "C:\\data\\clinic.db" },
    "drops":  { "type": "file",      "path": "C:\\data\\drops" } } }
```

CSV and Excel tables are loaded into a private in-memory SQLite database for each call, so one SQL dialect covers every file type.

**How a result is masked.** The database reports, for each result column, the table column it came from (its *lineage*). That column's confirmed category decides the masking, so renaming a column in the query (`SELECT SSN AS x`) changes nothing. A computed column (an aggregate or expression) has no lineage: it is classified from its name and values and may come back readable only as a count (a name containing "count", whole numbers) or an amount (a name like `total_pay`, numeric values). A readable column whose values don't fit its category (an SSN in a "Status" column) is masked anyway.

**Safeguards.** One `SELECT` (or `WITH ... SELECT`) per call; writes, procedures, `ATTACH`, `INTO` and `UNION`/`INTERSECT`/`EXCEPT` are refused before anything runs. SQLite sources are opened read-only with `ATTACH` disabled; SQL Server queries run in a transaction that is always rolled back (still use a read-only login). At most 1,000 rows per call. Database errors that can quote a value are hidden. A new table shape returns no rows until a person reviews its config and sets `"confirmed": true`.

**Register it with Claude Code** (build first: `dotnet build src/S03_Tools/C008_Mcp -c Release`):

```bash
claude mcp add masque --scope user -e MASQUE_HOME=C:\Users\you\.masque -- dotnet <repo>/src/S03_Tools/C008_Mcp/bin/Release/net10.0/Masque.Mcp.dll
```

Set `MASQUE_HOME` to the same folder for the `masque` CLI too (a user environment variable), so both share one vault and one set of configs. Packaged apps such as Claude Desktop on Windows redirect `%LOCALAPPDATA%` to a private folder, so without it they would get a separate vault and different tokens.

**Known limits.** A computed expression deliberately disguised as an amount (`SSN_number + 0 AS total`) can pass through; Masque guards against accidents, not a hostile query writer. Filtering by a token (`WHERE Name = 'Avery Brooks'`) finds nothing yet. Excel number formats are ignored (a ZIP stored as a number loses its leading zero), and a title row above the header isn't detected. Files are reloaded on every call.

## How it works

1. **Adapters** read CSV, Excel, PDF, pasted text or SQL results into a table or a block of text.
2. **Detectors** give each column a category (Name, SSN, Email, ...) from its header, its values and, for free text, named-entity recognition (NER). Anything unknown is masked ("fail closed").
3. **Saved configs** remember the categories for each header layout, so a file shape is reviewed once.
4. **The tokenizer** swaps each value for a token, using the **vault** (a local, encrypted map of real value to token) so the same value always gets the same token.
5. **The assistant** reaches data only through Masque's MCP tools; hooks block raw file and database reads. A local `reveal` command turns tokens in the assistant's answers back into real values, for you only.

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
| `token` | The fake value | What the assistant sees |

Each vault makes its own random 256-bit key and stores it wrapped by the OS secret store, so only the user who made the vault can open it. Windows uses DPAPI; macOS Keychain and Linux libsecret are planned. Tokens themselves are random, never derived from the real value.

```csharp
using var vault = SqliteTokenVault.Open(path, KeyProtectors.ForCurrentPlatform());
var tokenizer = Tokenizer.CreateDefault(vault, CryptoRandomSource.Shared);
tokenizer.Tokenize(PiiCategory.Ssn, "123-45-6789"); // same token every run
```

## Build and test

Requires the .NET 10 SDK.

```bash
dotnet build Masque.slnx
dotnet test Masque.slnx
```

## Layout

```
src/S01_Core/C001_Core/              Masque.Core: categories, rules, tokenizer, vault contract, in-memory vault
src/S01_Core/C002_Vault/             Masque.Vault: encrypted SQLite vault, OS key protection
src/S02_Adapters/C003_Csv/           Masque.Csv: CSV sampling and masked copies (CsvHelper)
src/S02_Adapters/C005_Excel/         Masque.Excel: workbook sheets as tables (ExcelDataReader)
src/S02_Adapters/C006_Sqlite/        Masque.Sqlite: SQLite query source; hosts CSV/Excel tables in memory
src/S02_Adapters/C007_SqlServer/     Masque.SqlServer: SQL Server query source
src/S03_Tools/C004_Cli/              Masque.Cli: the `masque` command (packs as a dotnet tool)
src/S03_Tools/C008_Mcp/              Masque.Mcp: the `masque-mcp` MCP server (packs as a dotnet tool)
tests/S01_Core/C001_Core.Tests/      xUnit v3 + Shouldly tests for Masque.Core (incl. the shared vault contract)
tests/S01_Core/C002_Vault.Tests/     Tests for Masque.Vault
tests/S02_Adapters/C003_Csv.Tests/   Tests for Masque.Csv
tests/S02_Adapters/C005_Excel.Tests/ Tests for Masque.Excel
tests/S02_Adapters/C006_Sqlite.Tests/ Tests for Masque.Sqlite
tests/S02_Adapters/C007_SqlServer.Tests/ Tests for Masque.SqlServer (need LocalDB or MASQUE_TEST_SQLSERVER; skipped otherwise)
tests/S03_Tools/C004_Cli.Tests/      End-to-end tests for the command
tests/S03_Tools/C008_Mcp.Tests/      End-to-end tests for the MCP tools
```

Folders follow `<Area>/SNN_Section/CNNN_Name/`; assemblies keep clean names (`Masque.Core`).

## Roadmap

1. **Core** (in progress): tokenizer, rules, in-memory vault and encrypted SQLite vault with Windows key protection are done. Next: macOS Keychain and Linux libsecret key protectors, date shifting per person.
2. **CSV and CLI** (done): `masque file.csv` writes `file.masque.csv`; header and value detectors; saved configs with a review step. Dates are fully redacted until date shifting lands.
3. **Excel and SQL** (first cut done): masked queries over CSV, Excel, SQLite and SQL Server with column lineage. Next: title-row detection and number formats in Excel, token-aware filters (a token in a `WHERE` matches its real value), caching loaded files.
4. **MCP server and hooks:** `masque_describe` and `masque_query` done. Next: hooks that block raw reads of data files, and a way to confirm configs that the assistant can't use.
5. **Free text:** NER, a clipboard command, and a prompt-submit hook.
6. **PDF** extraction.
7. **Reveal** command.
8. **Gateway mode:** a local proxy for assistants that support a custom API base URL.

## Prior art

[PII Shield](https://github.com/UseJunior/PII-Shield), [PrivAiTe](https://pypi.org/project/privaite/), [LLM Guard](https://protectai.github.io/llm-guard/input_scanners/anonymize) and [Microsoft Presidio](https://microsoft.github.io/presidio/) each cover part of this. Masque's focus is what they leave out: column-aware tables (CSV, Excel, SQL), tokens that stay the same across files and sessions, realistic format-preserving fakes, HIPAA Safe Harbor rules, and enforcement through hooks.

## License

[MIT](LICENSE)
