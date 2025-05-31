namespace Faxtract.Models;

public class TextChunk(string content, int start, int end)
{
    public string Content { get; set; } = content;
    public int StartPosition { get; set; } = start;
    public int EndPosition { get; set; } = end;
}
