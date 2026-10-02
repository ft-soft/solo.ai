using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Solo.Ai.Storage;

namespace Solo.Ai.Api;

public sealed class SoloAiGenerationWorker(SqliteChatDatabase database, SqliteGenerationQueue queue,
    SqliteRunTransitions transitions, SoloAiRunExecutor executor, SoloAiGenerationOptions options,
    SoloAgentRunRuntime runtime, IHostApplicationLifetime lifetime, ILogger<SoloAiGenerationWorker> logger)
    : BackgroundService
{
    private readonly CancellationTokenSource shutdown = new();
    private CancellationTokenRegistration stoppingRegistration;

    public override Task StartAsync(CancellationToken cancellationToken)
    {
        // StartAsync completes recovery before the host can serve new requests (.NET 10 ExecuteAsync is asynchronous).
        database.CheckHealth();
        queue.Recover();
        stoppingRegistration = lifetime.ApplicationStopping.Register(StopProcessing);
        runtime.SetReady(!shutdown.IsCancellationRequested);
        return base.StartAsync(cancellationToken);
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken, shutdown.Token);
        var tasks = new List<Task>();
        try
        {
            while (!stop.IsCancellationRequested)
            {
                tasks.RemoveAll(task => task.IsCompleted);
                lock (runtime.Gate)
                {
                    if (runtime.IsReady && !stop.IsCancellationRequested)
                    {
                        queue.SettleWaitingRuns();
                        foreach (var work in queue.GetPending(options.MaximumParallelRuns - tasks.Count))
                        {
                            if (!runtime.IsReady || stop.IsCancellationRequested) break;
                            if (!transitions.TryStartRun(work.Owner, work.Run.ChatId, work.Run.RunId)) continue;
                            var local = new CancellationTokenSource();
                            runtime.Register(work.Run.RunId, work.Run.ChatId, local);
                            // Executor yields before any model work; never hold this gate across a provider call.
                            tasks.Add(ExecuteLaterAsync(work, local, stop.Token));
                        }
                    }
                }
                await Task.Delay(TimeSpan.FromMilliseconds(250), stop.Token);
            }
        }
        catch (OperationCanceledException) when (stop.IsCancellationRequested) { }
        catch (Exception)
        {
            runtime.SetReady(false);
            logger.LogError("Generation scheduler stopped after a storage failure; restart is required.");
        }
        finally
        {
            runtime.SetReady(false);
            await Task.WhenAll(tasks);
        }
    }

    private async Task ExecuteLaterAsync(StoredGenerationRun work, CancellationTokenSource local, CancellationToken stopping)
    {
        await Task.Yield();
        await executor.ExecuteAsync(work, local, stopping);
    }

    private void StopProcessing()
    {
        runtime.SetReady(false);
        shutdown.Cancel();
    }

    public override Task StopAsync(CancellationToken cancellationToken)
    {
        StopProcessing();
        return base.StopAsync(cancellationToken);
    }

    public override void Dispose()
    {
        stoppingRegistration.Dispose();
        shutdown.Cancel();
        base.Dispose();
        shutdown.Dispose();
    }
}
