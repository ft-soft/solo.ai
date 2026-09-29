using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;

namespace Solo.Ai.Visograph;

public sealed class VisographModelClient : IModelGenerationClient, IDisposable
{
    private readonly ModelClientOptions _options;
    private readonly HttpClient _client;

    public VisographModelClient(ModelClientOptions options, HttpMessageHandler? handler = null)
    {
        _options = options;
        _client = new HttpClient(handler ?? new SocketsHttpHandler { AllowAutoRedirect = false })
        {
            Timeout = TimeSpan.FromDays(1),
        };
    }

    public async Task<ModelGenerationResponse> GenerateAsync(ModelGenerationRequest request, CancellationToken cancellationToken)
    {
        if (!_options.IsValid())
            throw new ModelExchangeException("configuration_failure", "routing");
        ModelProtocol.ValidateRequest(request);
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(_options.RequestTimeout);
        try
        {
            using var message = new HttpRequestMessage(HttpMethod.Post, new Uri(_options.BaseUri!, ModelProtocol.Endpoint))
            {
                Content = new ByteArrayContent(ModelProtocol.SerializeBounded(request, _options.MaxRequestBytes)),
            };
            message.Content.Headers.ContentType = new MediaTypeHeaderValue("application/json");
            message.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _options.ApiKey);
            using var response = await _client.SendAsync(message, HttpCompletionOption.ResponseHeadersRead, deadline.Token);
            if (response.StatusCode == HttpStatusCode.Unauthorized)
                throw new ModelExchangeException("authentication_failed", "authentication");
            if (response.StatusCode is HttpStatusCode.NotFound or HttpStatusCode.MethodNotAllowed)
                throw new ModelExchangeException("unsupported_contract", "response");
            if (response.Content.Headers.ContentLength > _options.MaxResponseBytes)
                throw new ModelExchangeException("limit_exceeded", "response");
            await using var stream = await response.Content.ReadAsStreamAsync(deadline.Token);
            var bytes = await ModelProtocol.ReadBoundedAsync(stream, _options.MaxResponseBytes, deadline.Token);
            var result = JsonSerializer.Deserialize<ModelGenerationResponse>(bytes, ModelProtocol.JsonOptions)
                ?? throw new ModelExchangeException("malformed_model_response", "response");
            ModelProtocol.ValidateResponse(result, request.RequestId);
            if (response.IsSuccessStatusCode != (result.Outcome == "completed"))
                throw new ModelExchangeException("malformed_model_response", "response");
            deadline.Token.ThrowIfCancellationRequested();
            return result;
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new ModelExchangeException("timeout", "transport");
        }
        catch (JsonException)
        {
            throw new ModelExchangeException("malformed_model_response", "response");
        }
        catch (HttpRequestException)
        {
            throw new ModelExchangeException("provider_error", "transport");
        }
    }

    public void Dispose() => _client.Dispose();
}
