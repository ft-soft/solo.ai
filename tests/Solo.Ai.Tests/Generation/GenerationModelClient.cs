using System.Collections.Concurrent;
using System.Threading.Channels;
using Microsoft.AspNetCore.Http;
using Solo.Ai.Visograph;

namespace Solo.Ai.Tests;

internal sealed class GenerationModelClient : IModelGenerationClient
{
    private readonly Channel<ModelGenerationRequest> requests = Channel.CreateUnbounded<ModelGenerationRequest>();
    private readonly ConcurrentDictionary<Guid, TaskCompletionSource<ModelGenerationResponse>> replies = new();
    public ConcurrentQueue<ModelGenerationRequest> Calls { get; } = new();
    public ConcurrentQueue<bool> CapturedHttpContext { get; } = new();
    public bool IgnoreCancellation { get; init; }

    public Task<ModelGenerationResponse> GenerateAsync(ModelGenerationRequest request, CancellationToken cancellationToken)
    {
        CapturedHttpContext.Enqueue(new HttpContextAccessor().HttpContext is not null);
        var reply = new TaskCompletionSource<ModelGenerationResponse>(TaskCreationOptions.RunContinuationsAsynchronously);
        if (!replies.TryAdd(request.RequestId, reply)) throw new InvalidOperationException("Repeated dispatch.");
        Calls.Enqueue(request);
        requests.Writer.TryWrite(request);
        return IgnoreCancellation ? reply.Task : reply.Task.WaitAsync(cancellationToken);
    }

    public async Task<ModelGenerationRequest> NextAsync() =>
        await requests.Reader.ReadAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(10));

    public void Complete(Guid runId, string content = "{\"text\":\"answer\"}") =>
        replies[runId].TrySetResult(new(1, runId, "completed") { Content = content });

    public void Fail(Guid runId, string outcome) =>
        replies[runId].TrySetResult(new(1, runId, outcome) { Stage = "provider" });
}
