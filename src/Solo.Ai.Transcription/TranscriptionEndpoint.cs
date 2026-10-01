using System.ComponentModel;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Solo.Ai.Transcription;

public sealed class TranscriptionEndpoint(
    IOptions<TranscriptionOptions> options, AudioDecoder decoder,
    GigaAmTranscriber transcriber, ILogger<TranscriptionEndpoint> logger) : IDisposable
{
    // ponytail: one request per process, no queue; introduce a worker pool only after measuring demand.
    private readonly SemaphoreSlim gate = new(1, 1);

    public async Task<IResult> TranscribeAsync(HttpContext context)
    {
        if (!options.Value.Enabled)
            return Error("transcription_disabled", StatusCodes.Status503ServiceUnavailable);
        var format = context.Request.ContentType?.Split(';')[0].Trim().ToLowerInvariant() switch
        {
            "audio/webm" => "matroska",
            "audio/mp4" => "mov",
            "audio/ogg" => "ogg",
            "audio/wav" or "audio/x-wav" => "wav",
            _ => null
        };
        if (format is null)
            return Error("unsupported_audio_type", StatusCodes.Status415UnsupportedMediaType);
        if (context.Request.ContentLength > TranscriptionOptions.MaxUploadBytes)
            return Error("audio_too_large", StatusCodes.Status413PayloadTooLarge);
        if (!await gate.WaitAsync(0, context.RequestAborted))
        {
            context.Response.Headers.RetryAfter = "1";
            return Error("transcription_busy", StatusCodes.Status429TooManyRequests);
        }
        string? path = null;
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(context.RequestAborted);
        deadline.CancelAfter(TimeSpan.FromSeconds(180));
        try
        {
            path = Path.GetTempFileName();
            await using (var file = new FileStream(path, FileMode.Truncate, FileAccess.Write, FileShare.None, 8192, true))
            {
                var buffer = new byte[8192];
                var total = 0;
                int count;
                while ((count = await context.Request.Body.ReadAsync(buffer, deadline.Token)) > 0)
                {
                    total += count;
                    if (total > TranscriptionOptions.MaxUploadBytes)
                        return Error("audio_too_large", StatusCodes.Status413PayloadTooLarge);
                    await file.WriteAsync(buffer.AsMemory(0, count), deadline.Token);
                }
                if (total == 0) return Error("empty_audio", StatusCodes.Status400BadRequest);
            }
            var samples = await decoder.DecodeAsync(path, format, deadline.Token);
            deadline.Token.ThrowIfCancellationRequested();
            // Native decoding cannot be interrupted. Retain the gate until it actually completes.
            var text = await Task.Run(() => transcriber.Transcribe(samples, deadline.Token), deadline.Token);
            deadline.Token.ThrowIfCancellationRequested();
            return Results.Ok(new TranscriptionReply(text));
        }
        catch (BadHttpRequestException error) { return Error(error.Message, error.StatusCode); }
        catch (OperationCanceledException) when (!context.RequestAborted.IsCancellationRequested)
        { return Error("transcription_timeout", StatusCodes.Status504GatewayTimeout); }
        catch (OperationCanceledException) { throw; }
        catch (Exception error) when (error is InvalidOperationException or Win32Exception or DllNotFoundException)
        {
            logger.LogError(error, "Transcription runtime is unavailable");
            return Error("transcription_unavailable", StatusCodes.Status503ServiceUnavailable);
        }
        catch (Exception error)
        {
            logger.LogError(error, "Transcription failed");
            return Error("transcription_failed", StatusCodes.Status500InternalServerError);
        }
        finally
        {
            try { if (path is not null) File.Delete(path); }
            finally { gate.Release(); }
        }
    }

    private static IResult Error(string code, int status) => Results.Json(new { code }, statusCode: status);
    public void Dispose() => gate.Dispose();
}
