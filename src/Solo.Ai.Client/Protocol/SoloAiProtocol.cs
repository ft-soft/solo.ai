using System.Text.Json;

namespace Solo.Ai.Client;

public static class SoloAiProtocol
{
    public const int Version = 2;
    public const string Endpoint = "api/v2/chats";
    public const string VersionHeader = "X-Solo-Ai-Contract-Version";
    public const string RequestIdHeader = "X-Solo-Ai-Request-Id";
    public const int DefaultPageSize = 50;
    public const int MaximumPageSize = 200;
    public const int MaximumTitleBytes = 512;
    public const int MaximumTextBytes = 65_536;
    public static JsonSerializerOptions JsonOptions { get; } = CreateJsonOptions();

    public static T Deserialize<T>(ReadOnlySpan<byte> bytes) where T : class =>
        JsonSerializer.Deserialize<T>(bytes, JsonOptions) ?? throw new JsonException();

    public static byte[] SerializeBounded<T>(T value, long maximumBytes)
    {
        if (maximumBytes is <= 0 or > int.MaxValue)
            throw new SoloAiExchangeException("configuration_failure");
        using var body = new BoundedSerializationStream(maximumBytes);
        JsonSerializer.Serialize(body, value, JsonOptions);
        return body.ToArray();
    }

    public static async Task<byte[]> ReadBoundedAsync(Stream stream, long maximumBytes, CancellationToken cancellationToken)
    {
        if (maximumBytes is <= 0 or > int.MaxValue)
            throw new SoloAiExchangeException("configuration_failure");
        using var body = new MemoryStream();
        var buffer = new byte[8192];
        int count;
        while ((count = await stream.ReadAsync(buffer, cancellationToken)) != 0)
        {
            if (body.Length + count > maximumBytes)
                throw new SoloAiExchangeException("limit_exceeded");
            await body.WriteAsync(buffer.AsMemory(0, count), cancellationToken);
        }
        return body.ToArray();
    }

    private static JsonSerializerOptions CreateJsonOptions()
    {
        var options = new JsonSerializerOptions(JsonSerializerDefaults.Web)
        {
            PropertyNameCaseInsensitive = false, AllowDuplicateProperties = false, RespectRequiredConstructorParameters = true,
            RespectNullableAnnotations = true,
            UnmappedMemberHandling = System.Text.Json.Serialization.JsonUnmappedMemberHandling.Disallow,
        };
        options.MakeReadOnly(populateMissingResolver: true);
        return options;
    }

    private sealed class BoundedSerializationStream(long maximumBytes) : MemoryStream
    {
        public override void Write(ReadOnlySpan<byte> buffer) { CheckLimit(buffer.Length); base.Write(buffer); }
        public override void Write(byte[] buffer, int offset, int count) { CheckLimit(count); base.Write(buffer, offset, count); }
        private void CheckLimit(int count)
        {
            if (Position + count > maximumBytes)
                throw new SoloAiExchangeException("limit_exceeded");
        }
    }
}
