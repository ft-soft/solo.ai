using System.Net;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using System.Text.Encodings.Web;
using Microsoft.AspNetCore.Authentication;
using Microsoft.Extensions.Options;

namespace Solo.Ai.Host;

public sealed class SoloBackendAuthenticationHandler(
    IOptionsMonitor<AuthenticationSchemeOptions> options, ILoggerFactory logger,
    UrlEncoder encoder, IConfiguration configuration)
    : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
{
    protected override Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        if (!Request.IsHttps && (Context.Connection.RemoteIpAddress is not { } address || !IPAddress.IsLoopback(address)))
            return Task.FromResult(AuthenticateResult.Fail("Secure service transport required."));
        var secret = configuration["SoloBackend:ApiKey"];
        var header = Request.Headers.Authorization.ToString();
        var userId = Request.Headers["X-Solo-User-Id"];
        if (string.IsNullOrWhiteSpace(secret) || secret.Length < 40 || !header.StartsWith("Bearer ", StringComparison.Ordinal) ||
            userId.Count != 1 || !Guid.TryParse(userId[0], out var owner) || owner == Guid.Empty)
            return Task.FromResult(AuthenticateResult.Fail("Service credential and delegated identity required."));
        if (!CryptographicOperations.FixedTimeEquals(SHA256.HashData(Encoding.UTF8.GetBytes(secret)),
            SHA256.HashData(Encoding.UTF8.GetBytes(header[7..]))))
            return Task.FromResult(AuthenticateResult.Fail("Invalid service credential."));
        var identity = new ClaimsIdentity([
            new Claim(ClaimTypes.NameIdentifier, owner.ToString()), new Claim(ClaimTypes.Role, "solo-backend")], Scheme.Name);
        return Task.FromResult(AuthenticateResult.Success(new AuthenticationTicket(new ClaimsPrincipal(identity), Scheme.Name)));
    }
}
