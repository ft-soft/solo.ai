using System.Text.Json.Serialization;

namespace Solo.Ai.Visograph;

public sealed record ModelGenerationResponse(int ContractVersion, Guid? RequestId, string Outcome)
{
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Stage { get; init; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Content { get; init; }

    public ModelUsage? Usage { get; init; }
}
