namespace Faxtract.Models;

/// <summary>
/// Represents one step in the LLM experiment pipeline, persisted as JSONL.
/// </summary>
public class ExperimentStep
{
    public string LineHash { get; set; } = "";
    public string LineContent { get; set; } = "";
    public int Iteration { get; set; }
    public PrePromptKind Kind { get; set; }
    public string Response { get; set; } = "";
    public string? Feedback { get; set; }
    public DateTime Timestamp { get; set; } = DateTime.UtcNow;
}
