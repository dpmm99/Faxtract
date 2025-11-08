namespace Faxtract.Sampling;

/// <summary>
/// Represents a rule that is active for a specific period in the token generation timeline.
/// </summary>
public readonly record struct TimeFrame(long StartTokenIndex, int Duration)
{
    /// <summary>
    /// Checks if this rule is active at the given point in the token generation timeline.
    /// </summary>
    public bool IsActive(long currentTokenIndex) =>
        currentTokenIndex >= StartTokenIndex && currentTokenIndex - StartTokenIndex < Duration;
}
