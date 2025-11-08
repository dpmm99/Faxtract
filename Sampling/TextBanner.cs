using LLama;
using LLama.Native;
using System.Text;

namespace Faxtract.Sampling;

/// <summary>
/// A sampler that works with decoded text, delegating token-level bans to a TokenBanner.
/// This allows for banning/allowing sequences of text that may not align perfectly with token boundaries.
/// </summary>
public class TextBanner(LLamaWeights model, TokenBanner tokenBanner) : ICustomSampler
{
    public long InferredTokenCount => _inferredTokenCount;
    private long _inferredTokenCount = 0;

    public readonly Dictionary<string, List<TextRule>> bannedSequences = [];
    public readonly Dictionary<string, List<TextRule>> allowedSequences = [];
    private Dictionary<int, string> _vocabularyCache = [];

    // State for Sequence Detection
    private readonly CircularBuffer<(int id, string text)> _recentTokens = new(1);
    private readonly StringBuilder _recentTextBuilder = new();
    private int _maxBannedSequenceLengthChars;
    private int _maxAllowedSequenceLengthChars;

    // State for Allowed Sequence Detection using a Trie
    private readonly Trie _allowedTrie = new();
    private TrieNode? _currentTrieNode;

    // Lazy cache for single-token bans: maps banned sequence text to the token ID that contains it
    private Dictionary<string, List<int>>? _singleTokenBanCache;

    private int _maxTokenLength = 1;

    public int TokensToRewind { get; private set; }
    public string Name => nameof(TextBanner);

    private string GetTokenText(LLamaToken token)
    {
        if (!_vocabularyCache.TryGetValue((int)token, out var text))
        {
            text = model.Vocab.LLamaTokenToString(token, false) ?? "";
            _vocabularyCache[(int)token] = text;
        }
        return text;
    }

    public void Accept(LLamaToken token)
    {
        _inferredTokenCount++;

        // Trim history to prevent it from growing indefinitely
        // Note: Not a great approach when sequences are very long, as it shifts a lot of text per token. The cost of convenience!
        var maxLength = Math.Max(_maxBannedSequenceLengthChars, _maxAllowedSequenceLengthChars) + _maxTokenLength;
        while (_recentTextBuilder.Length > maxLength * 2 && _recentTokens.Count > 1)
        {
            var (_, txt) = _recentTokens.RemoveFirst();
            _recentTextBuilder.Remove(0, txt.Length);
        }

        // Add new token text to history
        var text = GetTokenText(token);
        _recentTokens.Add(((int)token, text));
        _recentTextBuilder.Append(text);

        TokensToRewind = 0;

        //Skip all special tokens
        if (token.IsControl(model.Vocab)) return;

        // 1. Check for banned text sequences. This takes precedence.
        if (bannedSequences.Count > 0 && FindAndProcessBannedSequence())
        {
            // If a ban was found, we will be rewinding. Do not update allowed sequence state,
            // as this token acceptance is being invalidated.
            return;
        }

        // 2. Update the state for the allowed sequence Trie.
        if (allowedSequences.Count > 0)
        {
            UpdateAllowedSequenceState(text);
        }
    }

    /// <summary>
    /// Correctly checks if a newly added token has completed a banned sequence.
    /// The sequence does not have to end at the end of the token.
    /// </summary>
    /// <returns>True if a banned sequence was found and processed.</returns>
    private bool FindAndProcessBannedSequence()
    {
        if (_recentTokens.IsEmpty) return false;

        string lastTokenText = _recentTokens.PeekLast().text;

        foreach (var (seqText, rules) in bannedSequences)
        {
            // Find the first active rule for this sequence.
            var activeRule = rules.FirstOrDefault(r => r.TimeFrame.IsActive(_inferredTokenCount));
            if (activeRule == default) continue;

            if (seqText.Length > _recentTextBuilder.Length) continue;

            // Define the search window. A new match must involve the latest token--in fact, it must *end within* the latest token.
            // The earliest a new match could start is (length of banned_seq - 1) characters before the beginning of the new token's text.
            int searchStartIndex = Math.Max(0, _recentTextBuilder.Length - lastTokenText.Length - (seqText.Length - 1));
            int matchIndex = _recentTextBuilder.IndexOf(seqText, searchStartIndex);

            if (matchIndex != -1)
            {
                // Match found! Now calculate how many tokens to rewind to undo it--if BanType is LastToken, then that number is 1.
                int tokensToRewindCount = 0;
                int tokenToBanId = -1;

                // If banning last token only, we only need to rewind 1 token
                if (activeRule.BanType == BannedSequenceBanType.LastToken)
                {
                    tokensToRewindCount = 1;
                    tokenToBanId = _recentTokens.PeekLast().id;
                }
                else
                {
                    int charsToCover = _recentTextBuilder.Length - matchIndex;
                    for (int i = _recentTokens.Count - 1; i >= 0; i--)
                    {
                        var (id, txt) = _recentTokens[i];
                        tokensToRewindCount++;
                        tokenToBanId = id;
                        charsToCover -= txt.Length;
                        if (charsToCover <= 0) break;
                    }
                }

                // Generally, we should ban the token that *started* the sequence, and rewind to before that token was generated. But I'll let the user decide.
                if (tokenToBanId != -1)
                {
                    // The ban should start at the timeline index where the bad sequence began.
                    long banStartIndex = _inferredTokenCount - tokensToRewindCount;
                    var newBan = new TimeFrame(banStartIndex, activeRule.BanDuration);

                    if (!tokenBanner.bannedTokens.TryGetValue(tokenToBanId, out var banList))
                    {
                        banList = [];
                        tokenBanner.bannedTokens[tokenToBanId] = banList;
                    }
                    banList.Add(newBan);
                }

                TokensToRewind = tokensToRewindCount;
                return true; // Handle one ban at a time (though in theory I'd like to find the longest fully-matching ban sequence, or actually the earliest token to rewind to)
            }
        }
        return false;
    }

    private void UpdateAllowedSequenceState(string acceptedText)
    {
        // The new state is the result of traversing from the current state.
        // If the traversal fails at any point, the accepted token was invalid (which shouldn't happen if Apply is correct),
        // so we reset to the root as a fallback.
        _currentTrieNode = GetFinalTrieNode(_currentTrieNode!, acceptedText) ?? _allowedTrie.Root;
    }

    /// <summary>
    /// Simulates a traversal of the allowed sequence Trie to see if a given text is a valid continuation.
    /// It correctly handles concatenations of allowed sequences.
    /// </summary>
    /// <param name="startNode">The node to start traversal from.</param>
    /// <param name="text">The text to check.</param>
    /// <returns>The final TrieNode if the path is valid, otherwise null.</returns>
    private TrieNode? GetFinalTrieNode(TrieNode startNode, string text)
    {
        var currentNode = startNode;
        foreach (char c in text)
        {
            // If the current node marks the end of a complete allowed sequence,
            // the next character can start a new sequence from the root.
            if (currentNode.IsEndOfWord)
                currentNode = _allowedTrie.Root;

            if (!currentNode.Children.TryGetValue(c, out var nextNode))
                return null; // This character breaks the sequence.

            currentNode = nextNode;
        }
        return currentNode;
    }

    /// <summary>
    /// Lazily initializes the cache mapping banned sequences to token IDs that contain them.
    /// Also initializes _maxTokenLength so I don't need another similar loop stringifying every token in the vocab.
    /// </summary>
    private void InitializeSingleTokenBanCache(ref LLamaTokenDataArrayNative tokenData)
    {
        if (_singleTokenBanCache != null) return;

        _singleTokenBanCache = [];

        // Iterate through all tokens to find those containing complete banned sequences
        for (var i = 0; i < tokenData.Data.Length; i++)
        {
            var tokenId = (int)tokenData.Data[i].ID;
            var tokenText = GetTokenText(tokenData.Data[i].ID);
            _maxTokenLength = Math.Max(_maxTokenLength, tokenText.Length);

            // Check if this token contains any complete banned sequence
            foreach (var (seqText, _) in bannedSequences)
            {
                if (tokenText.Contains(seqText))
                {
                    if (!_singleTokenBanCache.TryGetValue(seqText, out var tokenList))
                    {
                        tokenList = [];
                        _singleTokenBanCache[seqText] = tokenList;
                    }
                    tokenList.Add(tokenId);
                }
            }
        }
    }

    public void Apply(ref LLamaTokenDataArrayNative tokenData)
    {
        // 1. Handle single-token bans first (bans take precedence over allows)
        if (bannedSequences.Count > 0)
        {
            // Lazy-initialize the cache on first call
            InitializeSingleTokenBanCache(ref tokenData);

            // Check each banned sequence to see if it's currently active
            foreach (var (seqText, rules) in bannedSequences)
            {
                // Find the first active rule for this sequence
                var activeRule = rules.FirstOrDefault(r => r.TimeFrame.IsActive(_inferredTokenCount));
                if (activeRule == default) continue;

                // If this sequence has associated tokens in the cache, ban them
                if (!_singleTokenBanCache!.TryGetValue(seqText, out var tokenIds)) continue;

                foreach (var tokenId in tokenIds)
                {
                    // Find and ban this token in the data array
                    for (var i = 0; i < tokenData.Data.Length; i++) //TODO: I think if Sorted is false, the tokens are in vocab order? Check my old experimental projects; I know I tested that somewhere
                    {
                        if ((int)tokenData.Data[i].ID == tokenId)
                        {
                            tokenData.Data[i].Logit = float.NegativeInfinity;
                            tokenData.Sorted = false;
                            break;
                        }
                    }
                }
            }
        }

        // 2. Handle allowed sequences (only if not already handling bans)
        if (allowedSequences.Count == 0) return;

        // Only apply the filter if at least one "allowed sequence" rule is currently active.
        bool anyRuleActive = allowedSequences.Values.Any(rules => rules.Any(r => r.TimeFrame.IsActive(_inferredTokenCount)));
        if (anyRuleActive)
        {
            ApplyAllowedSequenceFilter(ref tokenData);
        }
    }

    private void ApplyAllowedSequenceFilter(ref LLamaTokenDataArrayNative tokenData)
    {
        for (var i = 0; i < tokenData.Data.Length; i++)
        {
            //Skip all special tokens and already-banned ones
            if (tokenData.Data[i].ID.IsControl(model.Vocab) || tokenData.Data[i].Logit == float.NegativeInfinity) continue;
            //TODO: In a perfect world, you would ban all tokens that wouldn't lead to one of the next valid entries in the 'allowed' trie, including EOS/EOT.

            // A token is allowed if its text can continue the sequence from the current Trie node.
            var tokenText = GetTokenText(tokenData.Data[i].ID);
            if (GetFinalTrieNode(_currentTrieNode!, tokenText) == null)
            {
                tokenData.Data[i].Logit = float.NegativeInfinity;
                tokenData.Sorted = false;
            }
        }
    }

    /// <summary>
    /// Updates internal structures based on the current set of banned/allowed sequences.
    /// This should be called whenever the sequences are changed.
    /// Since it rebuilds the trie entirely, it does have the consequence of resetting the trie state.
    /// That means any in-progress allowed sequences will be forgotten (as in it acts like the last token ended an allowed sequence), which is a limitation of this approach.
    /// </summary>
    public void UpdateSequences()
    {
        _allowedTrie.Clear();
        foreach (var seq in allowedSequences.Keys)
            _allowedTrie.Insert(seq);

        _currentTrieNode = _allowedTrie.Root;

        _maxBannedSequenceLengthChars = bannedSequences.Keys.Select(k => k.Length).DefaultIfEmpty(0).Max();
        _maxAllowedSequenceLengthChars = allowedSequences.Keys.Select(k => k.Length).DefaultIfEmpty(0).Max();

        // Need enough capacity to hold tokens that could form the longest sequence
        // This is approximate - we're estimating based on character length
        // Should be at least 2x the max to handle rewinding
        var newCapacity = Math.Max(1, Math.Max(_maxBannedSequenceLengthChars, _maxAllowedSequenceLengthChars) * 2);
        if (newCapacity > _recentTokens.Capacity)
            _recentTokens.Resize(newCapacity);
    }

    /// <summary>
    /// A private helper method to create and add a new TextRule to a target dictionary.
    /// </summary>
    /// <returns>True if a rule was successfully added, false otherwise.</returns>
    private bool AddTextRule(Dictionary<string, List<TextRule>> targetDictionary, string sequence, int ruleDuration, int tokenBanDuration = 3, BannedSequenceBanType banType = BannedSequenceBanType.FirstToken)
    {
        if (string.IsNullOrEmpty(sequence)) return false;

        // A duration < 1 signifies a permanent rule. We represent this with a very large number to simplify the IsActive check.
        var activeDuration = ruleDuration < 1 ? int.MaxValue : ruleDuration;

        // The rule starts at the current point in the generation timeline.
        var timeFrame = new TimeFrame(InferredTokenCount, activeDuration);
        var rule = new TextRule(timeFrame, tokenBanDuration, banType);

        if (!targetDictionary.TryGetValue(sequence, out var rules))
        {
            rules = [];
            targetDictionary[sequence] = rules;
        }

        rules.Add(rule);
        return true;
    }

    /// <summary>
    /// Bans a specific sequence of text from being generated.
    /// The rule becomes active immediately and lasts for a specified number of generated tokens.
    /// </summary>
    /// <param name="sequence">The text sequence to ban.</param>
    /// <param name="ruleDuration">The number of tokens this ban rule should remain active for. A value less than 1 means it lasts forever.</param>
    /// <param name="tokenBanDuration">When the sequence is detected, this is the duration (in tokens) for which the target token will be banned.</param>
    /// <param name="banType">Specifies whether to ban the token that started the sequence or the token that completed it.</param>
    public void BanTextSequence(string sequence, int ruleDuration = -1, int tokenBanDuration = 3, BannedSequenceBanType banType = BannedSequenceBanType.FirstToken)
    {
        if (AddTextRule(bannedSequences, sequence, ruleDuration, tokenBanDuration, banType))
        {
            UpdateSequences();
        }
    }

    /// <summary>
    /// Bans multiple sequences of text.
    /// </summary>
    /// <param name="sequences">An enumerable of KeyValuePairs, where the Key is the sequence to ban and the Value is the duration (in tokens) the rule should be active.</param>
    public void BanTextSequences(IEnumerable<KeyValuePair<string, int>> sequences)
    {
        bool needsUpdate = false;
        foreach (var kvp in sequences)
        {
            // Using |= is a concise way to set the flag to true if any rule is successfully added.
            needsUpdate |= AddTextRule(bannedSequences, kvp.Key, kvp.Value);
        }
        if (needsUpdate) UpdateSequences();
    }

    /// <summary>
    /// Adds an allowed text sequence. When any allowed sequence rules are active, only text that forms a valid continuation of one of these sequences can be generated.
    /// </summary>
    /// <param name="sequence">The text sequence to allow.</param>
    /// <param name="ruleDuration">The number of tokens this "allow" rule should remain active for. A value less than 1 means it lasts forever.</param>
    public void AddAllowedTextSequence(string sequence, int ruleDuration = -1)
    {
        if (AddTextRule(allowedSequences, sequence, ruleDuration))
        {
            UpdateSequences();
        }
    }

    /// <summary>
    /// Adds multiple allowed text sequences.
    /// </summary>
    /// <param name="sequences">An enumerable of KeyValuePairs, where the Key is the sequence to allow and the Value is the duration (in tokens) the rule should be active.</param>
    public void AddAllowedTextSequences(IEnumerable<KeyValuePair<string, int>> sequences)
    {
        bool needsUpdate = false;
        foreach (var kvp in sequences)
        {
            needsUpdate |= AddTextRule(allowedSequences, kvp.Key, kvp.Value);
        }
        if (needsUpdate) UpdateSequences();
    }

    public ICustomSampler Clone()
    {
        var clone = new TextBanner(model, tokenBanner.Clone() as TokenBanner ?? throw new InvalidCastException("Cloned token banner is of the wrong type."))
        {
            _inferredTokenCount = _inferredTokenCount
        };

        foreach (var kvp in bannedSequences)
            clone.bannedSequences[kvp.Key] = [.. kvp.Value];

        foreach (var kvp in allowedSequences)
            clone.allowedSequences[kvp.Key] = [.. kvp.Value];

        clone.UpdateSequences(); // Initializes Trie and sets buffer capacity

        // Deep copy the dynamic state
        clone._recentTextBuilder.Append(_recentTextBuilder);
        foreach (var item in _recentTokens)
            clone._recentTokens.Add(item);

        // Resynchronize the Trie to match the current text state
        if (!clone._recentTokens.IsEmpty)
            clone._currentTrieNode = clone.GetFinalTrieNode(clone._allowedTrie.Root, clone._recentTextBuilder.ToString());

        return clone;
    }

    public void ClearBannedSequences()
    {
        bannedSequences.Clear();
        _singleTokenBanCache = null;
        UpdateSequences();
    }

    public void ClearAllowedSequences()
    {
        allowedSequences.Clear();
        _singleTokenBanCache = null;
        UpdateSequences();
    }

    public void Dispose() { }

    public void Reset()
    {
        bannedSequences.Clear();
        allowedSequences.Clear();
        _singleTokenBanCache = null;
        UpdateSequences();
        _inferredTokenCount = 0;
        _recentTokens.Clear();
        _recentTextBuilder.Clear();
    }

    /// <summary>
    /// Efficiently rewinds the buffer by removing tokens and their corresponding text from the end.
    /// </summary>
    public void RewindBuffer(int tokenCount)
    {
        if (tokenCount <= 0) return;

        // 1. Efficiently remove text from the StringBuilder and tokens from history.
        for (int i = 0; i < tokenCount && !_recentTokens.IsEmpty; i++)
        {
            var (_, text) = _recentTokens.Last();
            if (_recentTextBuilder.Length >= text.Length) // Should always be true as long as !_recentTokens.IsEmpty since _recentTextBuilder was constructed from _recentTokens...
                _recentTextBuilder.Remove(_recentTextBuilder.Length - text.Length, text.Length);

            _recentTokens.RemoveLast();
        }

        // Rewind the master timeline
        _inferredTokenCount = Math.Max(0, _inferredTokenCount - tokenCount);

        // Re-sync the Trie state from the new (shorter) history
        _currentTrieNode = _allowedTrie.Root;
        if (!_recentTokens.IsEmpty)
            _currentTrieNode = GetFinalTrieNode(_allowedTrie.Root, _recentTextBuilder.ToString());
    }

    #region Trie Implementation
    private class TrieNode
    {
        public readonly Dictionary<char, TrieNode> Children = [];
        public bool IsEndOfWord { get; set; }
    }

    private class Trie
    {
        public readonly TrieNode Root = new();

        public void Insert(string word)
        {
            var current = Root;
            foreach (char c in word)
            {
                if (!current.Children.TryGetValue(c, out var node))
                {
                    node = new TrieNode();
                    current.Children[c] = node;
                }
                current = node;
            }
            current.IsEndOfWord = true;
        }

        public void Clear()
        {
            Root.Children.Clear();
            Root.IsEndOfWord = false;
        }
    }
    #endregion
}
