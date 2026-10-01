using System.Diagnostics;
using System.Runtime.InteropServices;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Options;

namespace Solo.Ai.Transcription;

public sealed class AudioDecoder(IOptions<TranscriptionOptions> options)
{
    public async Task<float[]> DecodeAsync(string path, string format, CancellationToken ct)
    {
        using var process = new Process
        {
            StartInfo = new ProcessStartInfo(options.Value.FfmpegPath)
            {
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true
            }
        };
        // Explicit demuxer + protocol allowlist: do not autodetect uploaded playlists or allow network protocols.
        foreach (var argument in new[] { "-nostdin", "-hide_banner", "-loglevel", "quiet",
                     "-protocol_whitelist", "file,pipe", "-f", format, "-i", path,
                     "-map", "0:a:0", "-vn", "-ac", "1", "-ar", "16000",
                     "-t", (TranscriptionOptions.MaxAudioSeconds + 1).ToString(System.Globalization.CultureInfo.InvariantCulture), "-f", "f32le", "pipe:1" })
            process.StartInfo.ArgumentList.Add(argument);
        process.Start();
        using var cancellation = ct.Register(() => Kill(process));
        try
        {
            // quiet stderr is still drained so a subprocess cannot block on a full pipe.
            var drain = process.StandardError.BaseStream.CopyToAsync(Stream.Null, ct);
            using var pcm = new MemoryStream();
            var buffer = new byte[8192];
            int count;
            while ((count = await process.StandardOutput.BaseStream.ReadAsync(buffer, ct)) > 0)
            {
                if (pcm.Length + count > TranscriptionOptions.MaxAudioSeconds * 16000 * sizeof(float))
                    throw new BadHttpRequestException("audio_too_long", StatusCodes.Status413PayloadTooLarge);
                pcm.Write(buffer, 0, count);
            }
            await process.WaitForExitAsync(ct);
            await drain;
            ct.ThrowIfCancellationRequested();
            if (process.ExitCode != 0 || pcm.Length < 320 * sizeof(float) || pcm.Length % sizeof(float) != 0)
                throw new BadHttpRequestException("invalid_audio", StatusCodes.Status400BadRequest);
            var samples = MemoryMarshal.Cast<byte, float>(pcm.ToArray()).ToArray();
            if (samples.Any(sample => !float.IsFinite(sample)))
                throw new BadHttpRequestException("invalid_audio", StatusCodes.Status400BadRequest);
            return samples;
        }
        finally
        {
            Kill(process);
            await process.WaitForExitAsync(CancellationToken.None);
        }
    }

    private static void Kill(Process process)
    {
        try { if (!process.HasExited) process.Kill(entireProcessTree: true); }
        catch (InvalidOperationException) { }
    }
}
