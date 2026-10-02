using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;
using Solo.Ai.Api;
using Solo.Ai.Client;
using Solo.Ai.Visograph;
using Microsoft.AspNetCore.Http;
using Xunit;

namespace Solo.Ai.Tests;

internal sealed class ButlerApiFixture : IDisposable
{
    public const string Issuer = "https://butler.test";
    public const string Audience = "solo-ai.api";
    public const string Actor = "solo-ai-delegation";
    public const string Alice = "00000000-0000-0000-0000-000000000001";
    public const string Bob = "00000000-0000-0000-0000-000000000002";
    private readonly RSA rsa = RSA.Create(2048);
    private readonly HttpClient backchannel;
    private IHost? host;
    public SqliteStorageFixture Storage { get; } = new();
    public RsaSecurityKey Key { get; }
    public HttpClient Client { get; private set; } = null!;
    public int JwksRequests { get; private set; }
    public bool JwksAvailable { get; set; } = true;
    public SoloAgentRunRuntime Runtime => host!.Services.GetRequiredService<SoloAgentRunRuntime>();
    public GenerationLog Logs { get; } = new();

    private ButlerApiFixture()
    {
        Key = new RsaSecurityKey(rsa) { KeyId = Guid.NewGuid().ToString("N") };
        backchannel = new(new JwksHandler(this));
    }

    public static async Task<ButlerApiFixture> StartAsync(Dictionary<string, string?>? changes = null,
        string environment = "Production", bool mockBackchannel = true, IModelGenerationClient? modelClient = null,
        Func<HttpContext, Task>? beforeResponse = null, Action<SqliteStorageFixture>? prepareStorage = null)
    {
        var fixture = new ButlerApiFixture();
        var config = new Dictionary<string, string?>
        {
            ["SoloAiAuthentication:Issuer"] = Issuer, ["SoloAiAuthentication:Audience"] = Audience,
            ["SoloAiAuthentication:AllowedClientId"] = Actor, ["SoloAiAuthentication:JwksUri"] = Issuer + "/keys",
            ["SoloAiStorage:Enabled"] = "true", ["SoloAiStorage:DatabasePath"] = fixture.Storage.DatabasePath,
        };
        if (modelClient is not null)
            foreach (var pair in GenerationFixture.ModelConfiguration()) config[pair.Key] = pair.Value;
        foreach (var pair in changes ?? []) config[pair.Key] = pair.Value;
        try
        {
            prepareStorage?.Invoke(fixture.Storage);
            fixture.host = new HostBuilder().UseEnvironment(environment)
                .ConfigureAppConfiguration(builder => builder.AddInMemoryCollection(config))
                .ConfigureWebHost(web => web.UseTestServer().ConfigureServices((context, services) =>
                {
                    services.AddRouting();
                    services.AddLogging(logging => logging.AddProvider(fixture.Logs).AddSoloAiSafeLogging());
                    services.AddHttpContextAccessor();
                    if (modelClient is not null) services.AddSingleton(modelClient);
                    services.AddSoloAiStorage(context.Configuration);
                    services.AddSoloAiApi(context.Configuration);
                    services.AddSoloAiAuthentication(context.Configuration, context.HostingEnvironment);
                    if (mockBackchannel)
                        services.Configure<JwtBearerOptions>(ButlerAuthenticationExtensions.Scheme,
                            options => options.Backchannel = fixture.backchannel);
                }).Configure(app =>
                {
                    app.UseMiddleware<SoloAiExceptionMiddleware>();
                    if (beforeResponse is not null)
                        app.Use(async (context, next) =>
                        {
                            context.Response.OnStarting(() => beforeResponse(context));
                            await next(context);
                        });
                    app.UseRouting();
                    app.UseAuthentication();
                    app.UseAuthorization();
                    app.UseMiddleware<SoloAiRequestMiddleware>();
                    app.UseEndpoints(endpoints =>
                    {
                        endpoints.MapSoloAiChatEndpoints();
                        SoloAiHealth.MapEndpoints(endpoints);
                    });
                })).Build();
            await fixture.host.StartAsync(TestContext.Current.CancellationToken);
            fixture.Client = fixture.host.GetTestClient();
            return fixture;
        }
        catch { fixture.Dispose(); throw; }
    }

    public string CreateToken(string owner = Alice, Action<SecurityTokenDescriptor>? change = null)
    {
        var token = new SecurityTokenDescriptor
        {
            Issuer = Issuer, Audience = Audience, TokenType = "at+jwt",
            IssuedAt = DateTime.UtcNow.AddSeconds(-1), NotBefore = DateTime.UtcNow.AddSeconds(-1), Expires = DateTime.UtcNow.AddMinutes(1),
            SigningCredentials = new(Key, SecurityAlgorithms.RsaSha256),
            Claims = new Dictionary<string, object>
            {
                ["sub"] = owner, ["user_id"] = owner, ["account_type"] = "User", ["client_id"] = Actor,
                ["act"] = new Dictionary<string, object> { ["sub"] = Actor },
            },
        };
        change?.Invoke(token);
        return new JsonWebTokenHandler { SetDefaultTimesOnTokenCreation = false }.CreateToken(token);
    }

    public Task<HttpResponseMessage> SendAsync(string method, string path, object? body = null,
        string? token = null, Action<HttpRequestMessage>? change = null, CancellationToken? cancellationToken = null)
    {
        var request = new HttpRequestMessage(new HttpMethod(method), path);
        request.Headers.Add(SoloAiProtocol.VersionHeader, "2");
        request.Headers.Add(SoloAiProtocol.RequestIdHeader, Guid.NewGuid().ToString("D"));
        if (token != "") request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token ?? CreateToken());
        if (body is not null) request.Content = new StringContent(body is string text ? text : JsonSerializer.Serialize(body, SoloAiProtocol.JsonOptions), Encoding.UTF8, "application/json");
        change?.Invoke(request);
        return Client.SendAsync(request, cancellationToken ?? TestContext.Current.CancellationToken);
    }

    public static async Task<T> ReadDataAsync<T>(HttpResponseMessage response) where T : class =>
        SoloAiProtocol.Deserialize<SoloAiResponse<T>>(await response.Content.ReadAsByteArrayAsync(TestContext.Current.CancellationToken)).Data!;

    public void Dispose()
    {
        Client?.Dispose();
        host?.StopAsync().GetAwaiter().GetResult();
        host?.Dispose();
        backchannel.Dispose();
        rsa.Dispose();
        Storage.Dispose();
    }

    private sealed class JwksHandler(ButlerApiFixture fixture) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Assert.Equal(Issuer + "/keys", request.RequestUri!.AbsoluteUri);
            fixture.JwksRequests++;
            if (!fixture.JwksAvailable) return Task.FromResult(new HttpResponseMessage(HttpStatusCode.ServiceUnavailable));
            var key = JsonWebKeyConverter.ConvertFromRSASecurityKey(new RsaSecurityKey(fixture.rsa.ExportParameters(false)) { KeyId = fixture.Key.KeyId });
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(JsonSerializer.Serialize(new { keys = new[] { key } }), Encoding.UTF8, "application/json"),
            });
        }
    }
}
