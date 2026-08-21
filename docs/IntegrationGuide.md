# BookshelfReader Integration Guide

This guide explains how to consume the BookshelfReader service from another web application while keeping each codebase, deployment, and secret store independent. It consolidates the runtime surface, configuration knobs, and collaboration practices your host team will need when proxying or embedding the service.

## 1. Runtime surface

### Supported commands

| Scenario | Command |
| --- | --- |
| Restore dependencies | `dotnet restore`
| Local development server | `dotnet run --project BookshelfReader.Host`
| Test suite | `dotnet test`
| Production build artifact | `dotnet publish BookshelfReader.Host -c Release`

Running `dotnet run --project BookshelfReader.Host` starts Kestrel on the default HTTPS port (`https://localhost:5001`) and HTTP port (`http://localhost:5000`). Swagger UI is automatically exposed in Development.

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

Requests that violate validation (missing file, unsupported MIME type, oversized image, undecodable image, etc.) return `400` responses with RFC 7807 bodies. When `RequireApiKey` is enabled the parse endpoint also returns `401` for missing or invalid keys, and when parse rate limiting is enabled it returns `429 Too Many Requests` plus a `Retry-After` header once callers exceed the configured limit. If the Claude vision API rejects the request (bad upstream API key, rate limiting, or a server-side error) the parse endpoint returns a `502`/`429`/`5xx` ProblemDetails response describing the upstream failure.

### How book reading works

`/api/bookshelf/parse` reads the uploaded image entirely in memory, normalizes it (corrects EXIF orientation, downscales so the longest edge is at most the selected provider's `MaxImageDimension`, and re-encodes as JPEG - which also strips all metadata, including phone GPS), and sends it to the configured vision provider with a request for each book's title, author, and confidence. The image is never written to disk. Results are mapped to `BookCandidate`s (with `boundingBox` always empty - the vision models do not return spine coordinates) and enriched against Open Library as described above.

The vision provider is pluggable via `Vision:Provider` (`Claude` (default), `OpenAI`, or `Gemini`). All three sit behind the same `IVisionBookReader` abstraction and produce the same `VisionReadResult`; only the selected provider's option section is bound and validated, so you only configure a key for the provider you use.

| Provider | API | Default model | Image transport | Structured output |
| --- | --- | --- | --- | --- |
| `Claude` (default) | [Anthropic Messages API](https://docs.anthropic.com/) (`POST /v1/messages`) | `claude-haiku-4-5` | base64 image block | `output_config` JSON schema. Anthropic does not train on API inputs. |
| `OpenAI` | Chat Completions (`POST /v1/chat/completions`) | `gpt-4o-mini` | base64 `image_url` data URL | `response_format: json_object` |
| `Gemini` | Generative Language API (`POST v1beta/models/{model}:generateContent`) | `gemini-2.0-flash` | base64 `inline_data` | `generationConfig.responseMimeType: application/json` |

Capability notes: the default models for all three providers accept images well above BookshelfReader's `MaxImageDimension` (1568 px longest edge), so the configured downscale is the effective ceiling. OpenAI and Gemini use native JSON-mode/structured-output settings plus an explicit JSON instruction in the prompt (Claude additionally constrains output with a JSON schema); the readers tolerate prose or code-fence wrapping around the JSON object. Error handling (401/403 key rejection, 429 rate limiting, 5xx server errors, timeouts) is normalized across providers to the same `VisionBookReaderException` messages.

## 2. Configuration & secrets

Configuration lives in `BookshelfReader.Host/appsettings.json` with environment-specific overrides in `appsettings.<Environment>.json`. Every option is also bindable via environment variables, which is the preferred mechanism for production secrets.

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
| `Uploads:MaxBytes` | Maximum upload size in bytes. | `Uploads__MaxBytes=10485760`
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
| `GeminiVision:Model` | Multimodal Gemini model (default `gemini-2.0-flash`). | `GeminiVision__Model=gemini-2.0-flash`
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
2. **Payloads** - The upload endpoint requires `multipart/form-data` with a single `image` file field. JPEG and PNG inputs are accepted out of the box. The service streams files directly from the request and rejects payloads larger than the configured limit or whose signatures do not match the MIME type.
3. **Response shape** - Use the `ParseResult` schema above. Bounding boxes are relative to the uploaded image, enabling downstream UI overlays.
4. **Errors** - Validation problems use the standard ASP.NET Core RFC 7807 schema with `errors` dictionary. Treat `401` as missing or invalid credentials, `429` plus `Retry-After` as a rate-limit backoff signal, other 4xx codes as actionable client fixes, and 5xx codes as transient failures worth retrying.
5. **OpenAPI** - Consume `/swagger/v1/swagger.json` to generate typed clients (NSwag, AutoRest, openapi-typescript). Regenerate clients whenever the service bumps its version.

### UI embedding

BookshelfReader does not ship standalone UI assets. The recommended integration pattern is either:

* **Reverse proxy** - Surface the API under your main site (e.g., `/api/bookshelf/*`) via a reverse proxy or API gateway.
* **Iframe / widget** - Host your own UI and call the service from the browser via HTTPS. If you need cross-origin access, configure CORS in your host proxy; the service itself is locked down with strict security headers and same-origin policies.

## 4. Deployment boundaries

* Package and deploy the service independently (App Service, container app, Kubernetes workload, etc.).
* Do not share infrastructure resources (storage, queues) with the host site unless absolutely necessary; BookshelfReader is stateless and depends only on outbound HTTPS to the Anthropic API and Open Library. Uploaded images are processed in memory and never written to disk.
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
* **Vision request errors** - `401`/`403` means the configured API key was rejected; `429`/`529` mean the provider is rate limiting or overloaded - all surface to callers as ProblemDetails responses with the upstream message included, regardless of provider.

## 6. Validation & CI expectations

The repository targets .NET 8. A healthy pipeline should execute:

```
dotnet restore
dotnet build --configuration Release
dotnet test --configuration Release --no-build
```

Consider publishing a Docker image (`dotnet publish -c Release -o out && docker build`) or versioned release artifacts. Tag releases using semantic versioning (e.g., `v1.4.0`) so the host site can pin to a known build.

## 7. Release coordination

* Document API or response-shape changes in `CHANGELOG.md` or the README release notes section.
* Publish GitHub releases summarizing changes. Include upgrade notes for contract-affecting updates.
* For breaking changes, bump the major version and announce via your team's communication channel (Slack, Teams, email).
* When introducing new optional fields, guard the behavior with feature flags so the host site can opt in safely.

Following this guide will let you operate BookshelfReader as an autonomous service while providing a clear contract for your main site to call into whenever you are ready to embed bookshelf parsing features.
