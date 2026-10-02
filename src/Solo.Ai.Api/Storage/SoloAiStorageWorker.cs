using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Solo.Ai.Storage;

namespace Solo.Ai.Api;

public sealed class SoloAiStorageWorker(SqliteChatDatabase database, SqliteChatRetention retention,
    SoloAgentRunRuntime runtime, ILogger<SoloAiStorageWorker> logger) : BackgroundService
{
    private FileStream? hostLock;

    public override Task StartAsync(CancellationToken cancellationToken)
    {
        hostLock = database.AcquireHostLock();
        try
        {
            database.Initialize();
            database.CheckHealth();
            retention.DeleteExpiredChats(DateTimeOffset.UtcNow);
        }
        catch
        {
            hostLock.Dispose();
            hostLock = null;
            throw;
        }
        return base.StartAsync(cancellationToken);
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            using var timer = new PeriodicTimer(TimeSpan.FromMinutes(1));
            while (await timer.WaitForNextTickAsync(stoppingToken))
            {
                var deleted = retention.DeleteExpiredChats(DateTimeOffset.UtcNow);
                if (deleted > 0) logger.LogInformation("Retention removed {Count} chats.", deleted);
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { }
        catch (Exception)
        {
            runtime.SetReady(false);
            logger.LogError("Retention stopped with storage_failure; restart is required.");
        }
    }

    public override void Dispose()
    {
        base.Dispose();
        hostLock?.Dispose();
    }
}
