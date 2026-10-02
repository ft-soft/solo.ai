using System.Net;
using System.Security.Cryptography;
using Microsoft.IdentityModel.Tokens;
using Xunit;
using static Solo.Ai.Tests.ButlerApiFixture;

namespace Solo.Ai.Tests;

public sealed class ButlerAuthenticationTests
{
    [Fact]
    public async Task AcceptSignedDelegatedUserWithoutScopeAndCacheJwks()
    {
        using var api = await StartAsync();
        using var first = await api.SendAsync("GET", "/api/v2/chats/current");
        using var second = await api.SendAsync("GET", "/api/v2/chats/current");
        Assert.Equal(HttpStatusCode.NoContent, first.StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, second.StatusCode);
        Assert.Equal(1, api.JwksRequests);
    }

    [Theory]
    [InlineData("issuer", 401)]
    [InlineData("audience", 401)]
    [InlineData("audience-slash", 401)]
    [InlineData("expired", 401)]
    [InlineData("no-expiry", 401)]
    [InlineData("future", 401)]
    [InlineData("type", 401)]
    [InlineData("signature", 401)]
    [InlineData("unsigned", 401)]
    [InlineData("algorithm", 401)]
    [InlineData("malformed", 401)]
    [InlineData("missing", 401)]
    [InlineData("actor", 403)]
    [InlineData("client", 403)]
    [InlineData("service", 403)]
    [InlineData("user-mismatch", 403)]
    [InlineData("missing-user", 403)]
    [InlineData("missing-sub", 403)]
    [InlineData("invalid-sub", 403)]
    [InlineData("duplicate-user", 403)]
    [InlineData("duplicate-client", 403)]
    [InlineData("duplicate-account", 403)]
    [InlineData("malformed-act", 403)]
    [InlineData("duplicate-act-sub", 403)]
    [InlineData("nested-act", 403)]
    public async Task RejectInvalidTokenOrDelegation(string fault, int status)
    {
        using var api = await StartAsync();
        using var wrongRsa = RSA.Create(2048);
        var token = api.CreateToken(change: descriptor =>
        {
            switch (fault)
            {
                case "issuer": descriptor.Issuer = "https://other.test"; break;
                case "audience": descriptor.Audience = "other.api"; break;
                case "audience-slash": descriptor.Audience = Audience + "/"; break;
                case "expired": descriptor.IssuedAt = descriptor.NotBefore = DateTime.UtcNow.AddMinutes(-2); descriptor.Expires = DateTime.UtcNow.AddMinutes(-1); break;
                case "no-expiry": descriptor.Expires = null; break;
                case "future": descriptor.NotBefore = DateTime.UtcNow.AddSeconds(30); break;
                case "type": descriptor.TokenType = "JWT"; break;
                case "signature": descriptor.SigningCredentials = new(new RsaSecurityKey(wrongRsa) { KeyId = api.Key.KeyId }, SecurityAlgorithms.RsaSha256); break;
                case "unsigned": descriptor.SigningCredentials = null; break;
                case "algorithm": descriptor.SigningCredentials = new(api.Key, SecurityAlgorithms.RsaSha512); break;
                case "actor": descriptor.Claims["act"] = new Dictionary<string, object> { ["sub"] = "other-client" }; break;
                case "client": descriptor.Claims["client_id"] = "other-client"; break;
                case "service": descriptor.Claims["account_type"] = "Service"; descriptor.Claims["sub"] = Actor; descriptor.Claims.Remove("user_id"); break;
                case "user-mismatch": descriptor.Claims["user_id"] = Bob; break;
                case "missing-user": descriptor.Claims.Remove("user_id"); break;
                case "missing-sub": descriptor.Claims.Remove("sub"); break;
                case "invalid-sub": descriptor.Claims["sub"] = descriptor.Claims["user_id"] = "display-name"; break;
                case "duplicate-user": descriptor.Claims["user_id"] = new[] { Alice, Bob }; break;
                case "duplicate-client": descriptor.Claims["client_id"] = new[] { Actor, "other" }; break;
                case "duplicate-account": descriptor.Claims["account_type"] = new[] { "User", "Service" }; break;
                case "malformed-act": descriptor.Claims["act"] = "{"; break;
                case "duplicate-act-sub": descriptor.Claims["act"] = "{\"sub\":\"other\",\"sub\":\"solo-ai-delegation\"}"; break;
                case "nested-act": descriptor.Claims["act"] = new Dictionary<string, object> { ["sub"] = Actor, ["act"] = new Dictionary<string, object> { ["sub"] = "other" } }; break;
            }
        });
        using var response = await api.SendAsync("GET", "/api/v2/chats/current", token: fault switch { "missing" => "", "malformed" => "not-a-token", _ => token });
        Assert.Equal(status, (int)response.StatusCode);
        Assert.DoesNotContain(token, await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
        Assert.Null(api.Storage.Chats.GetCurrentChat(Alice));
    }

    [Theory]
    [InlineData("SoloAiAuthentication:Issuer", "")]
    [InlineData("SoloAiAuthentication:Audience", "")]
    [InlineData("SoloAiAuthentication:AllowedClientId", "")]
    [InlineData("SoloAiAuthentication:JwksUri", "")]
    [InlineData("SoloAiAuthentication:UseFakes", "true")]
    [InlineData("Butler:UseFakes", "true")]
    [InlineData("SoloAiAuthentication:AllowHttpMetadata", "true")]
    [InlineData("SoloAiAuthentication:Issuer", "http://butler.test")]
    [InlineData("SoloAiAuthentication:JwksUri", "http://butler.test/keys")]
    [InlineData("SoloAiAuthentication:ClockSkewSeconds", "31")]
    public async Task FailStartupForMissingOrUnsafeProductionConfiguration(string key, string value) =>
        await Assert.ThrowsAsync<InvalidOperationException>(() => StartAsync(new() { [key] = value }));

    [Fact]
    public async Task RejectWhenTrustedKeysCannotBeLoaded()
    {
        using var api = await StartAsync();
        api.JwksAvailable = false;
        using var response = await api.SendAsync("GET", "/api/v2/chats/current");
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task InitializeRealBackchannelWithoutDiscoveryOrToken()
    {
        using var api = await StartAsync(mockBackchannel: false);
        using var response = await api.SendAsync("GET", "/api/v2/chats/current", token: "");
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }
}
