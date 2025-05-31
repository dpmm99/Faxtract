using Faxtract.Models;
using System.Text;
using System.Text.RegularExpressions;

namespace Faxtract.Services;

public class TextChunker(int maxChunkSize = 800, int preferredMinSize = 600)
{
    public async IAsyncEnumerable<TextChunk> ChunkStreamAsync(StreamReader reader, string fileId)
    {
        var buffer = new StringBuilder();
        var position = 0;
        string? line;

        while ((line = await reader.ReadLineAsync()) != null)
        {
            buffer.AppendLine(line);

            // Process buffer when we have enough text, leaving some margin for word boundaries
            if (buffer.Length >= maxChunkSize)
            {
                // Process complete paragraphs, keeping partial ones in buffer
                var lastNewLine = buffer.ToString().LastIndexOf("\n\n");
                if (lastNewLine == -1)
                {
                    lastNewLine = buffer.ToString().LastIndexOf('\n');
                }

                if (lastNewLine > 0)
                {
                    var textToProcess = buffer.ToString(0, lastNewLine);
                    buffer.Remove(0, lastNewLine + 1);

                    foreach (var chunk in ProcessText(textToProcess, position, fileId))
                    {
                        yield return chunk;
                        position = chunk.EndPosition;
                    }
                }
            }
        }

        // Process any remaining text
        if (buffer.Length > 0)
        {
            foreach (var chunk in ProcessText(buffer.ToString(), position, fileId))
            {
                yield return chunk;
                position = chunk.EndPosition;
            }
        }
    }

    private IEnumerable<TextChunk> ProcessText(string text, int position, string fileId)
    {
        var sections = SplitByStructure(text);
        var currentSection = new StringBuilder();
        var sectionStart = position;

        foreach (var section in sections)
        {
            var combinedTokens = EstimateTokenCount(currentSection + section);

            if (combinedTokens > maxChunkSize && currentSection.Length > 0)
            {
                // Yield current accumulated section before processing the new one
                yield return new TextChunk(currentSection.ToString(), sectionStart, position, fileId);
                currentSection.Clear();
                sectionStart = position;
            }

            if (EstimateTokenCount(section) > maxChunkSize)
            {
                // If current section is empty, process oversized section
                if (currentSection.Length > 0)
                {
                    yield return new TextChunk(currentSection.ToString(), sectionStart, position, fileId);
                    currentSection.Clear();
                    sectionStart = position;
                }

                foreach (var chunk in ChunkSection(section, position, fileId))
                {
                    position = chunk.EndPosition;
                    yield return chunk;
                }
            }
            else
            {
                currentSection.Append(section);
                position += section.Length;
            }
        }

        // Don't forget remaining text
        if (currentSection.Length > 0)
        {
            yield return new TextChunk(currentSection.ToString(), sectionStart, position, fileId);
        }
    }

    private IEnumerable<TextChunk> ChunkSection(string section, int position, string fileId)
    {
        var sentences = SplitBySentences(section);
        var currentChunk = new StringBuilder();
        var chunkStart = position;
        var hasContent = false;

        foreach (var sentence in sentences)
        {
            var potentialChunkTokens = EstimateTokenCount(currentChunk + sentence);

            if (potentialChunkTokens > maxChunkSize && currentChunk.Length > 0)
            {
                yield return new TextChunk(currentChunk.ToString(), chunkStart, position, fileId);
                chunkStart = position;
                currentChunk.Clear();
                hasContent = false;
            }

            currentChunk.Append(sentence);
            position += sentence.Length;
            hasContent = true;

            // If we've reached preferred size and aren't in the middle of a section, yield the chunk
            if (EstimateTokenCount(currentChunk.ToString()) >= preferredMinSize &&
                !IsIncompleteSection(sentences.Last(), sentence))
            {
                yield return new TextChunk(currentChunk.ToString(), chunkStart, position, fileId);
                chunkStart = position;
                currentChunk.Clear();
                hasContent = false;
            }
        }

        // Always process remaining content, regardless of size
        if (hasContent)
        {
            yield return new TextChunk(currentChunk.ToString(), chunkStart, position, fileId);
        }
    }

    private static bool IsIncompleteSection(string lastSentence, string currentSentence)
    {
        // Don't split if we're at the last sentence
        return currentSentence != lastSentence;
    }

    private List<string> SplitByStructure(string text)
    {
        // Split by double newlines to separate paragraphs
        return text.Split(["\n\n", "\r\n\r\n"], StringSplitOptions.RemoveEmptyEntries)
            .Select(s => s.Trim())
            .Where(s => !string.IsNullOrEmpty(s))
            .ToList();
    }

    private List<string> SplitBySentences(string text)
    {
        // Simple sentence splitting - can be improved based on needs
        return Regex.Split(text, @"(?<=[.!?])\s+")
            .Select(s => s.Trim())
            .Where(s => !string.IsNullOrEmpty(s))
            .ToList();
    }

    private int EstimateTokenCount(string text)
    {
        // Rough estimation: ~4 characters per token
        return text?.Length / 4 ?? 0;
    }
}
