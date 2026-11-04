# Masque

Masque is a local layer between an AI assistant and sensitive data. It replaces PII (personally identifiable information) and PHI (protected health information) with consistent fake values before the assistant sees them. The same real value always becomes the same fake one, so joins, counts and "is this the same person?" still work. Real values never leave your machine.

> **Status:** early. The tokenizer, its rules and the encrypted vault are built and tested. File, SQL and assistant integration come next (see [Roadmap](#roadmap)).

> **Not a compliance certification.** Masque lowers the risk of exposing data; it does not by itself make data "de-identified" under HIPAA or any other law. Detection of free text is best-effort. Before you use it on real data, confirm with your compliance team that your organization allows tokenized data to reach your AI vendor.

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
tests/S01_Core/C001_Core.Tests/      xUnit v3 + Shouldly tests for Masque.Core (incl. the shared vault contract)
tests/S01_Core/C002_Vault.Tests/     Tests for Masque.Vault
```

Folders follow `<Area>/SNN_Section/CNNN_Name/`; assemblies keep clean names (`Masque.Core`).

## Roadmap

1. **Core** (in progress): tokenizer, rules, in-memory vault and encrypted SQLite vault with Windows key protection are done. Next: macOS Keychain and Linux libsecret key protectors, date shifting per person.
2. **CSV and CLI:** `masque file.csv` writes `file.masque.csv`; header and value detectors; saved configs.
3. **Excel and SQL:** sheet adapter with header-row detection; SQL result adapter.
4. **MCP server and hooks:** `read_anonymized`, `query_anonymized`; hooks that block raw reads.
5. **Free text:** NER, a clipboard command, and a prompt-submit hook.
6. **PDF** extraction.
7. **Reveal** command.
8. **Gateway mode:** a local proxy for assistants that support a custom API base URL.

## Prior art

[PII Shield](https://github.com/UseJunior/PII-Shield), [PrivAiTe](https://pypi.org/project/privaite/), [LLM Guard](https://protectai.github.io/llm-guard/input_scanners/anonymize) and [Microsoft Presidio](https://microsoft.github.io/presidio/) each cover part of this. Masque's focus is what they leave out: column-aware tables (CSV, Excel, SQL), tokens that stay the same across files and sessions, realistic format-preserving fakes, HIPAA Safe Harbor rules, and enforcement through hooks.

## License

[MIT](LICENSE)
