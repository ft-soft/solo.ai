namespace Solo.Ai;

public sealed record DocumentCreateOption(
    Guid FormId, Guid? PresetId, string Name, string? Category, string? Description,
    IReadOnlyList<CreateProfile> Profiles);
