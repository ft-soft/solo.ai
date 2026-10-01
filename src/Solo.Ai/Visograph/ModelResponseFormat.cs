using System.Text.Json.Nodes;

namespace Solo.Ai.Visograph;

public sealed record ModelResponseFormat(string Kind, string Name, JsonObject Schema);
