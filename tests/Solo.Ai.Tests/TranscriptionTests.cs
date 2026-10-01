using System.Diagnostics;
using System.IO.Pipelines;
using System.Net;
using System.Net.Http.Headers;
using System.Security.Claims;
using System.Text.Encodings.Web;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Solo.Ai.Transcription;
using Xunit;

namespace Solo.Ai.Tests;

public sealed class TranscriptionTests
{
    [Theory]
    [InlineData(false, false, "audio/wav", 1, HttpStatusCode.Unauthorized)]
    [InlineData(true, false, "audio/wav", 1, HttpStatusCode.ServiceUnavailable)]
    [InlineData(true, true, "text/plain", 1, HttpStatusCode.UnsupportedMediaType)]
    [InlineData(true, true, "audio/wav", 0, HttpStatusCode.BadRequest)]
    [InlineData(true, true, "audio/wav", TranscriptionOptions.MaxUploadBytes + 1, HttpStatusCode.RequestEntityTooLarge)]
    public async Task RejectBeforeLoadingModel(bool authenticated, bool enabled, string type, int size, HttpStatusCode expected)
    {
        using var host = await CreateHostAsync(enabled);
        using var client = host.GetTestClient();
        if (authenticated) client.DefaultRequestHeaders.Add("X-Test-User", "yes");
        using var content = new ByteArrayContent(new byte[size]);
        content.Headers.ContentType = new MediaTypeHeaderValue(type);
        using var response = await client.PostAsync("/api/transcription", content, TestContext.Current.CancellationToken);
        Assert.Equal(expected, response.StatusCode);
    }

    [Fact]
    public async Task BoundChunkedUploadsAndReleaseGateAfterCancellation()
    {
        var settings = Options.Create(new TranscriptionOptions { Enabled = true });
        using var transcriber = new GigaAmTranscriber(settings);
        using var endpoint = new TranscriptionEndpoint(settings, new AudioDecoder(settings), transcriber,
            NullLogger<TranscriptionEndpoint>.Instance);
        var oversized = new DefaultHttpContext();
        oversized.Request.ContentType = "audio/webm";
        oversized.Request.Body = new MemoryStream(new byte[TranscriptionOptions.MaxUploadBytes + 1]);
        Assert.Equal(413, ((IStatusCodeHttpResult)await endpoint.TranscribeAsync(oversized)).StatusCode);

        var pipe = new Pipe();
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        var waiting = new DefaultHttpContext { RequestAborted = cancellation.Token };
        waiting.Request.ContentType = "audio/webm";
        waiting.Request.Body = pipe.Reader.AsStream();
        var first = endpoint.TranscribeAsync(waiting);
        var next = new DefaultHttpContext();
        next.Request.ContentType = "audio/webm";
        Assert.Equal(429, ((IStatusCodeHttpResult)await endpoint.TranscribeAsync(next)).StatusCode);
        await cancellation.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => first);
        await pipe.Writer.CompleteAsync();
        await pipe.Reader.CompleteAsync();
        Assert.Equal(400, ((IStatusCodeHttpResult)await endpoint.TranscribeAsync(next)).StatusCode);
    }

    public static bool CanRunModel => Directory.Exists(Environment.GetEnvironmentVariable("SOLO_TRANSCRIPTION_MODEL_DIR"))
        && File.Exists(Environment.GetEnvironmentVariable("SOLO_TRANSCRIPTION_FFMPEG"));

    [Fact(SkipUnless = nameof(CanRunModel), Skip = "Set SOLO_TRANSCRIPTION_MODEL_DIR and SOLO_TRANSCRIPTION_FFMPEG for real inference.")]
    public async Task TranscribeRealAudioAndRejectBrokenAndOverlongAudio()
    {
        var directory = Environment.GetEnvironmentVariable("SOLO_TRANSCRIPTION_MODEL_DIR")!;
        var settings = new TranscriptionOptions
        {
            Enabled = true,
            ModelPath = Path.Combine(directory, "model.int8.onnx"),
            TokensPath = Path.Combine(directory, "tokens.txt"),
            VadModelPath = Path.Combine(directory, "silero_vad.onnx"),
            FfmpegPath = Environment.GetEnvironmentVariable("SOLO_TRANSCRIPTION_FFMPEG")!
        };
        using var host = await CreateHostAsync(true, settings);
        using var client = host.GetTestClient();
        client.DefaultRequestHeaders.Add("X-Test-User", "yes");
        var audioPath = Directory.GetFiles(Path.Combine(directory, "test_wavs"), "*.wav")[0];
        foreach (var format in new[] { "wav", "webm", "mp4", "ogg" })
        {
            var bytes = format == "wav"
                ? await File.ReadAllBytesAsync(audioPath, TestContext.Current.CancellationToken)
                : await EncodeAsync(settings.FfmpegPath, audioPath, format);
            using var audio = new ByteArrayContent(bytes);
            audio.Headers.ContentType = new MediaTypeHeaderValue($"audio/{format}");
            using var response = await client.PostAsync("/api/transcription", audio, TestContext.Current.CancellationToken);
            var json = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
            Assert.True(response.IsSuccessStatusCode, json);
            using var document = System.Text.Json.JsonDocument.Parse(json);
            var text = document.RootElement.GetProperty("text").GetString();
            Assert.Matches("[А-Яа-я]", text!);
            TestContext.Current.TestOutputHelper!.WriteLine($"{format}: {text}");
        }

        var decoder = new AudioDecoder(Options.Create(settings));
        var sample = await decoder.DecodeAsync(audioPath, "wav", TestContext.Current.CancellationToken);
        var longAudio = new float[120 * 16000];
        foreach (var offset in new[] { 0, 50 * 16000, longAudio.Length - sample.Length })
            sample.CopyTo(longAudio, offset);
        using (var audio = new ByteArrayContent(CreateWave(longAudio)))
        {
            audio.Headers.ContentType = new MediaTypeHeaderValue("audio/wav");
            using var response = await client.PostAsync("/api/transcription", audio, TestContext.Current.CancellationToken);
            var json = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
            Assert.True(response.IsSuccessStatusCode, json);
            using var document = System.Text.Json.JsonDocument.Parse(json);
            var text = document.RootElement.GetProperty("text").GetString()!;
            // Speech at the beginning, middle and very end must all survive segmentation.
            Assert.Equal(3, System.Text.RegularExpressions.Regex.Matches(text, "Ничьих", System.Text.RegularExpressions.RegexOptions.IgnoreCase).Count);
            TestContext.Current.TestOutputHelper!.WriteLine($"120 seconds: {text}");
        }
        using (var silence = new ByteArrayContent(CreateWave(120)))
        {
            silence.Headers.ContentType = new MediaTypeHeaderValue("audio/wav");
            using var response = await client.PostAsync("/api/transcription", silence, TestContext.Current.CancellationToken);
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            Assert.Equal("{\"text\":\"\"}", await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
        }

        foreach (var bytes in new[] { new byte[] { 1, 2, 3 }, CreateWave(121) })
        {
            using var content = new ByteArrayContent(bytes);
            content.Headers.ContentType = new MediaTypeHeaderValue("audio/wav");
            using var error = await client.PostAsync("/api/transcription", content, TestContext.Current.CancellationToken);
            Assert.Equal(bytes.Length == 3 ? HttpStatusCode.BadRequest : HttpStatusCode.RequestEntityTooLarge, error.StatusCode);
        }
    }

    private static async Task<byte[]> EncodeAsync(string ffmpeg, string input, string format)
    {
        var output = Path.Combine(Path.GetTempPath(), $"solo-transcription-test-{Guid.NewGuid():N}.{format}");
        using var process = new Process { StartInfo = new ProcessStartInfo(ffmpeg) { UseShellExecute = false } };
        foreach (var argument in new[] { "-nostdin", "-loglevel", "error", "-i", input, output })
            process.StartInfo.ArgumentList.Add(argument);
        try
        {
            process.Start();
            await process.WaitForExitAsync(TestContext.Current.CancellationToken);
            Assert.Equal(0, process.ExitCode);
            return await File.ReadAllBytesAsync(output, TestContext.Current.CancellationToken);
        }
        finally
        {
            if (!process.HasExited) { process.Kill(true); await process.WaitForExitAsync(CancellationToken.None); }
            File.Delete(output);
        }
    }

    private static byte[] CreateWave(int seconds)
    {
        using var data = new MemoryStream();
        using var writer = new BinaryWriter(data);
        var size = seconds * 16000 * 2;
        writer.Write("RIFF"u8); writer.Write(size + 36); writer.Write("WAVEfmt "u8);
        writer.Write(16); writer.Write((short)1); writer.Write((short)1);
        writer.Write(16000); writer.Write(32000); writer.Write((short)2); writer.Write((short)16);
        writer.Write("data"u8); writer.Write(size); writer.Write(new byte[size]);
        return data.ToArray();
    }

    private static byte[] CreateWave(float[] samples)
    {
        using var data = new MemoryStream();
        using var writer = new BinaryWriter(data);
        var size = samples.Length * sizeof(float);
        writer.Write("RIFF"u8); writer.Write(size + 36); writer.Write("WAVEfmt "u8);
        writer.Write(16); writer.Write((short)3); writer.Write((short)1);
        writer.Write(16000); writer.Write(64000); writer.Write((short)4); writer.Write((short)32);
        writer.Write("data"u8); writer.Write(size);
        writer.Write(System.Runtime.InteropServices.MemoryMarshal.AsBytes(samples.AsSpan()));
        return data.ToArray();
    }

    private static Task<IHost> CreateHostAsync(bool enabled, TranscriptionOptions? settings = null) =>
        new HostBuilder().ConfigureWebHost(web => web.UseTestServer()
            .ConfigureServices(services =>
            {
                services.AddRouting();
                services.AddAuthentication("test").AddScheme<AuthenticationSchemeOptions, TestAuthentication>("test", _ => { });
                services.AddAuthorization(options => options.AddPolicy("backend", policy => policy.RequireAuthenticatedUser()));
                services.AddTranscription(new ConfigurationBuilder().Build());
                // Guard tests deliberately enable without model files: rejected requests must never load a model.
                services.AddSingleton<IOptions<TranscriptionOptions>>(Options.Create(settings ?? new TranscriptionOptions { Enabled = enabled }));
            })
            .Configure(app => app.UseRouting().UseAuthentication().UseAuthorization()
                .UseEndpoints(endpoints => endpoints.MapTranscription("backend"))))
            .StartAsync(TestContext.Current.CancellationToken);

    private sealed class TestAuthentication(IOptionsMonitor<AuthenticationSchemeOptions> options,
        ILoggerFactory logger, UrlEncoder encoder) : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
    {
        protected override Task<AuthenticateResult> HandleAuthenticateAsync() => Task.FromResult(
            Request.Headers.ContainsKey("X-Test-User")
                ? AuthenticateResult.Success(new AuthenticationTicket(new ClaimsPrincipal(new ClaimsIdentity(
                    [new Claim(ClaimTypes.NameIdentifier, "test")], Scheme.Name)), Scheme.Name))
                : AuthenticateResult.NoResult());
    }
}
