namespace Solo.Ai.Api;

/// <summary>Local cancellation and admission gate. Accepted work lives exclusively in SQLite.</summary>
public sealed class SoloAgentRunRuntime
{
    // ponytail: one process serializes short admission/dispatch gates; multiple replicas require a DB lease.
    internal object Gate { get; } = new();
    private readonly Dictionary<Guid, (Guid ChatId, CancellationTokenSource Cancellation)> active = [];
    private bool ready;
    public bool IsReady { get { lock (Gate) return ready; } }

    internal void SetReady(bool value) { lock (Gate) ready = value; }

    internal void Register(Guid runId, Guid chatId, CancellationTokenSource cancellation)
    {
        lock (Gate) active.Add(runId, (chatId, cancellation));
    }

    internal void Unregister(Guid runId)
    {
        lock (Gate) active.Remove(runId);
    }

    internal void CancelRun(Guid runId)
    {
        lock (Gate)
            if (active.TryGetValue(runId, out var run)) run.Cancellation.Cancel();
    }

    internal void CancelChat(Guid chatId)
    {
        lock (Gate)
            foreach (var run in active.Values.Where(run => run.ChatId == chatId).ToArray()) run.Cancellation.Cancel();
    }
}
