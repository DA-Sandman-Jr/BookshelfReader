# BookshelfReader

BookshelfReader turns a bookshelf photo into structured book data. It combines a pluggable vision provider with genre classification and Open Library enrichment, then exposes the pipeline through dependency-injection helpers and ASP.NET Core endpoints.

> **Platform note:** version 4 requires .NET 10 and Windows because it includes `OpenCvSharp4.runtime.win`. The included API host is a local reference application; consumers normally install the [`BookshelfReader` NuGet package](https://www.nuget.org/packages/BookshelfReader) in an existing ASP.NET Core application.

Uploaded images stay in memory. Before an image is sent to the selected vision provider, it is resized and re-encoded, which strips EXIF metadata such as phone GPS coordinates. Supported providers are Anthropic Claude, OpenAI, and Google Gemini.

## Install

```bash
dotnet add package BookshelfReader
```

In `Program.cs`:

```csharp
using BookshelfReader.Api.Extensions;
using BookshelfReader.Api.Endpoints;
using BookshelfReader.Api.RateLimiting;
using BookshelfReader.Extensions;
using BookshelfReader.Extensions.Authentication;

builder.Services.AddBookshelfReader(builder.Configuration);
builder.Services.AddBookshelfReaderApi();

builder.Services
    .AddAuthentication(options =>
    {
        options.DefaultScheme = ApiKeyAuthenticationDefaults.AuthenticationScheme;
        options.DefaultAuthenticateScheme = ApiKeyAuthenticationDefaults.AuthenticationScheme;
        options.DefaultChallengeScheme = ApiKeyAuthenticationDefaults.AuthenticationScheme;
    })
    .AddBookshelfReaderApiKey();

builder.Services.AddAuthorization();
builder.Services.AddBookshelfReaderRateLimiting(builder.Configuration);

app.UseExceptionHandler();
app.UseAuthentication();
app.UseAuthorization();
app.UseRateLimiter();
app.MapBookshelfReaderApi();
```

Set `Vision:Provider` to `Claude`, `OpenAI`, or `Gemini`, then configure only that provider's API key and options. Authentication, upload limits, rate limiting, enrichment, and Open Library behavior are all configurable. The [integration guide](docs/IntegrationGuide.md) documents every setting, route, response, and failure mode.

The reference host requires an API key by default. Configure `Authentication__ApiKey__ValidKeys__0` and the selected vision provider's key through environment variables or a secret store before starting it. The development settings include a disposable local key.

## Upgrading to version 4

Upgrade your host to .NET 10 first; .NET 8 consumers must remain on version 3 until migrated. Routes and successful response shapes are unchanged. Uploads accept exactly one `image` file with no extra form fields, with a total request limit of `Uploads:MaxBytes` plus 64 KiB for multipart headers. Provider failures return a sanitized `502` ProblemDetails response; upstream credentials and messages are not returned to callers. Provider HTTP clients reject redirects.

Gemini now defaults to `gemini-3.6-flash` because `gemini-2.0-flash` was shut down; update any explicit model overrides. Claude remains the default provider. Image dimension validation uses Apache-licensed MetadataExtractor, removing the ImageSharp dependency and its license-file requirement.

Use a dedicated provider key with its own spending limit. Provider pricing and model availability change, so confirm both with the provider before deploying.

## Example response

`POST /api/bookshelf/parse` accepts a multipart image and returns a result shaped like this:

```json
{
  "imageId": "<guid>",
  "books": [
    {
      "title": "A Book Title",
      "author": "An Author",
      "genres": ["Fiction"],
      "confidence": 0.82,
      "metadata": {
        "title": "A Book Title",
        "author": "An Author",
        "publishYear": 1965,
        "isbn": "...",
        "coverUrl": "https://covers.openlibrary.org/b/id/...-M.jpg"
      }
    }
  ],
  "diagnostics": {
    "segmentCount": 1,
    "elapsedMs": 742,
    "notes": []
  }
}
```

## Build and test

```bash
dotnet restore BookshelfReader.sln
dotnet build BookshelfReader.sln --no-restore
dotnet test BookshelfReader.sln --no-build
```

The test suite covers dependency-injection wiring, configuration validation, provider behavior, image handling, and the parsing pipeline. Only the `BookshelfReader` project is packable, so solution-wide packing emits one NuGet package.

## Releasing

A `v*` tag runs the Windows release workflow, builds and tests the solution, and publishes through NuGet Trusted Publishing with a short-lived OIDC credential. The repository does not require a long-lived NuGet API key.

For a local package test:

```bash
dotnet pack BookshelfReader/BookshelfReader.csproj \
  -c Release \
  -p:PackageVersion=4.0.0-local.1 \
  -p:IncludeSymbols=true \
  -p:SymbolPackageFormat=snupkg
```

## License

BookshelfReader's source is licensed under [MIT](LICENSE). Runtime dependency notices are collected in [THIRD-PARTY-NOTICES.md](THIRD-PARTY-NOTICES.md).
