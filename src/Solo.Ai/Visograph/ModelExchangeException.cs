namespace Solo.Ai.Visograph;

public sealed class ModelExchangeException(string code, string stage) : Exception(code)
{
    public string Code { get; } = code;
    public string Stage { get; } = stage;
}
