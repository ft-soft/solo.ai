using System.Security.Claims;
using System.Text.Json;

namespace Solo.Ai.Api;

internal static class ButlerIdentity
{
    public static bool IsDelegatedUser(ClaimsPrincipal principal, string allowedClient)
    {
        if (principal.Identity?.IsAuthenticated != true || GetSingleClaim(principal, "account_type") != "User" ||
            !Guid.TryParseExact(GetSingleClaim(principal, "sub"), "D", out var subject) || subject == Guid.Empty ||
            !Guid.TryParseExact(GetSingleClaim(principal, "user_id"), "D", out var user) || user != subject ||
            GetSingleClaim(principal, "client_id") != allowedClient)
            return false;
        var actor = GetSingleClaim(principal, "act");
        if (actor is null) return false;
        try
        {
            using var document = JsonDocument.Parse(actor);
            if (document.RootElement.ValueKind != JsonValueKind.Object) return false;
            var properties = document.RootElement.EnumerateObject().ToArray();
            return properties.Length == 1 && properties[0].Name == "sub" &&
                properties[0].Value.ValueKind == JsonValueKind.String && properties[0].Value.GetString() == allowedClient;
        }
        catch (JsonException) { return false; }
    }

    // Called only behind the named delegated-user policy. Normalize the GUID once for storage.
    public static string GetOwner(ClaimsPrincipal principal) => Guid.Parse(GetSingleClaim(principal, "sub")!).ToString("D");

    private static string? GetSingleClaim(ClaimsPrincipal principal, string type)
    {
        var claims = principal.FindAll(type).Take(2).ToArray();
        return claims.Length == 1 ? claims[0].Value : null;
    }
}
