# BookshelfReader Integration Guide

This guide explains how to consume the BookshelfReader service from another web application while keeping each codebase, deployment, and secret store independent. It consolidates the runtime surface, configuration knobs, and collaboration practices your host team will need when proxying or embedding the service.

## 1. Runtime surface

Version 4 requires a patched .NET 10 runtime and Windows. Upgrade version 3 hosts from .NET 8 before installing this version. The package includes the native OpenCV Windows runtime; JPEG/PNG dimensions are checked with MetadataExtractor before pixel decoding.

### Supported commands

| Scenario | Command |
| --- | --- |
| Restore dependencies | `dotnet restore`
| Local development server | `dotnet run --project BookshelfReader.Host`
| Test suite | `dotnet test`
| Production build artifact | `dotnet publish BookshelfReader.Host -c Release`

Configure listening addresses explicitly with `ASPNETCORE_URLS` (for example, `https://localhost:5001;http://localhost:5000`) and configure a development certificate for HTTPS. `dotnet run --project BookshelfReader.Host` otherwise uses ASP.NET Core's defaults; this repository has no launch profile. Swagger UI is exposed only in Development.

### HTTP endpoints

All routes are rooted at `/api` and described by the generated OpenAPI document at `/swagger/v1/swagger.json`.

| Method | Route | Description | Auth | Success |
| --- | --- | --- | --- | --- |
| `GET` | `/api/books/lookup?name=<title or author>` | Searches Open Library for matching metadata. | None | `200 OK` with `IEnumerable<BookMetadata>` JSON payload.
| `POST` | `/api/bookshelf/parse` | Accepts `multipart/form-data` with an `image` file containing a bookshelf photo and returns extracted spine candidates. | Optional API key (configurable) | `200 OK` with `ParseResult` JSON payload.

`ParseResult` provides:

```json
{
  "imageId": "<guid>",
  "books": [
    {
      "boundingBox": { "x": 0, "y": 0, "width": 0, "height": 0 },
      "title": "...",
      "author": "...",
      "genres": ["..."],
      "confidence": 0.82,
      "rawText": "...",
      "notes": ["..."],
      "metadata": {
        "title": "...",
        "author": "...",
        "publishYear": 1965,
        "isbn": "...",
        "coverUrl": "https://covers.openlibrary.org/b/id/...-M.jpg",
        "subjects": ["..."]
      }
    }
  ],
  "diagnostics": {
    "segmentCount": 3,
    "elapsedMs": 742,
    "notes": ["The vision model response was truncated at the configured token limit; some books may be missing."]
  }
}
```

`diagnostics.segmentCount` is the number of books the vision model returned (the field name is preserved from earlier versions for contract compatibility).

When enrichment is enabled (the default), the service looks up each parsed title against Open Library inside the parse request and attaches the best catalog match as `metadata` - canonical title, author, first publish year, ISBN, cover image URL, and subjects (which also feed the `genres` list). `metadata` is `null` when no confident match was found; the candidate's `notes` explain why (no results, low match score, or lookup failure). Callers that prefer to orchestrate their own lookups can set `Enrichment:Enabled` to `false` and use `/api/books/lookup` per title instead. Enrichment adds outbound Open Library calls to parse latency, so expect parse requests to take a few seconds longer for shelves with many readable spines.

Requests that violate validation (missing file, extra files or form fields, unsupported MIME type, oversized image, undecodable image, etc.) return `400` responses with ProblemDetails bodies. Server-level request limits may reject oversized bodies with `413` before the handler runs. When `RequireApiKey` is enabled the parse endpoint also returns `401` for missing or invalid keys, and when parse rate limiting is enabled it returns `429 Too Many Requests` plus a `Retry-After` header once callers exceed the configured limit. Provider failures, including upstream authentication failures, throttling, and timeouts, return a sanitized `502` ProblemDetails response. Upstream error details are logged server-side and are not exposed to callers.

### How book reading works

`/api/bookshelf/parse` reads the uploaded image entirely in memory, normalizes it (corrects EXIF orientation, downscales so the longest edge is at most the selected provider's `MaxImageDimension`, and re-encodes as JPEG - which also strips all metadata, including phone GPS), and sends it to the configured vision provider with a request for each book's title, author, and confidence. The image is never written to disk. Results are mapped to `BookCandidate`s (with `boundingBox` always empty - the vision models do not return spine coordinates) and enriched against Open Library as described above.

The vision provider is pluggable via `Vision:Provider` (`Claude` (default), `OpenAI`, or `Gemini`). All three sit behind the same `IVisionBookReader` abstraction and produce the same `VisionReadResult`; only the selected provider's option section is bound and validated, so you only configure a key for the provider you use.

| Provider | API | Default model | Image transport | Structured output |
| --- | --- | --- | --- | --- |
| `Claude` (default) | [Anthropic Messages API](https://docs.anthropic.com/) (`POST /v1/messages`) | `claude-haiku-4-5` | base64 image block | `output_config` JSON schema. Anthropic does not train on API inputs. |
| `OpenAI` | Chat Completions (`POST /v1/chat/completions`) | `gpt-4o-mini` | base64 `image_url` data URL | `response_format: json_object` |
| `Gemini` | Generative Language API (`POST v1beta/models/{model}:generateContent`) | `gemini-3.6-flash` | base64 `inline_data` | `generationConfig.responseMimeType: application/json` |

Capability notes: the default models for all three providers accept images well above BookshelfReader's `MaxImageDimension` (1568 px longest edge), so the configured downscale is the effective ceiling. OpenAI and Gemini use native JSON-mode/structured-output settings plus an explicit JSON instruction in the prompt (Claude additionally constrains output with a JSON schema); the readers tolerate prose or code-fence wrapping around the JSON object. Error handling (401/403 key rejection, 429 rate limiting, 5xx server errors, timeouts) is normalized across providers to the same `VisionBookReaderException` messages.

## 2. Configuration & secrets

Configuration lives in `BookshelfReader.Host/appsettings.json` with environment-specific overrides in `appsettings.<Environment>.json`. Every option is also bindable via environment variables, which is the preferred mechanism for production secrets.

The reference host enables `Authentication:ApiKey:RequireApiKey` by default and fails startup until a valid key is configured. The embedding library keeps API-key enforcement opt-in; consumers should enable it for public parse endpoints. `appsettings.Development.json` supplies `local-dev-key` for local development only. Provider clients do not follow HTTP redirects, so custom provider gateways must serve requests directly at the configured HTTPS base URL. Responses are capped at 1 MiB for vision providers and 4 MiB for Open Library to bound buffering.

Gemini's default is now `gemini-3.6-flash`; replace explicit `gemini-2.0-flash` overrides, because Google has [shut down that model](https://ai.google.dev/gemini-api/docs/deprecations). Claude remains the default provider.

| Setting | Description | Environment variable example |
| --- | --- | --- |
| `Authentication:ApiKey:HeaderName` | HTTP header that carries the credential. | `Authentication__ApiKey__HeaderName=X-API-Key`
| `Authentication:ApiKey:RequireApiKey` | Enables API-key enforcement on `/api/bookshelf/parse`. | `Authentication__ApiKey__RequireApiKey=true`
| `Authentication:ApiKey:ValidKeys` | List of accepted API keys. Omit in source control. | `Authentication__ApiKey__ValidKeys__0=<value>`
| `RateLimiting:Parse:Enabled` | Enables per-IP rate limiting for `POST /api/bookshelf/parse` (default `false`). | `RateLimiting__Parse__Enabled=true`
| `RateLimiting:Parse:PermitLimit` | Allowed parse requests per window before `429` is returned (default `10`). | `RateLimiting__Parse__PermitLimit=10`
| `RateLimiting:Parse:WindowSeconds` | Length of the parse rate-limit window in seconds (default `60`). | `RateLimiting__Parse__WindowSeconds=60`
| `OpenLibrary:BaseUrl` | Override Open Library endpoint (for mocks or future changes). | `OpenLibrary__BaseUrl=https://...`
| `OpenLibrary:UserAgent` | Identifying `User-Agent` sent to Open Library, per their API guidelines. | `OpenLibrary__UserAgent=MyApp/1.0 (contact@example.com)`
| `Enrichment:Enabled` | Attach Open Library metadata to parse results in-pipeline (default `true`). | `Enrichment__Enabled=true`
| `Enrichment:MaxConcurrentLookups` | Concurrent Open Library lookups per parse request (1-16). | `Enrichment__MaxConcurrentLookups=4`
| `Enrichment:MinMatchScore` | Minimum fuzzy match score (0-100) before metadata is attached. | `Enrichment__MinMatchScore=55`
| `Uploads:MaxBytes` | Maximum image size in bytes (1 byte to 20 MiB); entire multipart requests are capped at this value plus 64 KiB, including chunked uploads. | `Uploads__MaxBytes=10485760`
| `Uploads:MaxImagePixels` | Maximum width times height checked before decoding (default 25,000,000; maximum 50,000,000). | `Uploads__MaxImagePixels=25000000`
| `Uploads:AllowedContentTypes` | Canonical MIME types accepted from the form upload. | `Uploads__AllowedContentTypes__0=image/jpeg`
| `Vision:Provider` | Vision backend: `Claude` (default), `OpenAI`, or `Gemini`. Only the selected provider's section is bound/validated. | `Vision__Provider=OpenAI`
| `ClaudeVision:ApiKey` | Anthropic API key (used when provider is `Claude`). Falls back to `ANTHROPIC_API_KEY`. Required for this provider - the host fails fast at startup without one. | `ANTHROPIC_API_KEY=sk-ant-...`
| `ClaudeVision:BaseUrl` | Anthropic API base URL (must be HTTPS, default `https://api.anthropic.com/`). | `ClaudeVision__BaseUrl=https://api.anthropic.com/`
| `ClaudeVision:Model` | Claude model used to read book spines (default `claude-haiku-4-5`). | `ClaudeVision__Model=claude-haiku-4-5`
| `ClaudeVision:MaxTokens` | Max response tokens per vision request (default `2048`). | `ClaudeVision__MaxTokens=2048`
| `ClaudeVision:TimeoutSeconds` | HTTP timeout for vision requests in seconds (default `60`). | `ClaudeVision__TimeoutSeconds=60`
| `ClaudeVision:MaxImageDimension` | Images are downscaled so their longest edge does not exceed this many pixels before being sent (default `1568`). | `ClaudeVision__MaxImageDimension=1568`
| `OpenAIVision:ApiKey` | OpenAI API key (used when provider is `OpenAI`). Falls back to `OPENAI_API_KEY`. Required for this provider. | `OPENAI_API_KEY=sk-...`
| `OpenAIVision:BaseUrl` | OpenAI API base URL (must be HTTPS, default `https://api.openai.com/`). | `OpenAIVision__BaseUrl=https://api.openai.com/`
| `OpenAIVision:Model` | Vision-capable OpenAI chat model (default `gpt-4o-mini`). | `OpenAIVision__Model=gpt-4o-mini`
| `OpenAIVision:MaxTokens` / `TimeoutSeconds` / `MaxImageDimension` | Same meanings/defaults (`2048`/`60`/`1568`) as the Claude options. | `OpenAIVision__MaxTokens=2048`
| `GeminiVision:ApiKey` | Gemini API key (used when provider is `Gemini`). Falls back to `GEMINI_API_KEY`. Required for this provider. | `GEMINI_API_KEY=...`
| `GeminiVision:BaseUrl` | Gemini API base URL (must be HTTPS, default `https://generativelanguage.googleapis.com/`). | `GeminiVision__BaseUrl=https://generativelanguage.googleapis.com/`
| `GeminiVision:Model` | Multimodal Gemini model (default `gemini-3.6-flash`). | `GeminiVision__Model=gemini-3.6-flash`
| `GeminiVision:MaxTokens` / `TimeoutSeconds` / `MaxImageDimension` | Same meanings/defaults (`2048`/`60`/`1568`) as the Claude options. | `GeminiVision__MaxTokens=2048`

Production deployments should inject secrets through your platform's secret store (Azure Key Vault, AWS Secrets Manager, GitHub Actions secrets to environment variables). Only ship sanitized sample values in source control. The development profile (`appsettings.Development.json`) demonstrates setting a disposable key (`local-dev-key`).

Create a dedicated console workspace and API key for whichever provider you select, with its own spend limit, so usage and billing stay isolated. Model availability and pricing change independently of this project; verify both with the provider before deploying.

### Sample `.env` (development)

```
ASPNETCORE_ENVIRONMENT=Development
Authentication__ApiKey__RequireApiKey=true
Authentication__ApiKey__ValidKeys__0=local-dev-key
RateLimiting__Parse__Enabled=true
# Default provider is Claude:
ANTHROPIC_API_KEY=sk-ant-...
# Or pick another provider and supply its key instead:
# Vision__Provider=OpenAI
# OPENAI_API_KEY=sk-...
# Vision__Provider=Gemini
# GEMINI_API_KEY=...
```

Load it with `dotnet user-secrets`, `direnv`, or your preferred tooling.

## 3. Integration contract

1. **Authentication** - When `RequireApiKey` is `true`, callers must include `X-API-Key: <value>` (or your configured header) on every `POST /api/bookshelf/parse` request. The lookup endpoint is anonymous by default.
2. **Payloads** - The upload endpoint requires `multipart/form-data` with exactly one `image` file field and no additional form fields. JPEG and PNG inputs are accepted out of the box. Image bytes and the total request size are bounded in memory, and signatures must match the MIME type.
3. **Response shape** - Use the `ParseResult` schema above. Bounding boxes remain empty because the vision models return titles and authors without spine coordinates.
4. **Errors** - Validation problems use the standard ASP.NET Core RFC 7807 schema with `errors` dictionary. Treat `401` as missing or invalid credentials, `429` plus `Retry-After` as a rate-limit backoff signal, other 4xx codes as actionable client fixes, and 5xx codes as transient failures worth retrying.
5. **OpenAPI** - Consume `/swagger/v1/swagger.json` to generate typed clients (NSwag, AutoRest, openapi-typescript). Regenerate clients whenever the service bumps its version.

### UI embedding

BookshelfReader does not ship standalone UI assets. The recommended integration pattern is either:

* **Reverse proxy** - Surface the API under your main site (e.g., `/api/bookshelf/*`) via a reverse proxy or API gateway.
* **Iframe / widget** - Host your own UI and call the service from the browser via HTTPS. If you need cross-origin access, configure CORS in your host proxy; the service itself is locked down with strict security headers and same-origin policies.

## 4. Deployment boundaries

* Package and deploy the service independently (App Service, container app, Kubernetes workload, etc.).
* BookshelfReader is stateless and uses outbound HTTPS to the selected vision provider and Open Library. Uploaded images are processed in memory and never written to disk. Apply request, concurrency, and rate limits at your ingress to bound resource use across clients.
* Expose the service via a dedicated subdomain (e.g., `https://bookshelf-reader.yourdomain.com`) or a reverse-proxy path segment. Ensure TLS termination happens at the edge the same way as your primary site.
* If you front the service with an ingress controller or API gateway, forward the `X-API-Key` header without modification and enforce rate limits upstream.

## 5. Local co-development workflow

1. Clone your main site repo and this repo side by side.
2. Start BookshelfReader: `dotnet run --project BookshelfReader.Host` (or rely on the defaults).
3. Start your host site on a different port (e.g., `https://localhost:7000`).
4. Configure the host to call BookshelfReader by setting an environment variable such as `BOOKSHELF_READER_BASE_URL=https://localhost:5001`.
5. For browser-based integration, either:
   * Proxy `/api/bookshelf/*` through your host dev server to avoid CORS, or
   * Enable CORS on your host proxy to allow calls from `https://localhost:7000` to `https://localhost:5001`. BookshelfReader itself does not register CORS; manage it at the proxy.
6. Use sample curl commands (`curl -H "X-API-Key: local-dev-key" -F "image=@sample.jpg" https://localhost:5001/api/bookshelf/parse`) to verify connectivity.

Troubleshooting tips:

* **401 Unauthorized** - Confirm `RequireApiKey` is enabled only when you supply the header. Rotate keys via configuration without redeploying.
* **400 Bad Request** - Ensure the file field name is exactly `image` and the MIME type is allowed.
* **Large file rejection** - Increase `Uploads:MaxBytes` temporarily during debugging.
* **Host fails at startup** - the selected provider's API key is required (`ClaudeVision:ApiKey`/`ANTHROPIC_API_KEY`, `OpenAIVision:ApiKey`/`OPENAI_API_KEY`, or `GeminiVision:ApiKey`/`GEMINI_API_KEY`); the host validates options on start and will not boot without a key for the configured `Vision:Provider`. An unrecognized `Vision:Provider` value also fails startup with a message listing the valid choices.
* **Vision request errors** - The parse endpoint returns sanitized `502` ProblemDetails responses for provider failures. Inspect server logs to distinguish a rejected provider key (`401`/`403`), provider throttling/overload (`429`/`529`), and network or timeout failures. The parse endpoint's own `401` and `429` still indicate caller authentication and local rate limiting.

## 6. Validation & CI expectations

The repository targets .NET 10 LTS. Keep the SDK and deployed runtime on current servicing patches; .NET 8 [reaches end of support on November 10, 2026](https://dotnet.microsoft.com/en-us/platform/support/policy). A healthy pipeline should execute:

```
dotnet restore
dotnet build --configuration Release --no-restore -p:B44AgentSyncVerifyOnly=true
dotnet test --configuration Release --no-build
```

Publish on Windows, or use a Windows container with the native runtime requirements installed. Linux containers are not supported by this package. Tag releases using semantic versioning (e.g., `v4.0.0`) so the host site can pin to a known build. NuGet audits direct and transitive packages during restore and fails on known vulnerabilities; Dependabot checks NuGet packages and pinned GitHub Actions weekly.

## 7. Release coordination

* Document API or response-shape changes in `CHANGELOG.md` or the README release notes section.
* Publish GitHub releases summarizing changes. Include upgrade notes for contract-affecting updates.
* For breaking changes, bump the major version and announce via your team's communication channel (Slack, Teams, email).
* When introducing new optional fields, guard the behavior with feature flags so the host site can opt in safely.

Following this guide will let you operate BookshelfReader as an autonomous service while providing a clear contract for your main site to call into whenever you are ready to embed bookshelf parsing features.
