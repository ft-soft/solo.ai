using SherpaOnnx;

namespace Solo.Ai.Transcription;

internal static class SpeechSegmenter
{
    public static IEnumerable<float[]> Split(float[] samples, string modelPath, CancellationToken ct)
    {
        if (!File.Exists(modelPath))
            throw new InvalidOperationException("Silero VAD model must be installed for long recordings.");
        var config = new VadModelConfig();
        config.SileroVad.Model = modelPath;
        config.SileroVad.MinSilenceDuration = 0.4F;
        config.SileroVad.MinSpeechDuration = 0.15F;
        config.SileroVad.MaxSpeechDuration = 20;
        using var vad = new VoiceActivityDetector(config, 30);
        var window = new float[config.SileroVad.WindowSize];
        var activeSamples = 0;
        var previousEnd = 0;
        for (var offset = 0; offset < samples.Length; offset += window.Length)
        {
            ct.ThrowIfCancellationRequested();
            Array.Clear(window);
            samples.AsSpan(offset, Math.Min(window.Length, samples.Length - offset)).CopyTo(window);
            vad.AcceptWaveform(window);
            activeSamples = vad.IsSpeechDetected() ? activeSamples + window.Length : 0;
            // VAD's MaxSpeechDuration only encourages a pause; it does not enforce a hard cap.
            // Force a boundary for uninterrupted speech, leaving room for VAD's leading context.
            if (activeSamples >= 20 * 16000)
            {
                vad.Flush();
                activeSamples = 0;
            }
            while (!vad.IsEmpty())
            {
                var segment = vad.Front();
                vad.Pop();
                yield return ExtractWithContext(segment);
            }
        }
        vad.Flush();
        while (!vad.IsEmpty())
        {
            var segment = vad.Front();
            vad.Pop();
            yield return ExtractWithContext(segment);
        }

        float[] ExtractWithContext(SpeechSegment segment)
        {
            // Keep quiet word edges around VAD boundaries, without duplicating overlapping audio.
            var start = Math.Max(previousEnd, Math.Max(0, segment.Start - 3200));
            var end = Math.Min(samples.Length, segment.Start + segment.Samples.Length + 3200);
            previousEnd = end;
            return samples[start..end];
        }
    }
}
