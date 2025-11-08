using Faxtract.Sampling;
using LLama;
using LLama.Common;
using LLama.Native;
using Microsoft.Extensions.Configuration;
using System.Buffers;
using System.Reflection;
using System.Text;

namespace Tests;

[TestClass]
public class TextBannerTests
{
    private const string TextBannerFieldName = "_textBanner";

    private static LLamaWeights _model = null!;
    private TextBanner _textBannerInstance = null!;
    private TokenBanner _tokenBanner = null!;

    #region Setup and Teardown

    [ClassInitialize]
    public static void ClassInitialize(TestContext context)
    {
        // Load the model vocab once for all tests in this class.
        //var modelParams = new ModelParams(@"C:\AI\Qwen2.5-0.5B-Instruct-Q6_K.gguf") { VocabOnly = true };
        //Trying with different models.
        //var modelParams = new ModelParams(@"C:\AI\Llama-3.2-1B-Instruct-Q6_K.gguf") { VocabOnly = true };
        //var modelParams = new ModelParams(@"C:\AI\MiniCPM-o-7.6B-Q6_K.gguf") { VocabOnly = true };
        var modelParams = new ModelParams(@"C:\AI\Magistral-Small-2509-UD-Q5_K_XL.gguf") { VocabOnly = true };

        _model = LLamaWeights.LoadFromFile(modelParams);
    }

    [ClassCleanup(ClassCleanupBehavior.EndOfClass)]
    public static void ClassCleanup()
    {
        _model?.Dispose();
    }

    [TestInitialize]
    public void TestInitialize()
    {
        // Before each test, get a fresh instance of TextBanner and its dependencies.
        GetTextBannerInstance(_model);

        // Reset the banner to a clean state.
        _textBannerInstance.Reset();
    }

    #endregion

    #region Banned Sequence Tests

    [TestMethod]
    [DataRow("llo worl", DisplayName = "Mid word on both ends")]
    [DataRow("llo world", DisplayName = "Ends on token boundary")]
    [DataRow(" worl", DisplayName = "Starts on token boundary")]
    public void Accept_BanSpansMultipleTokens_RewindsAndBansCorrectly(string bannedSequence)
    {
        // Arrange
        _textBannerInstance.BanTextSequence(bannedSequence);

        // Tokenize the full sequence to see how it's broken down by the model.
        // This is crucial for simulating the generation process accurately.
        var helloTokens = Tokenize("Hello");
        var worldTokens = Tokenize(" world");
        var fullSequenceTokens = helloTokens.Concat(worldTokens).ToArray();

        // Act & Assert Part 1: Simulate generating "Hello" token by token.
        // After each token, the ban is not yet complete.
        foreach (var token in helloTokens)
        {
            SimulateGenerationStep(_textBannerInstance, token, [token]);
            Assert.AreEqual(0, _textBannerInstance.TokensToRewind);
        }

        // Act & Assert Part 2: Simulate generating the final tokens that complete the ban.
        for (int i = 0; i < worldTokens.Length; i++)
        {
            var token = worldTokens[i];
            SimulateGenerationStep(_textBannerInstance, token, [token]);

            // The rewind should trigger only when the *last* token of the banned sequence is accepted.
            if (i == worldTokens.Length - 1)
            {
                if (bannedSequence == " worl")
                {
                    Assert.AreEqual(worldTokens.Length, _textBannerInstance.TokensToRewind, "Should rewind over just the ' world' token(s)");
                    var firstTokenOfBannedMatch = worldTokens[0];
                    Assert.IsTrue(_tokenBanner.bannedTokens.ContainsKey((int)firstTokenOfBannedMatch), "Should ban the token that started the sequence");
                }
                else
                {
                    Assert.AreEqual(fullSequenceTokens.Length, _textBannerInstance.TokensToRewind, "Should rewind over all tokens that form 'Hello world'");
                    var firstTokenOfBannedMatch = helloTokens[0];
                    Assert.IsTrue(_tokenBanner.bannedTokens.ContainsKey((int)firstTokenOfBannedMatch), "Should ban the token that started the sequence");
                }
            }
            else
            {
                Assert.AreEqual(0, _textBannerInstance.TokensToRewind);
            }
        }
    }

    [TestMethod]
    public void Accept_BanInMiddleOfSingleToken_RewindsAndBansCorrectly()
    {
        // Arrange
        // The idea is to try multiple words in case they're not a single token... then you have to substring the chosen word for the next line.
        (string word, int tokenId) = FindSingleTokenWord("excel", "banana", "fantastic", "marvel", "orange");

        // Ban a sequence that is guaranteed to be inside the single token's string representation.
        string bannedSubstring = word[1..^1];
        _textBannerInstance.BanTextSequence(bannedSubstring);

        // Act: Simulate a single generation step where the token containing the ban is chosen.
        // This correctly follows the Apply -> Accept pattern.
        SimulateGenerationStep(_textBannerInstance, (LLamaToken)tokenId, [(LLamaToken)tokenId]);

        // Assert
        Assert.AreEqual(1, _textBannerInstance.TokensToRewind, "Should rewind over the single token containing the ban.");
        Assert.IsTrue(_tokenBanner.bannedTokens.ContainsKey(tokenId), "Should ban the token containing the sequence.");
    }

    [TestMethod]
    public void Accept_BanStartsMidTokenEndsMidToken_RewindsAndBansCorrectly()
    {
        // Arrange
        const string bannedSequence = "fox jumps";
        _textBannerInstance.BanTextSequence(bannedSequence);

        const string fullText = "The quick brown fox jumps over";
        var tokens = Tokenize(fullText);

        // This is a robust way to find which tokens are involved in the banned sequence,
        // regardless of how the tokenizer splits the words.
        int startIndex = fullText.IndexOf(bannedSequence);
        int endIndex = startIndex + bannedSequence.Length;
        var (startTokenIndex, endTokenIndex) = FindTokenIndicesForTextSpan(tokens, startIndex, endIndex);

        // Act & Assert: Simulate generation up to the point of the ban.
        for (int i = 0; i < tokens.Length; i++)
        {
            SimulateGenerationStep(_textBannerInstance, tokens[i], [tokens[i]]);

            if (i < endTokenIndex)
            {
                // Before the ban is complete, no rewind should occur.
                Assert.AreEqual(0, _textBannerInstance.TokensToRewind);
            }
            else if (i == endTokenIndex)
            {
                // Once the token that completes the ban is accepted, assert the rewind.
                int expectedRewindCount = endTokenIndex - startTokenIndex + 1;
                Assert.AreEqual(expectedRewindCount, _textBannerInstance.TokensToRewind, "Should rewind over all tokens forming the sequence.");
                Assert.IsTrue(_tokenBanner.bannedTokens.ContainsKey(tokens[startTokenIndex]), "Should ban the first token involved in the sequence.");
                break; // Stop after the ban is triggered.
            }
        }
    }

    [TestMethod]
    public void Accept_NoMatchingBan_DoesNotRewind()
    {
        // Arrange
        _textBannerInstance.BanTextSequence("XYZ");

        var tokens = Tokenize("ABC");

        // Act: Simulate generation of "ABC"
        foreach (var token in tokens)
        {
            SimulateGenerationStep(_textBannerInstance, token, [token]);

            // Assert
            Assert.AreEqual(0, _textBannerInstance.TokensToRewind);
            Assert.AreEqual(0, _tokenBanner.bannedTokens.Count);
        }
    }

    #endregion

    #region Allowed Sequence Tests

    [TestMethod]
    public void Apply_AllowedSequence_OnlyPermitsValidContinuations()
    {
        // Arrange
        _textBannerInstance.AddAllowedTextSequence("Hello");

        // Potential first tokens for the generation.
        using var handle = CreateTokenDataArray(["He", "llo", "World", "H"], out var tokenData);
        var originalSorted = tokenData.Sorted;

        // Act 1: At the start, only tokens that can begin "Hello" should be allowed.
        _textBannerInstance.Apply(ref tokenData);

        // Assert 1
        AssertTokenLogit("He", tokenData, isAllowed: true);
        AssertTokenLogit("H", tokenData, isAllowed: true);
        AssertTokenLogit("llo", tokenData, isAllowed: false); // Does not start "Hello"
        AssertTokenLogit("World", tokenData, isAllowed: false);
        Assert.IsFalse(tokenData.Sorted, "Sorted flag should be false after modification.");

        // Arrange 2: Now, simulate that "He" was chosen and accepted.
        var tokenHe = Tokenize("He")[0];
        SimulateGenerationStep(_textBannerInstance, tokenHe, [tokenHe]);

        // Potential next tokens.
        using var handle2 = CreateTokenDataArray(["llo", "World", "Invalid"], out var tokenData2);

        // Act 2: After "He", only tokens that can continue "Hello" should be allowed.
        _textBannerInstance.Apply(ref tokenData2);

        // Assert 2
        AssertTokenLogit("llo", tokenData2, isAllowed: true); // Correct continuation
        AssertTokenLogit("World", tokenData2, isAllowed: false);
        AssertTokenLogit("Invalid", tokenData2, isAllowed: false);
    }

    [TestMethod]
    public void Apply_TransitionBetweenAllowedSequences_ResetsTrieState()
    {
        // Arrange
        _textBannerInstance.AddAllowedTextSequences(new Dictionary<string, int> { { "A", -1 }, { "B", -1 } });

        // Act 1: Simulate generating "A", which is a complete allowed sequence.
        var tokenA = Tokenize("A")[0];
        SimulateGenerationStep(_textBannerInstance, tokenA, [tokenA, Tokenize("B")[0], Tokenize("C")[0]]);

        // Arrange 2: Now that "A" is complete, the state should reset to the root of the Trie,
        // allowing either "A" or "B" to start again.
        using var handle = CreateTokenDataArray(["B", "C"], out var tokenData);

        // Act 2
        _textBannerInstance.Apply(ref tokenData);

        // Assert
        AssertTokenLogit("B", tokenData, isAllowed: true);
        AssertTokenLogit("C", tokenData, isAllowed: false);
    }

    [TestMethod]
    public void Apply_SingleTokenCompletesOneAndStartsAnother_IsValid()
    {
        // Arrange
        _textBannerInstance.AddAllowedTextSequences(new Dictionary<string, int> { { "Hello", -1 }, { "World", -1 } });

        // We've already output "He"
        var tokenHe = Tokenize("He")[0];
        SimulateGenerationStep(_textBannerInstance, tokenHe, [tokenHe]);

        // The next token is "lloWorld", which completes "Hello" and starts "World"
        using var handle = CreateTokenDataArray(["lloWorld", "Invalid"], out var tokenData);

        // Act
        _textBannerInstance.Apply(ref tokenData);

        // Assert: The logic should trace through "llo", see it's an end-of-word,
        // reset to root, and then trace "World", making the whole token valid.
        AssertTokenLogit("lloWorld", tokenData, isAllowed: true);
        AssertTokenLogit("Invalid", tokenData, isAllowed: false);
    }

    [TestMethod]
    public void RewindBuffer_StateIsCorrectlyReverted()
    {
        // Arrange
        _textBannerInstance.AddAllowedTextSequences(new Dictionary<string, int> { { "AB", -1 }, { "AC", -1 } });

        var tokenA = Tokenize("A")[0];
        var tokenB = Tokenize("B")[0];

        // Simulate generating "A", then "B"
        SimulateGenerationStep(_textBannerInstance, tokenA, [tokenA, tokenB]);
        SimulateGenerationStep(_textBannerInstance, tokenB, [tokenA, tokenB]);

        // Act: Rewind by one token. The state should revert to having just processed "A".
        _textBannerInstance.RewindBuffer(1);

        // Assert: The state should be as if we only accepted "A".
        // Therefore, both "B" and "C" should be allowed as valid continuations of "A".
        using var handle = CreateTokenDataArray(["B", "C"], out var tokenData);
        _textBannerInstance.Apply(ref tokenData);

        AssertTokenLogit("B", tokenData, isAllowed: true, "After rewinding, 'B' should be a valid next token to form 'AB'.");
        AssertTokenLogit("C", tokenData, isAllowed: true, "After rewinding, 'C' should also be a valid next token to form 'AC'.");
    }

    [TestMethod]
    public void Accept_MultipleBansAndRewinds_NavigatesAlternativePathsCorrectly()
    {
        // Arrange
        // This ban should trigger a multi-token rewind.
        const string rewindBan = "ick br";
        // This ban should be a simple block, but because we switch paths, it won't be encountered.
        const string simpleBan = "over";
        _textBannerInstance.BanTextSequences(new Dictionary<string, int> { { rewindBan, -1 }, { simpleBan, -1 } });

        // We need two sentences that tokenize to the same length to simulate a step-by-step choice.
        // This is highly dependent on the model's vocabulary. These sentences have been
        // verified to work with the Magistral-Small model used in this test class.
        const string primaryPath = "The quick brown fox jumps over the lazy dog.";
        const string alternativePath = "A fast yellow horse pops across a dead bush.";

        var primaryTokens = Tokenize(primaryPath);
        var alternativeTokens = Tokenize(alternativePath);
        var eosOrEot = _model.Vocab.EOT ?? _model.Vocab.EOS ?? _model.Vocab.Newline ?? default;

        // Assemble the complete vocabulary for use by _singleTokenBanCache
        using var completeVocab = CreateTokenDataArrayFromTokens(primaryTokens.Concat(alternativeTokens).Concat([eosOrEot]).Distinct(), out var vocabData);

        Assert.AreEqual(primaryTokens.Length, alternativeTokens.Length,
            "The primary and alternative paths must tokenize to the same length for this test to be valid. Primary: " +
            string.Join('/', primaryTokens.Select(p => _model.Vocab.LLamaTokenToString(p, false))) +
            "; secondary: " +
            string.Join('/', alternativeTokens.Select(p => _model.Vocab.LLamaTokenToString(p, false))));

        var generatedTokens = new List<LLamaToken>();
        int rewindTriggerIndex = int.MaxValue;

        // Act & Assert
        for (int i = 0; i < primaryTokens.Length; i++)
        {
            // Simulate a choice between the primary and alternative tokens at this step.
            using var handle = CreateTokenDataArrayFromTokens([primaryTokens[i], alternativeTokens[i]], out var tokenData);
            if (i == 0) tokenData = vocabData; // On the first iteration, provide the full vocab to initialize _singleTokenBanCache.

            // The pipeline applies constraints. This might ban 'tokenToTry' directly.
            _tokenBanner.Apply(ref tokenData);
            _textBannerInstance.Apply(ref tokenData);

            // In a real scenario, a sampler would pick from the valid logits.
            // Here, we simulate that choice. If our preferred token wasn't banned by Apply, we use it.
            var acceptedToken = tokenData.Data.ToArray().OrderBy(p => p.Logit).Last();

            _tokenBanner.Accept(acceptedToken.ID);
            _textBannerInstance.Accept(acceptedToken.ID);
            generatedTokens.Add(acceptedToken.ID);
            var debugValue = tokenData.Data.ToArray().Select(p => new { p.ID, p.Logit, Text = _model.Vocab.LLamaTokenToString(p.ID, false) }).ToList();

            //TODO: "over" sets textBanner.TokensToRewind to 1 when it really ought to just set the " over" logit to NegativeInfinity. This is partly because I let the LLM move the logic to Accept. Think through whether it HAS to be there or should be in Apply.
            var rewind = _tokenBanner.TokensToRewind + _textBannerInstance.TokensToRewind;
            if (rewind > 0)
            {
                // This block should execute when the tokens for " quick brown" are accepted.
                rewindTriggerIndex = Math.Min(i + 1 - rewind, rewindTriggerIndex); // The banned token's index

                // Find the actual start of the banned text in our token stream.
                if (_textBannerInstance.TokensToRewind > 0)
                {
                    var generatedText = string.Concat(generatedTokens.Select(t => _model.Vocab.LLamaTokenToString(t, false)));
                    int banStartIndexInText = generatedText.IndexOf(rewindBan);
                    //if (banStartIndexInText == -1) banStartIndexInText = generatedText.IndexOf(simpleBan); //TODO: The fact that I need this code here means the test should really be failing. I don't want to require a rewind if the banned text is fully contained in the current token.
                    Assert.IsTrue(banStartIndexInText >= 0, "The rewind was triggered, but the banned sequence wasn't found in the generated text.");

                    var (startTokenIndex, endTokenIndex) = FindTokenIndicesForTextSpan([.. generatedTokens], banStartIndexInText, banStartIndexInText + rewindBan.Length);

                    // Assert that the rewind request matches the number of tokens that formed the banned sequence.
                    int expectedRewindCount = endTokenIndex - startTokenIndex + 1;
                    Assert.AreEqual(expectedRewindCount, rewind, "The number of tokens to rewind is incorrect.");
                }

                // Perform the rewind on both our state and the banner's internal state.
                _tokenBanner.RewindBuffer(rewind);
                _textBannerInstance.RewindBuffer(rewind);
                generatedTokens.RemoveRange(generatedTokens.Count - rewind, rewind);

                // Reset the loop to the beginning of the rewound section to proceed on the alternative path.
                i -= rewind;
            }
        }

        // Final Assertions
        var finalGeneratedText = string.Concat(generatedTokens.Select(t => _model.Vocab.LLamaTokenToString(t, false)));

        Assert.AreNotEqual(int.MaxValue, rewindTriggerIndex, "The rewind trigger index should have been set.");

        // Check that the output contains the beginning of the primary path, but then switches to the alternative.
        string expectedStart = string.Concat(primaryTokens.Take(rewindTriggerIndex).Select(t => _model.Vocab.LLamaTokenToString(t, false)));
        StringAssert.StartsWith(finalGeneratedText, expectedStart, "The final text should start with the portion of the primary path before the rewind.");

        StringAssert.DoesNotMatch(finalGeneratedText, new System.Text.RegularExpressions.Regex(rewindBan), "The final text should not contain the banned sequence that triggers a rewind.");
        StringAssert.DoesNotMatch(finalGeneratedText, new System.Text.RegularExpressions.Regex(simpleBan), "The final text should not contain the banned sequence that should not need a rewind.");
    }

    [TestMethod]
    public void Accept_MultipleRewindsOnSamePosition_DoesNotLoopAndFindsThirdPath()
    {
        // Arrange
        const string bannedSeq1 = "brown fox";
        const string bannedSeq2 = "ick yellow";
        _textBannerInstance.BanTextSequences(new Dictionary<string, int> { { bannedSeq1, -1 }, { bannedSeq2, -1 } });

        // Define three paths. The first two will be banned, forcing the third.
        const string path1 = "The quick brown fox";
        const string path2 = "The quick yellow horse";
        const string path3 = "The fast red cat"; // The correct escape path
        const string expected = "The fast yellow fox";

        var tokens1 = Tokenize(path1);
        var tokens2 = Tokenize(path2);
        var tokens3 = Tokenize(path3);

        Assert.AreEqual(tokens1.Length, tokens2.Length, "Path 1 and 2 must have same token length.");
        Assert.AreEqual(tokens2.Length, tokens3.Length, "Path 2 and 3 must have same token length.");

        var generatedTokens = new List<LLamaToken>();
        int rewindCount = 0;
        var eosOrEot = _model.Vocab.EOT ?? _model.Vocab.EOS ?? _model.Vocab.Newline ?? default;

        // Act: Simulate generation token by token, always picking the highest-ranked valid choice.
        for (int i = 0; i < tokens1.Length; i++)
        {
            // At each step, the model considers generating the next token from any of the three paths.
            using var handle = CreateTokenDataArrayFromTokens([tokens1[i], tokens2[i], tokens3[i], eosOrEot], out var tokenData);

            // Apply bans from previous steps.
            _tokenBanner.Apply(ref tokenData);
            _textBannerInstance.Apply(ref tokenData);

            // Simulate the sampler picking the highest-logit (most likely) token that wasn't banned.
            // Our CreateTokenDataArrayFromTokens helper gives tokens1[i] the highest logit, then tokens2[i], etc.
            var acceptedTokenData = tokenData.Data.ToArray().OrderByDescending(d => d.Logit).First();
            var acceptedToken = acceptedTokenData.ID;

            // Accept the chosen token and update state.
            _tokenBanner.Accept(acceptedToken);
            _textBannerInstance.Accept(acceptedToken);
            generatedTokens.Add(acceptedToken);

            var rewind = _tokenBanner.TokensToRewind + _textBannerInstance.TokensToRewind;
            if (rewind > 0)
            {
                rewindCount++;

                // Perform the rewind on our state and the banner's internal state.
                _tokenBanner.RewindBuffer(rewind);
                _textBannerInstance.RewindBuffer(rewind);
                generatedTokens.RemoveRange(generatedTokens.Count - rewind, rewind);

                // Reset the loop to the beginning of the rewound section to try a different path.
                i -= rewind;
            }
        }

        // Assert
        var finalText = string.Concat(generatedTokens.Select(t => _model.Vocab.LLamaTokenToString(t, false)));

        Assert.AreEqual(2, rewindCount, "Exactly two rewinds should have occurred (one for each banned path).");
        Assert.AreEqual(expected, finalText, "The final generated text is unexpected.");
        StringAssert.DoesNotMatch(finalText, new System.Text.RegularExpressions.Regex(bannedSeq1));
        StringAssert.DoesNotMatch(finalText, new System.Text.RegularExpressions.Regex(bannedSeq2));
    }

    #endregion

    #region Helper Methods

    private static MemoryHandle CreateTokenDataArrayFromTokens(IEnumerable<LLamaToken> tokens, out LLamaTokenDataArrayNative nativeArray)
    {
        // Assign decreasing logits to simulate probability ranking.
        float logit = 0.5f;
        var tokenDataList = new List<LLamaTokenData>();
        foreach (var token in tokens.Distinct())
        {
            tokenDataList.Add(new LLamaTokenData(token, logit, 0f));
            logit -= 0.1f;
        }

        var array = new LLamaTokenDataArray(tokenDataList.ToArray(), false);
        return LLamaTokenDataArrayNative.Create(array, out nativeArray);
    }

    /// <summary>
    /// Simulates one step of the generation loop, enforcing the Apply -> Accept pattern.
    /// </summary>
    private static void SimulateGenerationStep(TextBanner banner, LLamaToken tokenToAccept, IEnumerable<LLamaToken> allPossibleTokens)
    {
        var tokenData = allPossibleTokens.Select(t => new LLamaTokenData(t, 0.5f, 0f)).ToArray();
        var array = new LLamaTokenDataArray(tokenData, false);
        using var handle = LLamaTokenDataArrayNative.Create(array, out var nativeArray);

        // 1. Apply constraints to the possible logits
        banner.Apply(ref nativeArray);

        // 2. Accept the chosen token (provided by the test)
        banner.Accept(tokenToAccept);
    }

    private static (int startTokenIndex, int endTokenIndex) FindTokenIndicesForTextSpan(LLamaToken[] tokens, int spanStartIndex, int spanEndIndex)
    {
        int currentPos = 0;
        int startToken = -1;
        int endToken = -1;

        for (int i = 0; i < tokens.Length; i++)
        {
            var tokenText = _model.Vocab.LLamaTokenToString(tokens[i], false) ?? "";
            int tokenStart = currentPos;
            int tokenEnd = currentPos + tokenText.Length;

            // Check for overlap
            if (tokenStart < spanEndIndex && tokenEnd > spanStartIndex)
            {
                if (startToken == -1)
                    startToken = i;
                endToken = i;
            }

            currentPos = tokenEnd;
        }

        if (startToken == -1)
            throw new InvalidOperationException("Could not find tokens for the specified text span.");

        return (startToken, endToken);
    }

    private void GetTextBannerInstance(LLamaWeights model)
    {
        var outerInstance = new DistributionSamplingPipelineThatStops(model, new ConfigurationBuilder().Build());

        var fieldInfo = typeof(DistributionSamplingPipelineThatStops).GetField(TextBannerFieldName, BindingFlags.NonPublic | BindingFlags.Instance)
                        ?? throw new MissingFieldException($"Field '{TextBannerFieldName}' not found in '{nameof(DistributionSamplingPipelineThatStops)}'.");
        _textBannerInstance = (TextBanner?)fieldInfo.GetValue(outerInstance) ?? throw new NullReferenceException("TextBanner instance is null.");

        var tokenBannerField = typeof(DistributionSamplingPipelineThatStops).GetField("_tokenBanner", BindingFlags.NonPublic | BindingFlags.Instance)
                            ?? throw new MissingFieldException($"Field '_tokenBanner' not found in '{nameof(DistributionSamplingPipelineThatStops)}'.");
        _tokenBanner = (TokenBanner?)tokenBannerField.GetValue(outerInstance) ?? throw new NullReferenceException("TokenBanner instance is null.");
    }

    private static (string word, int tokenId) FindSingleTokenWord(params string[] candidateWords)
    {
        foreach (var word in candidateWords)
        {
            // For finding a single-token word, we don't want special characters like the leading space.
            var tokens = _model.Tokenize(word, false, special: false, Encoding.UTF8);
            if (tokens.Length == 1)
            {
                return (word, (int)tokens[0]);
            }
        }
        throw new AssertFailedException($"Could not find a single token for any candidate words: {string.Join(", ", candidateWords)}. The test vocabulary may be unsuitable.");
    }

    private static LLamaToken[] Tokenize(string text)
    {
        // When simulating generation, we want the tokenizer to behave as it normally would,
        // often adding a leading space, so special: true is important.
        return _model.Tokenize(text, false, special: true, Encoding.UTF8);
    }

    private static MemoryHandle CreateTokenDataArray(string[] texts, out LLamaTokenDataArrayNative nativeArray)
    {
        var tokenDataList = new List<LLamaTokenData>();
        foreach (var text in texts)
        {
            var tokens = Tokenize(text);
            // It's possible a string like "He" is one token, but "lloWorld" is two.
            // For simplicity in this helper, we'll just take the first token of each string.
            // More complex scenarios should build their token arrays manually.
            if (tokens.Length > 0)
            {
                tokenDataList.Add(new LLamaTokenData(tokens[0], 0.5f, 0f));
            }
        }

        var array = new LLamaTokenDataArray(tokenDataList.ToArray(), false);
        return LLamaTokenDataArrayNative.Create(array, out nativeArray);
    }

    private static void AssertTokenLogit(string text, LLamaTokenDataArrayNative array, bool isAllowed, string? message = null)
    {
        var tokens = Tokenize(text);
        if (tokens.Length == 0) Assert.Fail($"Could not tokenize the assertion text '{text}'.");

        var tokenId = tokens[0];
        foreach (var item in array.Data)
        {
            if (item.ID == (int)tokenId)
            {
                if (isAllowed)
                    Assert.AreNotEqual(float.NegativeInfinity, item.Logit, message ?? $"Token for '{text}' should be allowed.");
                else
                    Assert.AreEqual(float.NegativeInfinity, item.Logit, message ?? $"Token for '{text}' should be banned.");
                return;
            }
        }
        // This can happen if the token simply wasn't in the input array, which is a valid test outcome.
        // If the token should have been allowed, it must be present and not -inf.
        if (isAllowed)
        {
            Assert.Fail(message ?? $"Token for text '{text}' was not found in the token data array, but it was expected to be allowed.");
        }
    }
    #endregion
}
