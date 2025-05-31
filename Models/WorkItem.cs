namespace Faxtract.Models;

public record WorkItem(string Input, string? Response = null, string Status = "Pending");
