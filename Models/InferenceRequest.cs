namespace Faxtract.Models;

public enum PrePromptKind
{
    FlashCard,
    Write,
    Judgment,
    Correction
}

public record InferenceRequest(
    int Id,
    string Prompt,
    string? ExtraContext,
    TaskCompletionSource<string> Tcs,
    int Iteration = 0,
    PrePromptKind PrePromptKind = PrePromptKind.FlashCard
);
