namespace Solo.Ai.Api;

public sealed class ButlerAuthenticationOptions
{
    public string Issuer { get; init; } = "";
    public string Audience { get; init; } = "";
    public string AllowedClientId { get; init; } = "";
    public string JwksUri { get; init; } = "";
    public bool AllowHttpMetadata { get; init; }
    public bool UseFakes { get; init; }
    public int ClockSkewSeconds { get; init; } = 5;

    internal void Validate(bool development)
    {
        if (UseFakes || AllowHttpMetadata && !development ||
            !IsAddressValid(Issuer) || !IsAddressValid(JwksUri) ||
            string.IsNullOrWhiteSpace(Audience) || Audience != Audience.Trim() ||
            string.IsNullOrWhiteSpace(AllowedClientId) || AllowedClientId != AllowedClientId.Trim() ||
            ClockSkewSeconds is < 0 or > 30)
            throw new InvalidOperationException("Invalid SoloAiAuthentication configuration.");
    }

    private bool IsAddressValid(string value) => Uri.TryCreate(value, UriKind.Absolute, out var uri) &&
        string.IsNullOrEmpty(uri.UserInfo) && string.IsNullOrEmpty(uri.Fragment) &&
        (uri.Scheme == "https" || AllowHttpMetadata && uri.Scheme == "http");
}
