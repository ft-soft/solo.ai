using System.Net;
using System.Text;
using Solo.Ai.Client;

namespace Solo.Ai.Tests;

internal sealed class SoloAiHttpFixture(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> respond)
    : HttpMessageHandler, IHttpClientFactory
{
    internal static readonly Guid ChatId = Guid.Parse("10000000-0000-0000-0000-000000000001");
    internal static readonly Guid MessageId = Guid.Parse("10000000-0000-0000-0000-000000000002");
    internal static readonly Guid RunId = Guid.Parse("10000000-0000-0000-0000-000000000003");
    internal static readonly Guid RetryId = Guid.Parse("10000000-0000-0000-0000-000000000004");
    public int Calls { get; private set; }
    public bool EchoHeaders { get; init; } = true;

    public HttpClient CreateClient(string name) => new(this, disposeHandler: false);
    public SoloAiClient CreateSdk(long requestLimit = 100_000, long responseLimit = 100_000, TimeSpan? timeout = null) => new(this, new()
    {
        Enabled = true, BaseUri = new("https://solo-ai.test/"), MaxRequestBytes = requestLimit,
        MaxResponseBytes = responseLimit, RequestTimeout = timeout ?? TimeSpan.FromSeconds(5), TotalDeadline = TimeSpan.FromSeconds(5),
    });

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        Calls++;
        var response = await respond(request, cancellationToken);
        if (EchoHeaders)
        {
            response.Headers.TryAddWithoutValidation(SoloAiProtocol.VersionHeader, "2");
            response.Headers.TryAddWithoutValidation(SoloAiProtocol.RequestIdHeader, request.Headers.GetValues(SoloAiProtocol.RequestIdHeader));
        }
        return response;
    }

    internal static string ReadFixture(string name) => File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", "SoloAgent", name));
    internal static HttpResponseMessage Reply(string? fixture, int status = 200) => new((HttpStatusCode)status)
    {
        Content = new StringContent(fixture is null ? "" : ReadFixture(fixture), Encoding.UTF8, "application/json"),
    };
}
