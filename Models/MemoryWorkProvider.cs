using Faxtract.Interfaces;

namespace Faxtract.Models;

public class MemoryWorkProvider : IWorkProvider
{
    private readonly Queue<TextChunk> _workItems = new();
    private readonly object _lock = new();

    public void AddWork(IEnumerable<TextChunk> items)
    {
        lock (_lock)
            foreach (var item in items)
                _workItems.Enqueue(item);
    }

    public List<TextChunk> GetNextBatch(int count)
    {
        var result = new List<TextChunk>();
        lock (_lock)
            while (count-- > 0 && _workItems.TryDequeue(out var item))
                result.Add(item);
        return result;
    }

    public int GetRemainingCount()
    {
        lock (_lock)
            return _workItems.Count;
    }
}
