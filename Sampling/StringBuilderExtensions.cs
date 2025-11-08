using System.Text;

namespace Faxtract.Sampling;

public static class StringBuilderExtensions
{
    /// <summary>
    /// Searches for the first occurrence of a string within a StringBuilder,
    /// starting from a specified index.
    /// </summary>
    /// <param name="sb">The StringBuilder to search in.</param>
    /// <param name="value">The string to search for.</param>
    /// <param name="startIndex">The search starting position (inclusive).</param>
    /// <returns>The zero-based index of the first occurrence, or -1 if not found.</returns>
    public static int IndexOf(this StringBuilder sb, string value, int startIndex = 0)
    {
        ArgumentNullException.ThrowIfNull(value);
        if (value.Length == 0) return startIndex;
        if (startIndex < 0 || startIndex >= sb.Length) return -1;
        if (sb.Length - startIndex < value.Length) return -1;

        int maxSearchIndex = sb.Length - value.Length;

        for (int i = startIndex; i <= maxSearchIndex; i++)
        {
            // Check if we have a match starting at position i
            bool match = true;
            for (int j = 0; j < value.Length; j++)
            {
                if (sb[i + j] != value[j])
                {
                    match = false;
                    break;
                }
            }

            if (match)
            {
                return i;
            }
        }

        return -1;
    }
}
