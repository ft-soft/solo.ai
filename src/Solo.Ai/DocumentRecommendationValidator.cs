using System.Text.Json;
using Solo.Ai.Client;
using Solo.Ai.Visograph;

namespace Solo.Ai;

public sealed class DocumentRecommendationValidator
{
    public SoloAgentResponse ValidateAndRender(string content, PreparedGenerationInput input)
    {
        RecommendationResult result;
        try
        {
            using var document = JsonDocument.Parse(content, new JsonDocumentOptions { AllowDuplicateProperties = false });
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object ||
                !new[] { "kind", "candidate", "reason", "candidates" }.All(name => root.TryGetProperty(name, out _)))
                return CreateFailure(input, "malformed_model_response");
            if (root.GetProperty("candidate").ValueKind != JsonValueKind.Null)
                ValidateCandidateShape(root.GetProperty("candidate"));
            foreach (var candidate in root.GetProperty("candidates").EnumerateArray())
                ValidateCandidateShape(candidate);
            result = JsonSerializer.Deserialize<RecommendationResult>(content, ModelProtocol.JsonOptions)!;
        }
        catch (Exception error) when (error is JsonException or InvalidOperationException or ModelExchangeException)
        {
            return CreateFailure(input, "malformed_model_response");
        }

        var response = CreateFailure(input, result.Kind);
        switch (result)
        {
            case { Kind: "no_match", Candidate: null, Reason: null, Candidates.Count: 0 }:
                return response with { Text = "В доступном наборе не найден подходящий вариант. Опишите, какой документ нужно оформить." };
            case { Kind: "recommendation", Candidate: not null, Reason: null, Candidates.Count: 0 }:
                if (!TryFindOption(result.Candidate, input.Catalog, out var option))
                    return CreateFailure(input, "recommendation_rejected");
                return response with { Selection = result.Candidate, Text = $"Рекомендуемый документ: {option!.Name}." };
            case { Kind: "clarification", Candidate: null, Reason: "intent", Candidates.Count: 0 }:
                return response with { Text = "Уточните, какой документ нужно оформить." };
            case { Kind: "clarification", Candidate: null, Reason: "variant" or "profile", Candidates.Count: >= 2 }:
                if (result.Candidates.Distinct().Count() != result.Candidates.Count ||
                    result.Candidates.Any(candidate => !TryFindOption(candidate, input.Catalog, out _)))
                    return CreateFailure(input, "recommendation_rejected");
                var variants = result.Candidates.Select(candidate => (candidate.FormId, candidate.PresetId)).Distinct().Count();
                if (result.Reason == "variant" && variants != result.Candidates.Count ||
                    result.Reason == "profile" && (variants != 1 || result.Candidates.Any(candidate => candidate.ProfileId is null)))
                    return CreateFailure(input, "malformed_model_response");
                var labels = result.Candidates.Select(candidate => RenderLabel(candidate, input.Catalog, result.Reason == "profile"));
                return response with
                {
                    Text = (result.Reason == "profile" ? "Уточните рабочий профиль:\n" : "Уточните нужный вариант:\n") + string.Join("\n", labels),
                    ClarificationOptions = result.Candidates,
                };
            default:
                return CreateFailure(input, "malformed_model_response");
        }
    }

    private static void ValidateCandidateShape(JsonElement candidate)
    {
        if (candidate.ValueKind != JsonValueKind.Object ||
            !new[] { "formId", "presetId", "profileId" }.All(name => candidate.TryGetProperty(name, out _)))
            throw new ModelExchangeException("malformed_model_response", "response");
    }

    private static bool TryFindOption(DocumentCandidate candidate, IReadOnlyList<DocumentCreateOption> catalog, out DocumentCreateOption? option)
    {
        option = catalog.SingleOrDefault(item => item.FormId == candidate.FormId && item.PresetId == candidate.PresetId);
        return option is not null && (candidate.ProfileId is null || option.Profiles.Any(profile => profile.Id == candidate.ProfileId));
    }

    private static string RenderLabel(DocumentCandidate candidate, IReadOnlyList<DocumentCreateOption> catalog, bool includeProfile)
    {
        TryFindOption(candidate, catalog, out var option);
        if (!includeProfile)
            return option!.Name;
        var profile = option!.Profiles.Single(item => item.Id == candidate.ProfileId);
        return profile.DisplayName ?? "Профиль без отображаемого названия";
    }

    internal static SoloAgentResponse CreateFailure(PreparedGenerationInput input, string outcome) =>
        new(input.RequestId, input.ChatId, input.RunId, input.MessageId, outcome);
}
