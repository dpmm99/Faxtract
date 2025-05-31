using Faxtract.Models;

namespace Faxtract.Interfaces;

public interface IWorkProvider
{
    void AddWork(IEnumerable<TextChunk> items);
    List<TextChunk> GetNextBatch(int count);
    int GetRemainingCount();
}
