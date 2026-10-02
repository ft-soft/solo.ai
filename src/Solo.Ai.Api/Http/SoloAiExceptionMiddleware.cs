using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;

namespace Solo.Ai.Api;

public sealed class SoloAiExceptionMiddleware(RequestDelegate next, ILogger<SoloAiExceptionMiddleware> logger)
{
    public async Task InvokeAsync(HttpContext context)
    {
        try { await next(context); }
        catch (OperationCanceledException) when (context.RequestAborted.IsCancellationRequested) { context.Abort(); }
        catch (Exception)
        {
            logger.LogError("HTTP request failed with unavailable.");
            if (context.Response.HasStarted) context.Abort();
            else
            {
                context.Response.Clear();
                // An unexpected exception may follow a commit: do not claim a rejected wire command.
                await Results.Json(new { status = "unavailable" }, statusCode: 503).ExecuteAsync(context);
            }
        }
    }
}
