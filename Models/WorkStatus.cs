namespace Faxtract.Models;

public record WorkStatus(int ProcessedCount, int RemainingCount, IEnumerable<WorkItem> CurrentItems);
