using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;

namespace Solo.Ai.Client;

public sealed class SoloAiClient(IHttpClientFactory clients, SoloAiClientOptions options)
{
    public const string ClientName = "solo-ai";

    public async Task<SoloAgentResponse> GenerateAsync(PreparedGenerationInput input, CancellationToken cancellationToken)
    {
        if (!options.IsValid())
            throw new SoloAiExchangeException("configuration_failure");
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(options.RequestTimeout);
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, new Uri(options.BaseUri!, SoloAiProtocol.Endpoint))
            {
                Content = new ByteArrayContent(SoloAiProtocol.SerializeBounded(new SoloAgentGenerationRequest(SoloAiProtocol.Version, input), options.MaxRequestBytes)),
            };
            request.Content.Headers.ContentType = new MediaTypeHeaderValue("application/json");
            using var client = clients.CreateClient(ClientName);
            using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, deadline.Token);
            if (response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
                throw new SoloAiExchangeException(response.StatusCode == HttpStatusCode.Unauthorized ? "authentication_failed" : "authorization_failed");
            if (response.StatusCode is HttpStatusCode.NotFound or HttpStatusCode.MethodNotAllowed)
                throw new SoloAiExchangeException("unsupported_contract");
            if (response.StatusCode == HttpStatusCode.RequestEntityTooLarge)
                throw new SoloAiExchangeException("limit_exceeded");
            if (response.Content.Headers.ContentLength > options.MaxResponseBytes)
                throw new SoloAiExchangeException("limit_exceeded");
            await using var stream = await response.Content.ReadAsStreamAsync(deadline.Token);
            var bytes = await SoloAiProtocol.ReadBoundedAsync(stream, options.MaxResponseBytes, deadline.Token);
            var reply = JsonSerializer.Deserialize<SoloAgentGenerationReply>(bytes, SoloAiProtocol.JsonOptions)
                ?? throw new SoloAiExchangeException("malformed_model_response");
            SoloAiProtocol.ValidateReply(reply, input, response.IsSuccessStatusCode);
            deadline.Token.ThrowIfCancellationRequested();
            return reply.Result!;
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new SoloAiExchangeException("timeout");
        }
        catch (JsonException)
        {
            throw new SoloAiExchangeException("malformed_model_response");
        }
        catch (Exception error) when (error is HttpRequestException or IOException)
        {
            throw new SoloAiExchangeException("provider_error");
        }
    }
}
