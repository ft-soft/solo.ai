using Microsoft.Extensions.Options;
using SherpaOnnx;

namespace Solo.Ai.Transcription;

// Access is serialized by TranscriptionEndpoint, including lazy model initialization.
public sealed class GigaAmTranscriber(IOptions<TranscriptionOptions> options) : IDisposable
{
    private OfflineRecognizer? recognizer;

    public string Transcribe(float[] samples, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        var segments = samples.Length <= 25 * 16000
            ? [samples]
            : SpeechSegmenter.Split(samples, options.Value.VadModelPath, ct);
        var text = new List<string>();
        foreach (var segment in segments)
        {
            ct.ThrowIfCancellationRequested();
            if (segment.Length == 0) continue;
            if (segment.Length > 25 * 16000)
                throw new InvalidOperationException("Speech segment exceeds the model input limit.");
            recognizer ??= CreateRecognizer();
            using var stream = recognizer.CreateStream();
            stream.AcceptWaveform(16000, segment);
            recognizer.Decode(stream);
            ct.ThrowIfCancellationRequested();
            var part = stream.Result.Text.Trim();
            if (part.Length > 0) text.Add(part);
        }
        return string.Join(" ", text);
    }

    private OfflineRecognizer CreateRecognizer()
    {
        var settings = options.Value;
        if (!File.Exists(settings.ModelPath) || !File.Exists(settings.TokensPath))
            throw new InvalidOperationException("GigaAM model and tokens must be installed before enabling transcription.");
        var config = new OfflineRecognizerConfig();
        config.FeatConfig.SampleRate = 16000;
        config.FeatConfig.FeatureDim = 64;
        config.ModelConfig.NeMoCtc.Model = settings.ModelPath;
        config.ModelConfig.Tokens = settings.TokensPath;
        config.ModelConfig.NumThreads = settings.NumThreads;
        config.ModelConfig.Provider = "cpu";
        config.DecodingMethod = "greedy_search";
        return new OfflineRecognizer(config);
    }

    public void Dispose() => recognizer?.Dispose();
}
