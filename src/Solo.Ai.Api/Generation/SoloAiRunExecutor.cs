using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;
using Solo.Ai.Client;
using Solo.Ai.Storage;
using Solo.Ai.Visograph;

namespace Solo.Ai.Api;

/// <summary>One dispatch, followed by bounded retries of the database write only.</summary>
public sealed class SoloAiRunExecutor(SqliteGenerationQueue queue, SqliteRunTransitions transitions,
    ISoloAgentModelPipeline pipeline, SoloAgentModelOptions modelOptions, SoloAiGenerationOptions options,
    SoloAgentRunRuntime runtime, ILogger<SoloAiRunExecutor> logger)
{
    public async Task ExecuteAsync(StoredGenerationRun work, CancellationTokenSource local, CancellationToken stopping)
    {
        using (local)
        {
            try
            {
                var state = "completed";
                string? code = null;
                string? text = null;
                using var deadline = CancellationTokenSource.CreateLinkedTokenSource(local.Token, stopping);
                var remaining = work.Run.DeadlineAt - DateTimeOffset.UtcNow;
                if (remaining <= TimeSpan.Zero) deadline.Cancel();
                else deadline.CancelAfter(remaining);
                try
                {
                    deadline.Token.ThrowIfCancellationRequested();
                    var history = queue.ReadHistory(work, modelOptions.MaxRequestBytes);
                    deadline.Token.ThrowIfCancellationRequested();
                    text = await pipeline.GenerateAsync(work.Run.RunId, history, deadline.Token).WaitAsync(deadline.Token);
                    deadline.Token.ThrowIfCancellationRequested();
                }
                catch (OperationCanceledException)
                {
                    // Persisted user cancellation wins in FinishRun; host shutdown is interruption.
                    (state, code) = stopping.IsCancellationRequested ? ("interrupted", "interrupted") :
                        local.IsCancellationRequested ? ("cancelled", "cancelled") : ("timed_out", "timed_out");
                }
                catch (ModelExchangeException error) { (state, code) = MapFailure(error.Code); }
                catch (SoloAiExchangeException error) when (error.Code == "not_found") { return; }
                catch (Exception error) when (error is SqliteException or IOException)
                {
                    runtime.SetReady(false);
                    logger.LogError("Generation storage failed for run {RunId}.", work.Run.RunId);
                    return;
                }
                catch (Exception) { (state, code) = ("failed", "provider_error"); }
                await FinalizeAsync(work, state, code, state == "completed" ? text : null);
            }
            catch (Exception)
            {
                runtime.SetReady(false);
                logger.LogError("Generation processing failed for run {RunId}.", work.Run.RunId);
            }
            finally { runtime.Unregister(work.Run.RunId); }
        }
    }

    private async Task FinalizeAsync(StoredGenerationRun work, string state, string? code, string? text)
    {
        for (var attempt = 1; attempt <= options.FinalizationAttempts; attempt++)
        {
            try
            {
                var result = transitions.FinishRun(work.Owner, work.Run.ChatId, work.Run.RunId, state, code, text);
                if (result.State == "cancellation_requested")
                    result = transitions.FinishRun(work.Owner, work.Run.ChatId, work.Run.RunId, "cancelled", "cancelled");
                logger.LogInformation("Generation run {RunId} finished with {State} and {Code}.", result.RunId, result.State, result.OutcomeCode);
                return;
            }
            catch (SoloAiExchangeException error) when (error.Code == "not_found") { return; }
            catch (Exception error) when (error is SqliteException or IOException)
            {
                logger.LogWarning("Generation finalization failed for run {RunId}, attempt {Attempt}.", work.Run.RunId, attempt);
                if (attempt < options.FinalizationAttempts) await Task.Delay(TimeSpan.FromMilliseconds(100));
            }
        }
        runtime.SetReady(false);
        logger.LogError("Generation admission and dispatch stopped: finalization exhausted for run {RunId}.", work.Run.RunId);
    }

    private static (string State, string Code) MapFailure(string code) => code switch
    {
        "timeout" => ("timed_out", "timed_out"),
        "incomplete_response" => ("incomplete", "incomplete_response"),
        "authentication_failed" or "authorization_failed" => ("failed", "dependency_authentication_failed"),
        "project_unavailable" => ("failed", "dependency_unavailable"),
        "unsupported_contract" or "configuration_failure" or "validation_failed" => ("failed", "configuration_failure"),
        "limit_exceeded" or "malformed_model_response" => ("failed", code),
        _ => ("failed", "provider_error"),
    };
}
