using Microsoft.AspNetCore.Authentication;
using Solo.Ai.Api;
using Solo.Ai.Transcription;
using Solo.Ai.Host;

var builder = WebApplication.CreateBuilder(args);
builder.Configuration.AddJsonFile("appsettings.Secrets.json", optional: true).AddEnvironmentVariables();
builder.Services.AddAuthentication("SoloBackend")
    .AddScheme<AuthenticationSchemeOptions, SoloBackendAuthenticationHandler>("SoloBackend", _ => { });
builder.Services.AddAuthorization(options => options.AddPolicy("SoloBackend", policy => policy
    .AddAuthenticationSchemes("SoloBackend").RequireAuthenticatedUser().RequireRole("solo-backend")));
builder.Services.AddSingleton<ISoloAgentRunRuntime, SoloAgentRunRuntime>();
builder.Services.AddSoloAgentModelEndpoint(builder.Configuration);
builder.Services.AddTranscription(builder.Configuration);
var app = builder.Build();
app.UseAuthentication();
app.UseAuthorization();
app.MapSoloAgentModelEndpoint();
app.MapTranscription("SoloBackend");
app.MapGet("/health", () => Results.Ok(new { status = "ready" }));
app.Run();
