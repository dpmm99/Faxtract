namespace Faxtract.Models;

public class TextChunk(string Content, int StartPosition, int EndPosition, string FileId, string? ExtraContext = null)
{
    public int Id { get; set; }
    public string Content { get; set; } = Content;
    public int StartPosition { get; set; } = StartPosition;
    public int EndPosition { get; set; } = EndPosition;
    public string FileId { get; set; } = FileId;
    public string? ExtraContext { get; set; } = ExtraContext;
}
