using LLama;
using LLama.Native;
using LLama.Sampling;

namespace Faxtract.Sampling;

/// <summary>
/// Sampling pipeline that can detect the model's stop token and temporarily ban specific tokens
/// (useful to break mode-collapse / repetition loops on small or quantized models).
/// If you use multi-token allowed or banned sequences, you must check TokensToRewind and rewind the context accordingly.
/// Example: if (pipeline.TokensToRewind > 0) { conversation.Rewind(pipeline.TokensToRewind); pipeline.Rewind(pipeline.TokensToRewind); }
/// Don't ban the EOS token, or inference can end up in an infinite loop. EOS will be automatically allowed if you have any allowed tokens/sequences.
/// </summary>
internal class DistributionSamplingPipelineThatStops : BaseSamplingPipeline
{
    private readonly StopTokenCatcher _stopTokenCatcher;
    private readonly TokenBanner _tokenBanner;
    private readonly TextBanner _textBanner;
    private readonly IConfiguration config;

    public DistributionSamplingPipelineThatStops(LLamaWeights model, IConfiguration config)
    {
        this.config = config;
        _stopTokenCatcher = new(model);
        _tokenBanner = new(model);
        _textBanner = new(model, _tokenBanner); // Had to use a real initializer just because _tokenBanner can't be referenced in the _textBanner initializer otherwise.
    }

    public bool StopTokenReceived => _stopTokenCatcher.StopTokenReceived;
    public int TokensToRewind => Math.Max(_tokenBanner.TokensToRewind, _textBanner.TokensToRewind);

    // Passthroughs to my private TokenBanner and TextBanner instances
    public void BanToken(LLamaToken token, int banDuration = 1) => _tokenBanner.BanToken(token, banDuration);
    public void BanSequence(LLamaToken[] sequence, int banDuration = 1) => _tokenBanner.BanSequence(sequence, banDuration);
    public void AddAllowedSequence(LLamaToken[] sequence, int allowDuration = 1) => _tokenBanner.AddAllowedSequence(sequence, allowDuration);
    public void ClearBannedTokens() => _tokenBanner.ClearBannedTokens();
    public void ClearBannedTokenSequences() => _tokenBanner.ClearBannedSequences();
    public void ClearAllowedTokenSequences() => _tokenBanner.ClearAllowedSequences();

    public void BanTextSequence(string sequence, int ruleDuration = -1, int tokenBanDuration = 3, BannedSequenceBanType banType = BannedSequenceBanType.FirstToken) => _textBanner.BanTextSequence(sequence, ruleDuration, tokenBanDuration, banType);
    public void BanTextSequences(IEnumerable<KeyValuePair<string, int>> sequences) => _textBanner.BanTextSequences(sequences);
    public void AddAllowedTextSequence(string sequence, int ruleDuration = -1) => _textBanner.AddAllowedTextSequence(sequence, ruleDuration);
    public void AddAllowedTextSequences(IEnumerable<KeyValuePair<string, int>> sequences) => _textBanner.AddAllowedTextSequences(sequences);
    public void ClearBannedTextSequences() => _textBanner.ClearBannedSequences();
    public void ClearAllowedTextSequences() => _textBanner.ClearAllowedSequences();

    public void Rewind(int tokens)
    {
        _tokenBanner.RewindBuffer(tokens);
        _textBanner.RewindBuffer(tokens);
    }

    public Grammar? Grammar { get; init; }

    protected override SafeLLamaSamplerChainHandle CreateChain(SafeLLamaContextHandle context)
    {
        var chain = SafeLLamaSamplerChainHandle.Create(LLamaSamplerChainParams.Default());
        if (Grammar != null)
        {
            chain.AddGrammar(context.ModelHandle, Grammar.Gbnf, Grammar.Root);
        }

        chain.AddCustom(_tokenBanner);
        chain.AddCustom(_textBanner);
        if (config.GetValue("Temperature", 0.6f) > 0)
        {
            chain.AddTopK(config.GetValue("TopK", 40));
            chain.AddTypical(1f, 1);
            chain.AddTopP(config.GetValue("TopP", 0.9f), 1);
            chain.AddMinP(config.GetValue("MinP", 0.1f), 1);

            chain.AddTemperature(config.GetValue("Temperature", 0.6f));
            chain.AddDistributionSampler((uint)Random.Shared.Next());
        }
        else
        {
            chain.AddGreedySampler();
        }

        chain.AddCustom(_stopTokenCatcher);
        return chain;
    }
}
