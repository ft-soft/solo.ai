namespace Solo.Ai.Client;

public sealed class SoloAiExchangeException(string code) : Exception(code)
{
    public string Code { get; } = code;
}
