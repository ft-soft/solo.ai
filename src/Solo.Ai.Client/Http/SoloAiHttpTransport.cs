using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;

namespace Solo.Ai.Client;

internal sealed class SoloAiHttpTransport(IHttpClientFactory clients, SoloAiClientOptions options)
{
    public async Task<T?> ExchangeAsync<T>(HttpMethod method, string path, object? body,
        int[] successStatuses, Action<T, int>? validate, CancellationToken cancellationToken) where T : class
    {
        if (!options.IsValid()) throw new SoloAiExchangeException("configuration_failure");
        cancellationToken.ThrowIfCancellationRequested();
        if (body is not null) SoloAiValidation.ValidateRequest(body);
        var bytes = body is null ? null : SoloAiProtocol.SerializeBounded(body, options.MaxRequestBytes);
        var command = method != HttpMethod.Get;
        var sent = false;
        var rejected = false;
        HttpStatusCode? status = null;
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(options.RequestTimeout);
        try
        {
            var requestId = Guid.NewGuid().ToString("D");
            var uri = new Uri(options.BaseUri!, SoloAiProtocol.Endpoint + (path.Length == 0 ? "" : "/" + path));
            using var request = new HttpRequestMessage(method, uri);
            request.Headers.Add(SoloAiProtocol.RequestIdHeader, requestId);
            request.Headers.Add(SoloAiProtocol.VersionHeader, "2");
            request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
            if (bytes is not null)
            {
                request.Content = new ByteArrayContent(bytes);
                request.Content.Headers.ContentType = new MediaTypeHeaderValue("application/json");
            }
            using var client = clients.CreateClient(SoloAiClient.ClientName);
            deadline.Token.ThrowIfCancellationRequested();
            sent = true;
            using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, deadline.Token);
            status = response.StatusCode;
            deadline.Token.ThrowIfCancellationRequested();
            // Authentication middleware can reject without a JSON body or application correlation headers.
            if (status is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
            {
                rejected = true;
                throw new SoloAiExchangeException(status == HttpStatusCode.Unauthorized ? "authentication_required" : "forbidden_actor");
            }
            if (!HasSingleHeader(response, SoloAiProtocol.VersionHeader, "2"))
                throw new SoloAiExchangeException("unsupported_contract");
            if (!HasSingleHeader(response, SoloAiProtocol.RequestIdHeader, requestId) ||
                response.RequestMessage?.RequestUri is { } actualUri && actualUri != uri)
                throw new SoloAiExchangeException("invalid_response");
            if (response.Content.Headers.ContentLength > options.MaxResponseBytes)
                throw new SoloAiExchangeException("limit_exceeded");
            await using var stream = await response.Content.ReadAsStreamAsync(deadline.Token);
            var responseBytes = await SoloAiProtocol.ReadBoundedAsync(stream, options.MaxResponseBytes, deadline.Token);
            deadline.Token.ThrowIfCancellationRequested();
            if (status == HttpStatusCode.NoContent)
            {
                if (!successStatuses.Contains(204) || responseBytes.Length != 0)
                    throw new SoloAiExchangeException("invalid_response");
                return null;
            }
            var contentType = response.Content.Headers.ContentType;
            if (!string.Equals(contentType?.MediaType, "application/json", StringComparison.OrdinalIgnoreCase) ||
                contentType?.CharSet is { } charset && !string.Equals(charset.Trim('"'), "utf-8", StringComparison.OrdinalIgnoreCase))
                throw new SoloAiExchangeException("invalid_response");
            var reply = SoloAiProtocol.Deserialize<SoloAiResponse<T>>(responseBytes);
            if (reply.ContractVersion != SoloAiProtocol.Version)
                throw new SoloAiExchangeException("unsupported_contract");
            if (!response.IsSuccessStatusCode)
            {
                if (reply.Data is not null || !SoloAiValidation.IsHttpErrorValid((int)status, reply.Error))
                    throw new SoloAiExchangeException("invalid_response");
                rejected = true;
                throw new SoloAiExchangeException(reply.Error!);
            }
            if (!successStatuses.Contains((int)status) || reply.Data is null || reply.Error is not null)
                throw new SoloAiExchangeException("invalid_response");
            validate?.Invoke(reply.Data, (int)status);
            deadline.Token.ThrowIfCancellationRequested();
            return reply.Data;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw new SoloAiRequestCanceledException(command && sent && !rejected, cancellationToken);
        }
        catch (OperationCanceledException)
        {
            throw new SoloAiExchangeException("http_timeout", command && sent && !rejected, status);
        }
        catch (SoloAiExchangeException error)
        {
            throw new SoloAiExchangeException(error.Code, command && sent && !rejected, status);
        }
        catch (JsonException)
        {
            throw new SoloAiExchangeException("invalid_response", command && sent && !rejected, status);
        }
        catch (Exception error) when (error is HttpRequestException or IOException)
        {
            throw new SoloAiExchangeException("transport_error", command && sent && !rejected, status);
        }
    }

    private static bool HasSingleHeader(HttpResponseMessage response, string name, string expected) =>
        response.Headers.TryGetValues(name, out var values) && values.SequenceEqual([expected]);
}
