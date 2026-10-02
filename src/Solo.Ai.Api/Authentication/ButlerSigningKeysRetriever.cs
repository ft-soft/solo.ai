using Microsoft.IdentityModel.Protocols;
using Microsoft.IdentityModel.Protocols.OpenIdConnect;
using Microsoft.IdentityModel.Tokens;

namespace Solo.Ai.Api;

// The installed BOX discovery advertises the wrong jwks_uri. Use the configured,
// trusted JWKS address with IdentityModel's standard parser, cache and key refresh.
internal sealed class ButlerSigningKeysRetriever(string issuer) : IConfigurationRetriever<OpenIdConnectConfiguration>
{
    public async Task<OpenIdConnectConfiguration> GetConfigurationAsync(string address,
        IDocumentRetriever retriever, CancellationToken cancel)
    {
        var keys = new JsonWebKeySet(await retriever.GetDocumentAsync(address, cancel));
        var configuration = new OpenIdConnectConfiguration { Issuer = issuer, JwksUri = address, JsonWebKeySet = keys };
        foreach (var key in keys.GetSigningKeys()) configuration.SigningKeys.Add(key);
        if (configuration.SigningKeys.Count == 0) throw new InvalidOperationException("No Butler signing keys.");
        return configuration;
    }
}
