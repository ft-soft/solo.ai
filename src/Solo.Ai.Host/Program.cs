using Solo.Ai.Api;

var builder = WebApplication.CreateBuilder(args);
builder.Configuration.AddJsonFile("appsettings.Secrets.json", optional: true).AddEnvironmentVariables();
builder.Services.AddSingleton<ISoloAgentRunRuntime, SoloAgentRunRuntime>();
builder.Services.AddSoloAgentModelEndpoint(builder.Configuration);
var app = builder.Build();
app.MapSoloAgentModelEndpoint();
app.MapGet("/health", () => Results.Ok(new { status = "ready" }));
app.Run();
