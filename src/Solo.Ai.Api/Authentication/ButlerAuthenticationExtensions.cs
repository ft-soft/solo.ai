using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.IdentityModel.Protocols;
using Microsoft.IdentityModel.Protocols.OpenIdConnect;
using Microsoft.IdentityModel.Tokens;

namespace Solo.Ai.Api;

public static class ButlerAuthenticationExtensions
{
    public const string Scheme = "SoloAiButler";
    public const string Policy = "SoloAiDelegatedUser";

    public static IServiceCollection AddSoloAiAuthentication(this IServiceCollection services,
        IConfiguration configuration, IHostEnvironment environment)
    {
        var settings = configuration.GetSection("SoloAiAuthentication").Get<ButlerAuthenticationOptions>() ?? new();
        settings.Validate(environment.IsDevelopment());
        if (configuration.GetValue<bool>("Butler:UseFakes"))
            throw new InvalidOperationException("Fake Butler authentication is not supported by Solo AI.");
        services.AddHttpClient(Scheme, client => client.Timeout = TimeSpan.FromSeconds(10))
            .RemoveAllLoggers()
            .ConfigurePrimaryHttpMessageHandler(() => new HttpClientHandler { AllowAutoRedirect = false });
        services.AddAuthentication().AddJwtBearer(Scheme, options =>
        {
            options.MapInboundClaims = false;
            options.SaveToken = false;
            options.IncludeErrorDetails = false;
            options.RequireHttpsMetadata = !settings.AllowHttpMetadata;
            options.TokenValidationParameters = new()
            {
                ValidateIssuer = true, ValidIssuer = settings.Issuer,
                ValidateAudience = true, ValidAudience = settings.Audience, IgnoreTrailingSlashWhenValidatingAudience = false,
                ValidateIssuerSigningKey = true, RequireSignedTokens = true,
                ValidateLifetime = true, RequireExpirationTime = true,
                ClockSkew = TimeSpan.FromSeconds(settings.ClockSkewSeconds),
                ValidTypes = ["at+jwt"], ValidAlgorithms = [SecurityAlgorithms.RsaSha256],
                NameClaimType = "sub", LogTokenId = false, IncludeTokenOnFailedValidation = false,
            };
        });
        services.AddOptions<JwtBearerOptions>(Scheme).PostConfigure<IHttpClientFactory>((options, clients) =>
        {
            options.Backchannel ??= clients.CreateClient(Scheme);
            options.ConfigurationManager = new ConfigurationManager<OpenIdConnectConfiguration>(settings.JwksUri,
                new ButlerSigningKeysRetriever(settings.Issuer),
                new HttpDocumentRetriever(options.Backchannel) { RequireHttps = !settings.AllowHttpMetadata });
        });
        services.AddAuthorization(options => options.AddPolicy(Policy, policy => policy
            .AddAuthenticationSchemes(Scheme).RequireAuthenticatedUser()
            .RequireAssertion(context => ButlerIdentity.IsDelegatedUser(context.User, settings.AllowedClientId))));
        return services;
    }
}
