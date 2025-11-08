namespace Faxtract.Sampling;

/// <summary>
/// Defines a text-based rule with its active timeframe and resulting ban behavior.
/// </summary>
public readonly record struct TextRule(TimeFrame TimeFrame, int BanDuration, BannedSequenceBanType BanType = BannedSequenceBanType.FirstToken);
