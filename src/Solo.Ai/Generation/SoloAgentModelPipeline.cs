using System.Text.Json;
using System.Text.Json.Nodes;
using Solo.Ai.Client;
using Solo.Ai.Visograph;

namespace Solo.Ai;

public sealed class SoloAgentModelPipeline(IModelGenerationClient client, SoloAgentModelOptions options)
    : ISoloAgentModelPipeline
{
    private const string Instructions = "You are a helpful assistant in a personal chat. Reply in the user's language. " +
        "Return plain text with line breaks in the required JSON text field. Do not use HTML or Markdown formatting.";

    public async Task<string> GenerateAsync(Guid runId, IReadOnlyList<ModelMessage> history, CancellationToken cancellationToken)
    {
        if (!options.IsValid()) throw new ModelExchangeException("configuration_failure", "configuration");
        var schema = JsonNode.Parse("""
            {"type":"object","properties":{"text":{"type":"string","minLength":1}},"required":["text"],"additionalProperties":false}
            """)!.AsObject();
        var messages = new List<ModelMessage> { new("system", Instructions) };
        messages.AddRange(history);
        var request = new ModelGenerationRequest(ModelProtocol.Version, runId, ModelProtocol.Capabilities,
            messages, new("json_schema", "SoloChatTextV1", schema));
        ModelProtocol.ValidateRequest(request);
        ModelProtocol.SerializeBounded(request, options.MaxRequestBytes);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(options.ModelTimeout < options.TotalDeadline ? options.ModelTimeout : options.TotalDeadline);
        try
        {
            timeout.Token.ThrowIfCancellationRequested();
            var response = await client.GenerateAsync(request, timeout.Token).WaitAsync(timeout.Token);
            ModelProtocol.ValidateResponse(response, runId);
            timeout.Token.ThrowIfCancellationRequested();
            if (response.Outcome != "completed") throw new ModelExchangeException(response.Outcome, "response");
            using var document = JsonDocument.Parse(response.Content!, new JsonDocumentOptions { AllowDuplicateProperties = false });
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object || root.EnumerateObject().Count() != 1 ||
                !root.TryGetProperty("text", out var text) || text.ValueKind != JsonValueKind.String)
                throw new ModelExchangeException("malformed_model_response", "response");
            var result = text.GetString()!;
            SoloAiValidation.ValidateText(result, SoloAiProtocol.MaximumTextBytes);
            timeout.Token.ThrowIfCancellationRequested();
            return result;
        }
        catch (JsonException) { throw new ModelExchangeException("malformed_model_response", "response"); }
        catch (SoloAiExchangeException error)
        {
            throw new ModelExchangeException(error.Code == "limit_exceeded" ? error.Code : "malformed_model_response", "response");
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new ModelExchangeException("timeout", "transport");
        }
    }
}
