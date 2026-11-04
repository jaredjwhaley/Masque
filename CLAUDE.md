# Masque: instructions for Claude

Open-source (MIT) local PII/PHI tokenizer that sits between AI assistants and sensitive data. Design and roadmap: `README.md`.

## How we work (Jared, 2026-11-04)
Jared steers. Claude built the first skeleton (through v0.1.0) alone; from here on:
- Before new code, Claude explains the design or change in plain words and waits for Jared's OK.
- Work in small steps Jared can review: one behaviour (test + code) at a time, not whole milestones.
- Jared may write code himself; Claude reviews it the way a senior engineer would (firm, specific, encouraging).
- Gaps Jared and Claude find together go in the README roadmap or an issue, not straight into code.

## Hard rules
- **Synthetic data only.** Never put real PII, PHI, secrets or employer data in code, tests, fixtures, docs, commits or issues. Use obviously fake values (`123-45-6789`, `pat@example.com`).
- **Generic.** No organization, client, vendor, schema or AI-vendor specifics in code. Anything shape-specific belongs in user configs, never in the repo.
- **Fail closed.** When unsure what a value is, mask it. A change that lets unknown data pass through needs Jared's explicit OK.
- **Never print or log a data value.** Console output, exception messages and config files hold headers, categories, paths, row numbers and counts only. Third-party exceptions can quote raw data (CsvHelper's include the whole row): catch them, rethrow a safe message, attach no inner exception. Every new command gets a test asserting its output has no values.
- Tokens must never be valid real-world values (900-range SSNs, `.invalid` email, 0xx area codes). Keep that property in any new rule and test it.

## Standards
- TDD: write the failing test first; `dotnet test Masque.slnx` must pass before a commit.
- XML docs on all public and protected members (the build fails without them); a short summary on private helpers.
- One `ITokenRule` per category (Strategy pattern); add a category by adding a rule, not by editing the tokenizer.
- Layout: `<Area>/SNN_Section/CNNN_Name/CNNN_Name.csproj`, with `Masque.slnx` folders mirroring disk. Set `AssemblyName`/`RootNamespace` to the clean `Masque.<Part>` name.
- Tests: xUnit v3 + Shouldly (MIT; FluentAssertions 8 needs a paid license for commercial use, which would burden adopters).

## Git
- Default branch `main`. No remote yet: creating the GitHub repo (and making it public) is Jared's call.
- Jared reviews every commit message before any push.
