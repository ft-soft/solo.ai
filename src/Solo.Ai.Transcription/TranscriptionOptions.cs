namespace Solo.Ai.Transcription;

public sealed class TranscriptionOptions
{
    public bool Enabled { get; set; }
    public string ModelPath { get; set; } = "";
    public string TokensPath { get; set; } = "";
    public string VadModelPath { get; set; } = "";
    public string FfmpegPath { get; set; } = "ffmpeg";
    public int NumThreads { get; set; } = 2;
    public const int MaxAudioSeconds = 120;
    public const int MaxUploadBytes = 8 * 1024 * 1024;
}
