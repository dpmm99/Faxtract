namespace Faxtract.Models;

public class TextChunk(string content, int start, int end, string fileId, string? extraContext = null)
{
    public int Id { get; set; }
    public string Content { get; set; } = content;
    public int StartPosition { get; set; } = start;
    public int EndPosition { get; set; } = end;
    public string FileId { get; set; } = fileId;
    public string? ExtraContext { get; set; } = extraContext;
}
