using System.Diagnostics;
using System.Text.Json;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Solo.Ai.Client;
using Solo.Ai.Visograph;

namespace Solo.Ai.Api;

public sealed class SoloAgentModelEndpoint(
    ISoloAgentModelPipeline pipeline, IOptions<SoloAgentModelApiOptions> options,
    ILogger<SoloAgentModelEndpoint> logger, ISoloAgentRunAuthorizer? authorizer = null)
{
    public const string Path = "/api/v1/solo-agent/generations";
    public const int ContractVersion = 1;

    public async Task<IResult> GenerateAsync(HttpContext context)
    {
        Guid? requestId = null;
        PreparedGenerationInput? acceptedInput = null;
        SoloAgentResponse? acceptedResult = null;
        var outcome = "configuration_failure";
        var started = Stopwatch.GetTimestamp();
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(context.RequestAborted);
        try
        {
            if (context.User.Identity?.IsAuthenticated != true)
            {
                outcome = "authentication_failed";
                return Results.Unauthorized();
            }
            var limits = options.Value;
            if (!limits.IsValid() || authorizer is null)
                return CreateError("configuration_failure", StatusCodes.Status503ServiceUnavailable);
            deadline.CancelAfter(limits.RequestTimeout);
            if (context.Request.ContentType?.Split(';')[0].Trim() != "application/json")
                return CreateError("validation_failed", StatusCodes.Status415UnsupportedMediaType);
            if (context.Request.ContentLength > limits.MaxRequestBytes)
                return CreateError("limit_exceeded", StatusCodes.Status413PayloadTooLarge);
            var bytes = await ModelProtocol.ReadBoundedAsync(context.Request.Body, limits.MaxRequestBytes, deadline.Token);
            var request = JsonSerializer.Deserialize<SoloAgentGenerationRequest>(bytes, ModelProtocol.JsonOptions);
            if (request is null || request.Input is null)
                return CreateError("validation_failed", StatusCodes.Status400BadRequest);
            if (request.ContractVersion != ContractVersion)
                return CreateError("unsupported_contract", StatusCodes.Status400BadRequest);
            var input = request.Input;
            if (input.RequestId == Guid.Empty || input.ChatId == Guid.Empty || input.RunId == Guid.Empty ||
                input.MessageId == Guid.Empty || string.IsNullOrWhiteSpace(input.Message) || input.History is null || input.Catalog is null)
                return CreateError("validation_failed", StatusCodes.Status400BadRequest);
            requestId = input.RequestId;
            if (!await authorizer.AuthorizeAndAcceptAsync(context.User, input, deadline.Token).WaitAsync(deadline.Token))
            {
                outcome = "authorization_failed";
                return Results.Forbid();
            }
            acceptedInput = input;
            deadline.Token.ThrowIfCancellationRequested();
            input = authorizer.PrepareGeneration(input);
            var result = await pipeline.GenerateAsync(input, deadline.Token).WaitAsync(deadline.Token);
            deadline.Token.ThrowIfCancellationRequested();
            outcome = result.Outcome;
            var reply = new SoloAgentGenerationReply(ContractVersion, result, null);
            var body = ModelProtocol.SerializeBounded(reply, limits.MaxResponseBytes);
            acceptedResult = result;
            return Results.Bytes(body, "application/json");
        }
        catch (JsonException)
        {
            return CreateError("validation_failed", StatusCodes.Status400BadRequest);
        }
        catch (ModelExchangeException error)
        {
            return CreateError(error.Code == "limit_exceeded" ? error.Code : "configuration_failure",
                error.Code == "limit_exceeded" ? StatusCodes.Status413PayloadTooLarge : StatusCodes.Status503ServiceUnavailable);
        }
        catch (OperationCanceledException) when (!context.RequestAborted.IsCancellationRequested)
        {
            return CreateError("timeout", StatusCodes.Status504GatewayTimeout);
        }
        catch (OperationCanceledException)
        {
            outcome = "cancelled";
            throw;
        }
        catch (Exception)
        {
            return CreateError("provider_error", StatusCodes.Status502BadGateway);
        }
        finally
        {
            if (acceptedInput is not null)
                authorizer!.Complete(acceptedInput, deadline.IsCancellationRequested ? null : acceptedResult);
            logger.LogInformation("solo_agent_generation requestId {RequestId} version {Version} outcome {Outcome} durationMs {DurationMs}",
                requestId, ContractVersion, outcome, Stopwatch.GetElapsedTime(started).TotalMilliseconds);
        }

        IResult CreateError(string code, int status)
        {
            outcome = code;
            return Results.Json(new SoloAgentGenerationReply(ContractVersion, null, code), ModelProtocol.JsonOptions, statusCode: status);
        }
    }
}
