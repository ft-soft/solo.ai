# solo.ai

.NET 10 modules for a non-streaming document recommendation path. `Solo.Ai.Api`
receives a complete catalog and an accepted generation context over HTTP. `Solo.Ai`
builds ordered model messages, calls Visograph once, validates the result against
that catalog and returns text produced by trusted templates.

`Solo.Ai.Host` is the normal executable. Incoming authentication is temporarily removed.
It uses the unverified `X-Solo-User-Id` header to partition chats, rejects replay and
supplies server-owned history. The current runtime keeps chats in memory, capped at 1024 accepted attempts
per process; persistent history, chat management and UI remain DOC-5748 work.
See [runtime integration](docs/runtime-integration.md) and
[the Solo → Solo AI HTTP contract](docs/solo-agent-api.md).

Both model configuration objects default to disabled. Enabling requires an explicit
Visograph service credential, finite deadlines and byte limits. No live model or
context capacity is inferred from application defaults. Provider context overflow
and role/schema compatibility must be verified before enabling a deployment.

Repositories build independently. This repository owns the `Solo.Ai.Client` NuGet
package, including the HTTP client and generation DTOs used by the endpoint. Solo
consumes a pinned package version through PackageReference. See [client packaging](docs/client-package.md).
Solo AI calls Visograph directly over HTTP with code inside `Solo.Ai/Visograph`;
no Visograph client package is referenced. Versioned synthetic fixtures check
wire compatibility. No project/source reference to another checkout is required.

```powershell
dotnet restore Solo.Ai.slnx
dotnet build Solo.Ai.slnx --no-restore
dotnet run --project src/Solo.Ai.Host --launch-profile Development
dotnet run --project tests/Solo.Ai.Tests --no-build --no-restore
```

Tests use synthetic catalogs, HTTP handlers and an in-memory ASP.NET server,
including independently stored request/reply and future-tool fixtures. They do
not call a configured LLM. The endpoint requires `ISoloAgentRunRuntime`;
without the runtime gate it fails closed.
Configure the host with local `appsettings.Secrets.json` or environment variables;
see [normal startup](docs/startup.md). No stand host, module or per-message fixture is needed.

Optional isolated [GigaAM v3 transcription](src/Solo.Ai.Transcription/README.md)
provides an authenticated audio-to-text endpoint inside the .NET host. Disabled by default.
