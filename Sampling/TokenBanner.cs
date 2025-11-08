using LLama;
using LLama.Native;

namespace Faxtract.Sampling;

public class TokenBanner(LLamaWeights model) : ICustomSampler
{
    public readonly Dictionary<LLamaToken, List<TimeFrame>> bannedTokens = [];
    public readonly Dictionary<LLamaToken[], List<TimeFrame>> bannedSequences = new(new TokenArrayComparer());
    public readonly Dictionary<LLamaToken[], List<TimeFrame>> allowedSequences = new(new TokenArrayComparer());

    private readonly CircularBuffer<LLamaToken> _recentTokens = new(1);
    public long InferredTokenCount { get; private set; } // Master timeline of token generation

    public int TokensToRewind { get; private set; }

    public string Name => nameof(TokenBanner);

    public void Accept(LLamaToken token)
    {
        _recentTokens.Add(token);
        InferredTokenCount++;
        TokensToRewind = 0;

        //Skip all special tokens
        if (token.IsControl(model.Vocab)) return;

        // Check for banned sequences where the rule itself is currently active.
        if (bannedSequences.Count > 0)
        {
            foreach (var (seq, timeFrames) in bannedSequences)
            {
                // Find the first active timeframe for this sequence rule.
                var activeTimeFrame = timeFrames.FirstOrDefault(tf => tf.IsActive(InferredTokenCount));
                if (activeTimeFrame == default) continue;

                if (_recentTokens.TailMatches(seq))
                {
                    // A banned sequence was just generated while its rule was active.
                    // Create a new ban on the first token of that sequence.
                    // The duration of this new ban is inherited from the sequence rule's duration.
                    var tokenToBan = seq[0];
                    var banDuration = activeTimeFrame.Duration;
                    var newBan = new TimeFrame(InferredTokenCount, banDuration);

                    if (!bannedTokens.TryGetValue(tokenToBan, out var banList))
                    {
                        banList = [];
                        bannedTokens[tokenToBan] = banList;
                    }
                    banList.Add(newBan);

                    TokensToRewind = seq.Length;
                    break; // Only handle the first matched sequence.
                }
            }
        }
    }

    public void Apply(ref LLamaTokenDataArrayNative tokenData)
    {
        // Handle allowed sequences first, as they are more restrictive.
        if (allowedSequences.Count > 0)
        {
            ApplyAllowedSequenceFilter(ref tokenData);
        }

        // No regular banned tokens to process, so we can exit early.
        if (bannedTokens.Count == 0) return;

        // Apply standard token bans by checking against the timeline.
        for (var i = 0; i < tokenData.Data.Length; i++)
        {
            //Skip all special tokens
            if (tokenData.Data[i].ID.IsControl(model.Vocab)) continue;
            if (bannedTokens.TryGetValue(tokenData.Data[i].ID, out var banList))
            {
                // Check if any of the ban instances for this token are active right now.
                if (banList.Any(ban => ban.IsActive(InferredTokenCount)))
                {
                    tokenData.Data[i].Logit = float.NegativeInfinity;
                    tokenData.Sorted = false;
                }
            }
        }
    }

    private void ApplyAllowedSequenceFilter(ref LLamaTokenDataArrayNative tokenData)
    {
        var allowedNextTokens = new HashSet<LLamaToken>();
        bool activeAllowRuleExists = false;

        foreach (var (sequence, timeFrames) in allowedSequences)
        {
            // Check if any timeframe for this sequence rule is currently active.
            if (!timeFrames.Any(tf => tf.IsActive(InferredTokenCount)))
            {
                continue;
            }

            activeAllowRuleExists = true;
            if (sequence.Length == 0) continue;

            var prefixLen = _recentTokens.GetMatchingPrefixLength(sequence);

            if (prefixLen > 0)
            {
                // We are in the middle of a potential allowed sequence.
                if (prefixLen < sequence.Length)
                    allowedNextTokens.Add(sequence[prefixLen]);
                // If prefixLen equals sequence.Length, the sequence is complete, so we don't restrict further.
            }
            else if (_recentTokens.IsEmpty)
            {
                // We are at the beginning, so allow the first token of any allowed sequence.
                allowedNextTokens.Add(sequence[0]);
            }
        }

        // If at least one "allow" rule is active, we must enforce the constraints.
        if (activeAllowRuleExists)
        {
            // If our current context matches the start of an allowed sequence,
            // permit only the valid next tokens.
            if (allowedNextTokens.Count > 0)
            {
                for (var i = 0; i < tokenData.Data.Length; i++)
                {
                    //Skip all special tokens
                    if (tokenData.Data[i].ID.IsControl(model.Vocab)) continue;
                    if (!allowedNextTokens.Contains(tokenData.Data[i].ID))
                    {
                        tokenData.Data[i].Logit = float.NegativeInfinity;
                        tokenData.Sorted = false;
                    }
                }
            }
            else
            {
                // We are under an "allow" constraint, but no sequence matches our
                // current context. This means we are in an invalid state, so ban all tokens
                // to force a backtrack or different generation path.
                for (var i = 0; i < tokenData.Data.Length; i++)
                {
                    //Skip all special tokens
                    if (tokenData.Data[i].ID.IsControl(model.Vocab)) continue;
                    tokenData.Data[i].Logit = float.NegativeInfinity;
                }
                tokenData.Sorted = false;
            }
        }
    }

    public void UpdateRecentTokensLength()
    {
        int maxSeqLen = 0;
        if (bannedSequences.Count > 0)
            maxSeqLen = bannedSequences.Keys.Max(seq => seq?.Length ?? 0);

        if (allowedSequences.Count > 0)
            maxSeqLen = Math.Max(maxSeqLen, allowedSequences.Keys.Max(seq => seq?.Length ?? 0));

        // Capacity should be at least 1. Double the length to ensure enough history for tail matching even after a rewind.
        var newCapacity = Math.Max(1, maxSeqLen * 2);
        if (newCapacity > _recentTokens.Capacity)
        {
            _recentTokens.Resize(newCapacity);
        }
    }

    public void RewindBuffer(int tokenCount)
    {
        for (int i = 0; i < tokenCount && !_recentTokens.IsEmpty; i++)
        {
            _recentTokens.RemoveLast();
        }
        // Rewind our master timeline.
        InferredTokenCount = Math.Max(0, InferredTokenCount - tokenCount);
    }


    /// <summary>
    /// A private helper method to create and add a new TimeFrame to a target dictionary.
    /// </summary>
    /// <returns>True if a rule was successfully added, false otherwise.</returns>
    private bool AddTokenRule(Dictionary<LLamaToken[], List<TimeFrame>> targetDictionary, LLamaToken[] sequence, int ruleDuration)
    {
        if (sequence == null || sequence.Length == 0) return false;

        // A duration < 1 signifies a permanent rule. We represent this with a very large number to simplify the IsActive check.
        var activeDuration = ruleDuration < 1 ? int.MaxValue : ruleDuration;

        // The rule starts at the current point in the generation timeline.
        var timeFrame = new TimeFrame(InferredTokenCount, activeDuration);

        if (!targetDictionary.TryGetValue(sequence, out var timeFrames))
        {
            timeFrames = [];
            targetDictionary[sequence] = timeFrames;
        }

        timeFrames.Add(timeFrame);
        return true;
    }

    public void BanToken(LLamaToken token, int banDuration = 1)
    {
        if (model.Vocab.EOS.HasValue && token == model.Vocab.EOS.Value) throw new InvalidOperationException("Cannot ban EOS token; an infinite loop is possible if all tokens are banned/no tokens are allowed.");
        if (model.Vocab.EOT.HasValue && token == model.Vocab.EOT.Value) throw new InvalidOperationException("Cannot ban EOT token; an infinite loop is possible if all tokens are banned/no tokens are allowed.");
        if (!bannedTokens.TryGetValue(token, out var banTokens))
        {
            banTokens = [];
            bannedTokens[token] = banTokens;
        }

        banTokens.Add(new TimeFrame(InferredTokenCount, banDuration < 1 ? int.MaxValue : banDuration));
    }

    public void BanSequence(LLamaToken[] sequence, int banDuration = 1)
    {
        if (model.Vocab.EOS.HasValue && sequence.Contains(model.Vocab.EOS.Value)) throw new InvalidOperationException("Cannot ban EOS token; an infinite loop is possible if all tokens are banned/no tokens are allowed.");
        if (model.Vocab.EOT.HasValue && sequence.Contains(model.Vocab.EOT.Value)) throw new InvalidOperationException("Cannot ban EOT token; an infinite loop is possible if all tokens are banned/no tokens are allowed.");
        AddTokenRule(bannedSequences, sequence, banDuration);
        UpdateRecentTokensLength();
    }

    public void AddAllowedSequence(LLamaToken[] sequence, int allowDuration = 1)
    {
        AddTokenRule(allowedSequences, sequence, allowDuration);

        // Never disallow EOT or EOS, if known, or we can end up in an infinite loop because everything is banned.
        if (model.Vocab.EOT.HasValue) allowedSequences[[model.Vocab.EOT.Value]] = [new TimeFrame(0, int.MaxValue)];
        if (model.Vocab.EOS.HasValue) allowedSequences[[model.Vocab.EOS.Value]] = [new TimeFrame(0, int.MaxValue)];
        UpdateRecentTokensLength();
    }

    public void ClearBannedTokens()
    {
        bannedTokens.Clear();
    }

    public void ClearBannedSequences()
    {
        bannedSequences.Clear();
        UpdateRecentTokensLength();
    }

    public void ClearAllowedSequences()
    {
        allowedSequences.Clear();
        UpdateRecentTokensLength();
    }

    public ICustomSampler Clone()
    {
        var clone = new TokenBanner(model)
        {
            InferredTokenCount = InferredTokenCount,
            TokensToRewind = TokensToRewind,
        };

        // Deep copy the stateful dictionaries
        foreach (var kvp in bannedTokens)
            clone.bannedTokens[kvp.Key] = [.. kvp.Value];

        foreach (var kvp in bannedSequences)
            clone.bannedSequences[kvp.Key] = [.. kvp.Value];

        foreach (var kvp in allowedSequences)
            clone.allowedSequences[kvp.Key] = [.. kvp.Value];

        // Copy the recent token history
        clone._recentTokens.Resize(_recentTokens.Capacity);
        foreach (var token in _recentTokens)
            clone._recentTokens.Add(token);

        return clone;
    }

    public void Dispose() { }

    public void Reset()
    {
        bannedTokens.Clear();
        bannedSequences.Clear();
        allowedSequences.Clear();
        UpdateRecentTokensLength();
        TokensToRewind = 0;
        InferredTokenCount = 0;
    }
}
