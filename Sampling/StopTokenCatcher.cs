using LLama;
using LLama.Native;

namespace Faxtract.Sampling;

public class StopTokenCatcher(LLamaWeights model) : ICustomSampler
{
    public bool StopTokenReceived;

    public string Name => nameof(StopTokenCatcher);

    public void Accept(LLamaToken token)
    {
        StopTokenReceived = token == model.Vocab.EOS || token == model.Vocab.EOT; //Note: EOT is supposed to be used for the user's message, not the assistant's, but they often match, and it's surely possible for a model to infer it.
    }

    public void Apply(ref LLamaTokenDataArrayNative tokenData)
    {
    }

    public ICustomSampler Clone() => new StopTokenCatcher(model);

    public void Dispose()
    {
    }

    public void Reset()
    {
        StopTokenReceived = false;
    }
}
