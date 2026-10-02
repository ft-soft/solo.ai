namespace Solo.Ai.Client;

/// <summary>Only HTTP observation was cancelled; an already sent command may have committed.</summary>
public sealed class SoloAiRequestCanceledException(bool outcomeUnknown, CancellationToken cancellationToken)
    : OperationCanceledException("HTTP observation cancelled.", cancellationToken)
{
    public bool OutcomeUnknown { get; } = outcomeUnknown;
}
