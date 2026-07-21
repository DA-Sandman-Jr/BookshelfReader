# BookshelfReader - Agent Instructions

<!-- B44 ORGANIZATION GUIDANCE: START -->
## B44 Organization Guidance

- `AGENTS.md` files are auto-generated from sibling `CLAUDE.md` by the opt-in `B44.Standards` build target. Edit the `CLAUDE.md`, not the `AGENTS.md`.
- Before editing or reviewing a file, read and follow every applicable `CLAUDE.md` from the repository root through that file's directory. Nearer instructions override broader instructions.
- Analyzer severities live in the `B44.Standards` packaged globalconfig, never in a repository `.editorconfig`. Repository editorconfigs own style and whitespace only; tune analyzer policy upstream in the package.
- Public server/function and endpoint-owning projects set `<B44SecuritySensitive>true</B44SecuritySensitive>` in `Directory.Build.props`; B44.Standards then enables the complete SDK Security category at a target-level-pinned rule set.
- Fix shared behavior in the B44 package that owns it; do not fork or paste a local copy into a consumer repository.
- Use compatibility-bounded floating versions for internal B44 packages in every consumer, including production: pre-1.0 packages use `0.<minor>.*`, while stable packages use `<major>.*`. Package owners bump the excluded boundary for breaking changes, and consumers cross that boundary manually. Never use an unbounded `*`. Enforcement-expanding Standards changes bump the minor version and never enter an existing patch float.
- Treat roughly 350 physical lines as a review warning for production source files. New production files should normally stay at or below 500 lines; files above 650 lines require a clear cohesion-based reason.
- Existing oversized files must not grow unless the same change performs a real extraction and leaves the file smaller. Coordinators coordinate; do not evade the limit with cosmetic partial classes, one-method services, generic utility dumping grounds, or needless factories.
- Before automated analyzer fixes, baseline measurement, scripted bulk text rewrites, or consuming a freshly published package, read `.b44/B44.Tooling.md`.
<!-- B44 ORGANIZATION GUIDANCE: END -->

BookshelfReader provides .NET 8 dependency-injection helpers and optional API endpoint mappings for a bookshelf image parsing pipeline: upload validation, pluggable vision book reading (Claude, OpenAI, or Gemini), genre classification, and Open Library lookup with in-pipeline metadata enrichment.

## Commands

Restore dependencies:

```bash
dotnet restore BookshelfReader.sln
```

Build the solution:

```bash
dotnet build BookshelfReader.sln
```

Run tests:

```bash
dotnet test BookshelfReader.sln
```

Run the reference host for local testing:

```bash
dotnet run --project BookshelfReader.Host
```

Pack the NuGet payloads:

```bash
dotnet pack BookshelfReader.sln -c Release
```

## Architecture

- `BookshelfReader/` is the single packable project. It contains shared domain contracts and models (`Core/`), concrete pipeline implementations and external integrations such as image preprocessing, the vision book readers (Claude/OpenAI/Gemini, all behind `IVisionBookReader` in `Infrastructure/VisionLlm/`), genre classification, and Open Library lookup (`Infrastructure/`), service-registration helpers (`Extensions/`), and endpoint-mapping helpers and API surface (`Api/`). `dotnet pack` emits a single `BookshelfReader` NuGet package from this project.
- `BookshelfReader.Host/` is a runnable reference host for local testing only; it is not the package consumers should embed.
- `BookshelfReader.Tests/` covers DI wiring, option validation, pipeline components, and API behavior.

## Key Conventions

- Keep the consumer-facing NuGet surface in `BookshelfReader/`.
- Do not move product logic into `BookshelfReader.Host/`; it is only a reference host.
- Keep configuration bindable from `appsettings.json` and environment variables. Do not commit API keys or production secrets.
- Upload validation must stay strict: configured size limits, allowed JPEG/PNG content types, and signature checks protect the parse endpoint.
- API changes should preserve the documented `/api/books/lookup` and `/api/bookshelf/parse` contracts unless a breaking change is intentional and documented.
- The vision provider is pluggable. New providers implement `IVisionBookReader` in `Infrastructure/VisionLlm/`, add a `VisionProvider` enum value plus a `<Provider>VisionOptions` class (with API-key env-var fallback), and are wired in `BookshelfReaderServiceCollectionExtensions` via the `AddVisionBookReader` switch (bind/validate only the selected provider's options). Keep all provider calls behind `IVisionBookReader` so tests use fake `HttpMessageHandler`s and run offline. Claude is the default; do not change its behavior.
- Update `README.md` and `docs/IntegrationGuide.md` when changing package usage, endpoint shape, configuration keys, or deployment guidance.

## Configuration Notes

Important configuration sections include `Authentication:ApiKey`, `RateLimiting:Parse`, `Uploads`, `OpenLibrary`, `Enrichment`, and the vision sections. `Vision:Provider` selects the vision backend (`Claude` (default), `OpenAI`, or `Gemini`); only the selected provider's section is bound and validated. Each provider reads its key from its own section with an environment-variable fallback: `ClaudeVision:ApiKey`/`ANTHROPIC_API_KEY`, `OpenAIVision:ApiKey`/`OPENAI_API_KEY`, `GeminiVision:ApiKey`/`GEMINI_API_KEY`. The host fails fast at startup if the selected provider has no key.

Read `docs/IntegrationGuide.md` before making integration, deployment, or host-embedding changes.
