# Masque: instructions for Claude

Open-source (MIT) local PII/PHI tokenizer that sits between AI assistants and sensitive data. Design and roadmap: `README.md`.

## Hard rules
- **Synthetic data only.** Never put real PII, PHI, secrets or employer data in code, tests, fixtures, docs, commits or issues. Use obviously fake values (`123-45-6789`, `pat@example.com`).
- **Generic.** No organization, client, vendor, schema or AI-vendor specifics in code. Anything shape-specific belongs in user configs, never in the repo.
- **Fail closed.** When unsure what a value is, mask it. A change that lets unknown data pass through needs Jared's explicit OK.
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
