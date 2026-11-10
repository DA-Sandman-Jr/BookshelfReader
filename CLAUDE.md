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
- `B44.Standards` fails the build on drift it can decide mechanically: an engine assembly or source generator reaching an engine-free project, a banned-symbol boundary whose analyzer is missing (which leaves the ban list inert), a `*.Tests` project that would discover no tests, a production reference to a test project, an unbounded `*` float on an internal B44 package, and — where the repository opts in — committed build debris, analyzer suppressions past its budget, and warnings that no longer fail the build. Each check names the property that turns it off; raise a budget or add an exemption in `Directory.Build.props` in the same change that needs it, so the decision is visible in review rather than silent.
- Generated guidance is verified, not trusted. Build with `-p:B44AgentSyncVerifyOnly=true` in CI so a stale `AGENTS.md`, managed `CLAUDE.md` block, or `.b44/B44.Tooling.md` fails the build instead of being silently rewritten by whoever builds next. Hand-authored prose is a different thing and no build can check it: repository-local guidance that names types, counts, or responsibilities goes stale silently and actively misleads the next change, because guidance is read as instructions. Re-read the prose nearest the code you just changed.
- Use one working branch for a coherent body of related work. Commit and checkpoint locally as often as useful, but open one pull request only when the branch is a meaningful, independently landable integration unit. A finished subtask, checkpoint, agent turn, desire to run hosted CI, or desire for branch cleanup is not by itself a reason for another PR; keep tightly related implementation slices on the same branch when they are intended to land as one result.
- Hosted GitHub Actions validation is a deliberate checkpoint, never the development feedback loop. Validate locally with the repository's prescribed build, test, formatting, architecture, and engine or runtime checks throughout implementation, and report that evidence in the handoff or PR. In private repositories, routine commits, pushes, pull requests and merges do not launch hosted build, test, format, engine, or smoke validation: it is requested manually through `workflow_dispatch` after a meaningful feature, an Epic or cross-component integration change, or before a release, for an independent clean-runner proof; do not dispatch it unless asked. Public repositories, where standard GitHub-hosted runners are free, keep their automatic push and pull-request validation and any required status checks. Tag-triggered release and publication workflows stay automatic, and reusable `workflow_call` workflows stay callable.
- A separate PR before the larger body is complete remains appropriate when the result is independently useful and should land first, another repository genuinely depends on it, a package or release boundary requires it, hosted-only infrastructure must be exercised, a deliberate approval, clean-review, provenance, security, or human-decision process requires a distinct candidate, or separation materially lowers integration or rollback risk. Do not collapse genuine approval boundaries merely to save Actions usage.
- When approved work on a non-default GitHub branch is meant to land, merge it through its pull request rather than direct-merging the branch into its base. The PR remains the merge and branch-cleanup mechanism, and any explicit human-approval requirement still controls whether merge is allowed. After landing, verify both the merged PR and removal of the head branch when automatic head-branch deletion is configured; if repository rules or settings prevent deletion, report the residual branch explicitly. Never merge rejected, abandoned, or unapproved work merely to trigger cleanup.
- An architectural rule that can be stated as "this layer must not call these members" is cheap to enforce: put the members in a `BannedSymbols.<Rule>.txt` and register it with `B44BannedSymbols` on the projects the rule governs. Prefer that over leaving the rule to review forever. Rules that cannot be written as an exact list stay in the owning repository's own architecture tests.
- Extraction is judged on the capability, not on a headcount of repositories. A single real consumer is enough to extract a bounded reusable capability when it solves a recognizable reusable problem rather than a one-project quirk, its seam is small and coherent, its API stays natural and domain-facing without caller-specific assumptions, independent evidence says the reuse is real, and nothing speculative has to be built around it. That evidence can be another project, a genre or domain pattern, existing B44 work, donor or reuse findings already translated into neutral requirements, or established practice — a second consumer is one form of it, not a precondition. Keep behavior local instead when the reusable seam is unclear, when it is still strongly shaped by one project's rules, vocabulary, presentation, or implementation, or when extracting it would require machinery no caller needs yet.
- Recognizing a capability and choosing its home are separate decisions. Shared behavior belongs to the package that naturally owns it; nothing lands in `B44.Common` by default, and no package becomes a general utility dump. A primitive that turns up independently in a second repository does not by itself extract anything, but it is a strong ownership-review trigger and a reason to reconsider a shared home against a project-specific one: record both call sites in the relevant work item and settle ownership there. Nothing automates this: whether two near-identical functions are the same concept, or the same formula serving different intents, is a design judgement.
- Generalized infrastructure raises the bar rather than inheriting the bounded one. Cross-capability foundations, generalized orchestration, registries and schedulers, transaction or authority frameworks, plugin and policy architectures, portfolio-wide Standards rules, and abstractions that mostly serve hypothetical future consumers need concrete pressure from multiple independent real consumers — normally at least two — before they exist at all. The goal is a broad repertoire of useful bounded capabilities, not a universal game engine.
- Before automated analyzer fixes, baseline measurement, scripted bulk text rewrites, or consuming a freshly published package, read `.b44/B44.Tooling.md`.
- Godot writes a `.uid` file beside a script and uses it as that script's stable identifier. Commit every one Godot generates and never add `*.uid` to `.gitignore`: without a committed sidecar, references break as soon as the repository is cloned onto another machine, including a CI runner doing a fresh checkout. A sidecar Godot has not written yet is not a defect and not tracked debt — nothing requires one, no build or CI check reports a missing one, and a UID is never hand-written to satisfy a check, because a fabricated value looks authoritative and resolves to nothing. Godot generates sidecars for C# scripts under the project directory, including engine-free `Core` and test projects it never loads; those are committed too. What is checked is the sidecar that outlives its file: a tracked `.uid` or `.import` whose principal file is no longer tracked is orphaned debris and fails repository hygiene.
- Use ordinary modern .NET naming and structure; B44 has no house dialect and writes no analyzer to defend one. What every active repository already does: a file-scoped `namespace` that mirrors the file's folder path beneath the project root, a root namespace equal to the assembly name, `I`-prefixed interfaces, an `Async` suffix on production methods returning `Task` or `ValueTask`, and a file named for the type that owns it. These stay prose because their exceptions are legitimate rather than rare: a polyfill must declare `System.Runtime.CompilerServices` wherever it sits, `Main` cannot take a suffix, and a small companion enum or interface belongs beside the type it serves rather than alone in a file of its own. One public type per file is not a rule — `MA0048` is off deliberately — and grouping unrelated public types is a review question. Packaged libraries document their public types with XML comments; applications do not, which is why `CS1591` sits in `B44AllowedNoWarn` portfolio-wide.
- A test project is named `*.Tests` and its classes end in `Tests`. That first half is load-bearing — the Standards test overlay and the test-integrity guard both key on the suffix — and the rest of a suite is the repository's own: test-method phrasing, test-double vocabulary (`Fake`, `Stub`, `Spy`, prefixed or suffixed), shared doubles in a `Fixtures/` folder or beside the test that needs them, and `src/` plus `tests/` folders or flat project directories. Each is consistent within a repository and deliberately not unified across the portfolio; do not rename an existing suite to match another one. Hand-written doubles are the default, and a mocking framework is the exception rather than the starting point.
- Each repository keeps a root `BACKLOG.md` for agreed-but-not-started work and known defects, with defects in their own section so they stay distinct from planned work. It is authored by hand, never generated and never gated by the build — an empty file written to satisfy a check is worse than no file. Cross-repository work has no mandated repository home. Keep its canonical plan with the work, respect repository visibility, and link each consumer's own tasks to it rather than duplicating the plan.
- Isolation is by repository, not by folder. Engine- or framework-coupled adapters live in their own repository and package so engine-free build guards remain literal and release cadences stay independent.
- Keep licensing boundaries explicit. Source governed by terms different from a repository's `LICENSE` belongs behind a separately documented repository/package boundary with its provenance and required notices intact.
<!-- B44 ORGANIZATION GUIDANCE: END -->

BookshelfReader provides .NET 10 dependency-injection helpers and optional API endpoint mappings for a bookshelf image parsing pipeline: upload validation, pluggable vision book reading (Claude, OpenAI, or Gemini), genre classification, and Open Library lookup with in-pipeline metadata enrichment.

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
dotnet pack BookshelfReader/BookshelfReader.csproj -c Release
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
