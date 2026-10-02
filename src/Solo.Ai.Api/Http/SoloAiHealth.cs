using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Solo.Ai.Storage;

namespace Solo.Ai.Api;

public sealed class SoloAiHealth(SoloAgentRunRuntime runtime, SqliteChatDatabase? database)
{
    public IResult CheckReadiness()
    {
        var ready = runtime.IsReady && database is not null;
        if (ready)
        {
            try { database!.CheckHealth(); ready = runtime.IsReady; }
            catch (Exception)
            {
                runtime.SetReady(false);
                ready = false;
            }
        }
        return Results.Json(new { status = ready ? "ready" : "unavailable" }, statusCode: ready ? 200 : 503);
    }

    public static void MapEndpoints(IEndpointRouteBuilder endpoints)
    {
        endpoints.MapGet("/health/live", () => Results.Json(new { status = "alive" }));
        endpoints.MapGet("/health/ready", (SoloAiHealth health) => health.CheckReadiness());
        endpoints.MapGet("/health", (SoloAiHealth health) => health.CheckReadiness());
    }
}
