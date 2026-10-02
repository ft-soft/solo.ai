using System.Text.Json.Nodes;

namespace Solo.Ai;

public static class RecommendationSchema
{
    public static JsonObject Create()
    {
        using var stream = typeof(RecommendationSchema).Assembly.GetManifestResourceStream("Solo.Ai.recommendation-v1.schema.json")
            ?? throw new InvalidOperationException("Recommendation schema is missing.");
        return JsonNode.Parse(stream)!.AsObject();
    }
}
