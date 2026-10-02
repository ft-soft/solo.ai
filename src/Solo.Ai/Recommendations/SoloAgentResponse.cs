namespace Solo.Ai;

public sealed record SoloAgentResponse(Guid RequestId, Guid ChatId, Guid RunId, Guid MessageId, string Outcome)
{
    public string? Text { get; init; }
    public DocumentCandidate? Selection { get; init; }
    public IReadOnlyList<DocumentCandidate> ClarificationOptions { get; init; } = [];
}
