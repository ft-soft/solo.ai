using System.Text.Json;

namespace Solo.Ai.Client;

public static class SoloAiProtocol
{
    public const int Version = 1;
    public const string Endpoint = "api/v1/solo-agent/generations";
    public static JsonSerializerOptions JsonOptions { get; } = CreateJsonOptions();

    public static byte[] SerializeBounded<T>(T value, long maximumBytes)
    {
        if (maximumBytes is <= 0 or > int.MaxValue)
            throw new SoloAiExchangeException("configuration_failure");
        using var body = new BoundedSerializationStream(maximumBytes);
        JsonSerializer.Serialize(body, value, JsonOptions);
        return body.ToArray();
    }

    public static async Task<byte[]> ReadBoundedAsync(Stream stream, long maximumBytes, CancellationToken cancellationToken)
    {
        if (maximumBytes is <= 0 or > int.MaxValue)
            throw new SoloAiExchangeException("configuration_failure");
        using var body = new MemoryStream();
        var buffer = new byte[8192];
        int count;
        while ((count = await stream.ReadAsync(buffer, cancellationToken)) != 0)
        {
            if (body.Length + count > maximumBytes)
                throw new SoloAiExchangeException("limit_exceeded");
            await body.WriteAsync(buffer.AsMemory(0, count), cancellationToken);
        }
        return body.ToArray();
    }

    public static void ValidateReply(SoloAgentGenerationReply reply, PreparedGenerationInput input, bool success)
    {
        if (reply.ContractVersion != Version)
            throw new SoloAiExchangeException("unsupported_contract");
        if (reply.Result is null)
        {
            if (success || reply.Error is not ("validation_failed" or "unsupported_contract" or "configuration_failure" or "limit_exceeded" or "timeout" or "provider_error"))
                throw new SoloAiExchangeException("malformed_model_response");
            throw new SoloAiExchangeException(reply.Error);
        }
        var result = reply.Result;
        if (!success || reply.Error is not null || result.RequestId != input.RequestId || result.ChatId != input.ChatId ||
            result.RunId != input.RunId || result.MessageId != input.MessageId || result.ClarificationOptions is null)
            throw new SoloAiExchangeException("malformed_model_response");
        ValidateResult(result);
        if (result.Selection is not null && !IsAllowed(result.Selection, input.Catalog) ||
            result.ClarificationOptions.Any(candidate => candidate is null || !IsAllowed(candidate, input.Catalog)))
            throw new SoloAiExchangeException("recommendation_rejected");
    }

    public static void ValidateResult(SoloAgentResponse result)
    {
        if (result.ClarificationOptions is null)
            throw new SoloAiExchangeException("malformed_model_response");
        var userOutcome = result.Outcome is "recommendation" or "clarification" or "no_match" or "empty_catalog";
        if (result.Outcome is not ("recommendation" or "clarification" or "no_match" or "empty_catalog" or
            "authentication_failed" or "authorization_failed" or "validation_failed" or "unsupported_contract" or
            "configuration_failure" or "catalog_read_failed" or "limit_exceeded" or "provider_error" or "timeout" or
            "cancelled" or "project_unavailable" or "incomplete_response" or "malformed_model_response" or "recommendation_rejected") ||
            userOutcome && string.IsNullOrWhiteSpace(result.Text) || !userOutcome && result.Text is not null ||
            result.Outcome == "recommendation" && (result.Selection is null || result.ClarificationOptions.Count != 0) ||
            result.Outcome != "recommendation" && result.Selection is not null ||
            result.Outcome != "clarification" && result.ClarificationOptions.Count != 0)
            throw new SoloAiExchangeException("malformed_model_response");
    }

    private static bool IsAllowed(DocumentCandidate candidate, IReadOnlyList<DocumentCreateOption> catalog) =>
        catalog.Any(option => option.FormId == candidate.FormId && option.PresetId == candidate.PresetId &&
            (candidate.ProfileId is null || option.Profiles.Any(profile => profile.Id == candidate.ProfileId)));

    private static JsonSerializerOptions CreateJsonOptions()
    {
        var options = new JsonSerializerOptions(JsonSerializerDefaults.Web)
        {
            PropertyNameCaseInsensitive = false, AllowDuplicateProperties = false, RespectRequiredConstructorParameters = true,
            UnmappedMemberHandling = System.Text.Json.Serialization.JsonUnmappedMemberHandling.Disallow,
        };
        options.MakeReadOnly(populateMissingResolver: true);
        return options;
    }

    private sealed class BoundedSerializationStream(long maximumBytes) : MemoryStream
    {
        public override void Write(ReadOnlySpan<byte> buffer) { CheckLimit(buffer.Length); base.Write(buffer); }
        public override void Write(byte[] buffer, int offset, int count) { CheckLimit(count); base.Write(buffer, offset, count); }
        private void CheckLimit(int count)
        {
            if (Position + count > maximumBytes)
                throw new SoloAiExchangeException("limit_exceeded");
        }
    }
}
