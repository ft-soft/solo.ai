using System.Net;

namespace Solo.Ai.Client;

public sealed class SoloAiExchangeException(string code, bool outcomeUnknown = false, HttpStatusCode? statusCode = null) : Exception(code)
{
    public string Code { get; } = code;
    public bool OutcomeUnknown { get; } = outcomeUnknown;
    public HttpStatusCode? StatusCode { get; } = statusCode;
}
