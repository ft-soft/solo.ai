using System.Text;
using Microsoft.AspNetCore.Http;
using Solo.Ai.Client;

namespace Solo.Ai.Api;

internal static class SoloAiHttp
{
    public static Guid ParseId(string? value) => Guid.TryParseExact(value, "D", out var id) && id != Guid.Empty
        ? id : throw new SoloAiExchangeException("validation_failed");

    public static async Task<T> ReadRequestAsync<T>(HttpContext context, SoloAiApiOptions options) where T : class
    {
        if (!context.Request.HasJsonContentType()) throw new SoloAiExchangeException("validation_failed");
        var bytes = await SoloAiProtocol.ReadBoundedAsync(context.Request.Body, options.MaxRequestBytes, context.RequestAborted);
        var request = SoloAiProtocol.Deserialize<T>(bytes);
        SoloAiValidation.ValidateRequest(request);
        return request;
    }

    public static IResult Reply<T>(T data, SoloAiApiOptions options, int status = 200) where T : class =>
        Results.Content(Encoding.UTF8.GetString(SoloAiProtocol.SerializeBounded(new SoloAiResponse<T>(2, data, null), options.MaxResponseBytes)),
            "application/json; charset=utf-8", statusCode: status);

    public static IResult Reject(string code)
    {
        var status = code switch
        {
            "validation_failed" or "unsupported_contract" => 400,
            "authentication_required" => 401,
            "forbidden_actor" => 403,
            "not_found" => 404,
            "chat_busy" or "idempotency_conflict" or "retry_not_allowed" => 409,
            "limit_exceeded" => 413,
            "capacity_exceeded" => 429,
            _ => 503,
        };
        return Results.Json(new SoloAiResponse<object>(2, null, status == 503 ? "unavailable" : code),
            SoloAiProtocol.JsonOptions, statusCode: status);
    }
}
