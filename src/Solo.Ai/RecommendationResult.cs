using Solo.Ai.Client;

namespace Solo.Ai;

internal sealed record RecommendationResult(
    string Kind, DocumentCandidate? Candidate, string? Reason, IReadOnlyList<DocumentCandidate> Candidates);
