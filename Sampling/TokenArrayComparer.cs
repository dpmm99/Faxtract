using LLama.Native;

namespace Faxtract.Sampling;

/// <summary>
/// A custom comparer to allow using token arrays as dictionary keys.
/// </summary>
public class TokenArrayComparer : IEqualityComparer<LLamaToken[]>
{
    public bool Equals(LLamaToken[]? x, LLamaToken[]? y)
    {
        if (ReferenceEquals(x, y)) return true;
        if (x is null || y is null) return false;
        return x.SequenceEqual(y);
    }

    public int GetHashCode(LLamaToken[] obj)
    {
        if (obj is null) return 0;
        unchecked // Overflow is fine, just wrap
        {
            int hash = 17;
            foreach (var token in obj)
            {
                hash = hash * 23 + token.GetHashCode();
            }
            return hash;
        }
    }
}
