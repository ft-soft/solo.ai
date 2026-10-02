namespace Solo.Ai.Api;

public sealed class SoloAiApiOptions
{
    public long MaxRequestBytes { get; init; } = 524_288;
    public long MaxResponseBytes { get; init; } = 524_288;

    internal void Validate()
    {
        // A valid Chat/Run response always fits this minimum, including maximum escaped title.
        // Thus create/rename/cancel cannot commit and then become a byte-limit rejection.
        if (MaxRequestBytes is <= 0 or > int.MaxValue || MaxResponseBytes is < 16_384 or > int.MaxValue)
            throw new InvalidOperationException("Invalid SoloAiApi configuration.");
    }
}
