using System.Text.Json;

namespace Solo.Ai.Visograph;

public static class ModelProtocol
{
    public const int Version = 1;
    public const string Endpoint = "api/v1/model/generations";
    public static IReadOnlyList<string> Capabilities => ["ordered_messages", "non_streaming", "json_schema"];
    public static JsonSerializerOptions JsonOptions { get; } = CreateJsonOptions();

    public static ModelGenerationRequest ParseRequest(ReadOnlyMemory<byte> bytes)
    {
        try
        {
            using var document = JsonDocument.Parse(bytes, new JsonDocumentOptions { AllowDuplicateProperties = false });
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
                throw new ModelExchangeException("validation_failed", "validation");
            if (root.TryGetProperty("contractVersion", out var version) &&
                version.ValueKind == JsonValueKind.Number && version.TryGetInt32(out var number) && number != Version)
                throw new ModelExchangeException("unsupported_contract", "validation");
            if (root.TryGetProperty("tools", out _) || root.TryGetProperty("toolCalls", out _))
                throw new ModelExchangeException("unsupported_contract", "validation");
            var request = JsonSerializer.Deserialize<ModelGenerationRequest>(bytes.Span, JsonOptions)
                ?? throw new ModelExchangeException("validation_failed", "validation");
            ValidateRequest(request);
            return request;
        }
        catch (JsonException)
        {
            throw new ModelExchangeException("validation_failed", "validation");
        }
    }

    public static void ValidateRequest(ModelGenerationRequest request)
    {
        if (request.ContractVersion != Version)
            throw new ModelExchangeException("unsupported_contract", "validation");
        if (request.RequiredCapabilities is null ||
            !request.RequiredCapabilities.Order(StringComparer.Ordinal).SequenceEqual(Capabilities.Order(StringComparer.Ordinal)))
            throw new ModelExchangeException("unsupported_contract", "validation");
        if (request.RequestId == Guid.Empty || request.Messages is not { Count: > 0 } ||
            request.ResponseFormat is not { Kind: "json_schema", Schema: not null } ||
            string.IsNullOrWhiteSpace(request.ResponseFormat.Name))
            throw new ModelExchangeException("validation_failed", "validation");
        foreach (var message in request.Messages)
        {
            if (message is null || string.IsNullOrWhiteSpace(message.Content))
                throw new ModelExchangeException("validation_failed", "validation");
            if (message.Role is not ("system" or "user" or "assistant"))
                throw new ModelExchangeException("unsupported_contract", "validation");
        }
    }

    public static void ValidateResponse(ModelGenerationResponse response, Guid requestId)
    {
        if (response.ContractVersion != Version)
            throw new ModelExchangeException("unsupported_contract", "response");
        if (response.RequestId is { } correlation && correlation != requestId ||
            response.Outcome == "completed" && response.RequestId != requestId || response.Outcome is not
            ("completed" or "project_unavailable" or "validation_failed" or "unsupported_contract" or
             "configuration_failure" or "limit_exceeded" or "provider_error" or "timeout" or "incomplete_response"))
            throw new ModelExchangeException("malformed_model_response", "response");
        if (response.Outcome == "completed" && (string.IsNullOrWhiteSpace(response.Content) || response.Stage is not null) ||
            response.Outcome != "completed" && (response.Content is not null || string.IsNullOrWhiteSpace(response.Stage)) ||
            response.Usage is { InputTokens: < 0 } or { OutputTokens: < 0 } or { TotalTokens: < 0 })
            throw new ModelExchangeException("malformed_model_response", "response");
    }

    public static async Task<byte[]> ReadBoundedAsync(Stream stream, long maximumBytes, CancellationToken cancellationToken)
    {
        if (maximumBytes is <= 0 or > int.MaxValue)
            throw new ModelExchangeException("configuration_failure", "budget");
        using var body = new MemoryStream();
        var buffer = new byte[8192];
        int count;
        while ((count = await stream.ReadAsync(buffer, cancellationToken)) != 0)
        {
            if (body.Length + count > maximumBytes)
                throw new ModelExchangeException("limit_exceeded", "budget");
            await body.WriteAsync(buffer.AsMemory(0, count), cancellationToken);
        }
        return body.ToArray();
    }

    public static byte[] SerializeBounded<T>(T value, long maximumBytes)
    {
        if (maximumBytes is <= 0 or > int.MaxValue)
            throw new ModelExchangeException("configuration_failure", "budget");
        using var body = new BoundedSerializationStream(maximumBytes);
        JsonSerializer.Serialize(body, value, JsonOptions);
        return body.ToArray();
    }

    private static JsonSerializerOptions CreateJsonOptions()
    {
        var options = new JsonSerializerOptions(JsonSerializerDefaults.Web)
        {
            PropertyNameCaseInsensitive = false,
            AllowDuplicateProperties = false,
            RespectRequiredConstructorParameters = true,
            UnmappedMemberHandling = System.Text.Json.Serialization.JsonUnmappedMemberHandling.Disallow,
        };
        options.MakeReadOnly(populateMissingResolver: true);
        return options;
    }

    private sealed class BoundedSerializationStream(long maximumBytes) : MemoryStream
    {
        public override void Write(ReadOnlySpan<byte> buffer)
        {
            CheckLimit(buffer.Length);
            base.Write(buffer);
        }

        public override void Write(byte[] buffer, int offset, int count)
        {
            CheckLimit(count);
            base.Write(buffer, offset, count);
        }

        private void CheckLimit(int count)
        {
            if (Position + count > maximumBytes)
                throw new ModelExchangeException("limit_exceeded", "budget");
        }
    }
}
