# Solo.Ai.Client

HTTP client and request/reply DTOs for `POST /api/v1/solo-agent/generations`.
The package targets .NET 10 and uses the named `solo-ai` HttpClient supplied by
the caller's `IHttpClientFactory`. Configure its agreed authentication handler,
disable redirects and HTTP payload logging, and do not attach automatic retries.

`SoloAiClientOptions` implements the Solo.Toolbox `ICustomConfiguration` contract
and declares the `SoloAgent:Client` section. Register it with
`services.AddConfiguration<SoloAiClientOptions>(configuration)` using
`Solo.Toolbox.Extensions` (or Solo's plugin configuration extension).
The same options instance is available directly, through `IOptions<SoloAiClientOptions>`
and through the custom configuration contract.

Configure that section with Enabled, deployment-root BaseUri, finite
RequestTimeout/TotalDeadline and full request/response byte limits. It is disabled
by default. `SoloAiClient.GenerateAsync` sends one complete `PreparedGenerationInput`
and checks version, correlation IDs and candidate compatibility.

The caller authenticates the sender and reads a fresh permission-scoped catalog.
The Solo AI runtime checks chat/run ownership, deduplicates the sender's message
and supplies permitted history before calling the model. `SoloAiProtocol.ValidateResult`
provides the shared response-shape check for incoming endpoints.
IDs and submitted data do not grant authorization. Render result Text literally.
This package supplies no chat runtime, storage or Visograph integration.
