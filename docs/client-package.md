# Solo.Ai.Client package

`src/Solo.Ai.Client` is the producer-owned .NET 10 SDK for the Solo AI generation
endpoint. It contains the HTTP client, options, safe exchange errors and the same
wire DTOs used by the server. Server pipelines, authentication, runtime interfaces
and Visograph code are excluded. Its package dependencies are
`Microsoft.Extensions.Http` 10.0.10 and `Solo.Toolbox` 1.2.0.
`SoloAiClientOptions` implements Toolbox's `ICustomConfiguration` and declares
`SoloAgent:Client` with `ConfigurationAttribute`; consumers register it through
`AddConfiguration<SoloAiClientOptions>(configuration)`.

The current package version is `0.1.0-preview.3`; wire contract version remains 1.
Released package versions are immutable. Change the package version for another
release and update Solo's `src/Directory.Packages.props` deliberately.

```powershell
dotnet pack src/Solo.Ai.Client/Solo.Ai.Client.csproj -c Release -o artifacts/packages
```

Solo consumes `<PackageReference Include="Solo.Ai.Client" />` with the pinned
central version. Normal builds require that version in the configured team NuGet
feed. Packing locally does not publish it. Feed publication and credentials follow
the team's existing release process.

For local validation only, restore the Solo project with the packed directory as
an additional package source, then build/test with `--no-restore`. The feed path
is a command-line setting, not an absolute or sibling path in checked-in config:

```powershell
dotnet restore D:/GitHub/solo/src/Demands.Tests/Demands.Tests.csproj `
  -p:RestoreAdditionalProjectSources=D:/GitHub/solo.ai/artifacts/packages
```

For another workspace, substitute its paths. No source/project dependency exists
between repositories. `Solo.Ai` continues to call Visograph directly over HTTP;
it does not consume or publish a Visograph SDK package.

`SoloAiProtocol.ValidateResult` validates the response shape for both the HTTP
client and Solo's incoming endpoint. `ValidateReply` additionally validates
wire version, correlation IDs and candidates against the request catalog.
