using Faxtract.Interfaces;
using LLama;
using LLama.Native;

namespace Faxtract.Services;

public class MistralSmall32Template(LLamaWeights model, Func<string, bool, LLamaToken[]> tokenizer) : IChatTemplate
{
    private const string SYSTEM_START = "[SYSTEM_PROMPT]";
    private const string SYSTEM_END = "[/SYSTEM_PROMPT]";
    private const string INST_START = "[INST]";
    private const string INST_END = "[/INST]";
    private const string BOS = "<s>";
    private const string EOS = "</s>";

    public LLamaToken[] Apply(string role, string message, bool includeBefore = true, bool includeAfter = true)
    {
        return role.ToLower() switch
        {
            "system" => FormatSystemMessage(message, includeBefore, includeAfter),
            "user" => FormatUserMessage(message, includeBefore, includeAfter),
            "assistant" => FormatAssistantMessage(message, includeAfter),
            _ => throw new ArgumentException($"Unknown role: {role}")
        };
    }

    public LLamaToken[] GetConversationStart()
    {
        var tokens = new List<LLamaToken>();

        // Add BOS token if requested
        if (model.Vocab.BOS.HasValue) tokens.Add(model.Vocab.BOS.Value); else tokens.AddRange(tokenizer(BOS, true));

        return [.. tokens];
    }

    private LLamaToken[] FormatSystemMessage(string message, bool includeBefore, bool includeAfter)
    {
        var tokens = new List<LLamaToken>();

        if (includeBefore) tokens.AddRange(GetSpecialTokens(SYSTEM_START));

        tokens.AddRange(tokenizer(message, false));

        if (includeAfter) tokens.AddRange(GetSpecialTokens(SYSTEM_END));

        return [.. tokens];
    }

    private LLamaToken[] FormatUserMessage(string message, bool includeBefore, bool includeAfter)
    {
        var tokens = new List<LLamaToken>();

        if (includeBefore) tokens.AddRange(GetSpecialTokens(INST_START));

        tokens.AddRange(tokenizer(message, false));

        if (includeAfter) tokens.AddRange(GetSpecialTokens(INST_END));

        return [.. tokens];
    }

    private LLamaToken[] FormatAssistantMessage(string message, bool includeAfter)
    {
        var tokens = new List<LLamaToken>();

        // Assistant messages come directly after [/INST] with no extra spacing
        tokens.AddRange(tokenizer(message, false));

        if (includeAfter) tokens.AddRange(GetSpecialTokens(EOS));

        return [.. tokens];
    }

    private LLamaToken[] GetSpecialTokens(string specialToken)
    {
        // Use the tokenizer with special=true to get proper special token handling
        return tokenizer(specialToken, true);
    }
}