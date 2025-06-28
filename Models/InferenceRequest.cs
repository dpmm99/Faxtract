namespace Faxtract.Models;

public record InferenceRequest(
    int Id,
    string Prompt,
    string? ExtraContext,
    TaskCompletionSource<string> Tcs
);
