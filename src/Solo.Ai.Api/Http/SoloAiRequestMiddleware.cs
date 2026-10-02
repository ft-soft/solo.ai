using System.Text.Json;
using Microsoft.AspNetCore.Http;
using Microsoft.Data.Sqlite;
using Solo.Ai.Client;
using Solo.Ai.Storage;

namespace Solo.Ai.Api;

// Registered after authorization and before endpoints, including parameter/DI binding.
public sealed class SoloAiRequestMiddleware(RequestDelegate next)
{
    public async Task InvokeAsync(HttpContext context, SoloAiApiOptions options, SqliteChatOptions storage)
    {
        if (!context.Request.Path.StartsWithSegments("/" + SoloAiProtocol.Endpoint))
        {
            await next(context);
            return;
        }
        var ids = context.Request.Headers[SoloAiProtocol.RequestIdHeader];
        var validId = ids.Count == 1 && Guid.TryParseExact(ids[0], "D", out var id) && id != Guid.Empty;
        context.Response.Headers[SoloAiProtocol.RequestIdHeader] = validId ? ids[0] : Guid.NewGuid().ToString("D");
        context.Response.Headers[SoloAiProtocol.VersionHeader] = "2";
        context.Response.Headers.CacheControl = "no-store";
        try
        {
            var versions = context.Request.Headers[SoloAiProtocol.VersionHeader];
            if (versions.Count != 1 || versions[0] != "2") throw new SoloAiExchangeException("unsupported_contract");
            if (!validId) throw new SoloAiExchangeException("validation_failed");
            if (context.Request.ContentLength > options.MaxRequestBytes) throw new SoloAiExchangeException("limit_exceeded");
            if (HttpMethods.IsGet(context.Request.Method) || HttpMethods.IsDelete(context.Request.Method) ||
                context.Request.Path.Value!.TrimEnd('/').EndsWith("/cancel", StringComparison.OrdinalIgnoreCase))
            {
                var body = await SoloAiProtocol.ReadBoundedAsync(context.Request.Body, options.MaxRequestBytes, context.RequestAborted);
                if (body.Length != 0) throw new SoloAiExchangeException("validation_failed");
            }
            if (!storage.Enabled) throw new SoloAiExchangeException("unavailable");
            await next(context);
        }
        catch (SoloAiExchangeException error) { await SoloAiHttp.Reject(error.Code).ExecuteAsync(context); }
        catch (JsonException) { await SoloAiHttp.Reject("validation_failed").ExecuteAsync(context); }
        catch (BadHttpRequestException error)
        {
            await SoloAiHttp.Reject(error.StatusCode == 413 ? "limit_exceeded" : "validation_failed").ExecuteAsync(context);
        }
        catch (Exception error) when (error is SqliteException or IOException or UnauthorizedAccessException)
        {
            await SoloAiHttp.Reject("unavailable").ExecuteAsync(context);
        }
    }
}
