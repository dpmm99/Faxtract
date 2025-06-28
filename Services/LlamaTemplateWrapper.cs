using Faxtract.Interfaces;
using LLama;
using LLama.Native;
using System.Text;

namespace Faxtract.Services;

/// <summary>
/// This generally works for models that aren't brand-new, as it relies on the templates that are hard-coded in llama.cpp.
/// </summary>
/// <param name="tokenizer">Should always be (text, isSpecial) => executor.Context.Tokenize(text, false, isSpecial).
/// The 'false' is because we add the BOS token via GetConversationStart instead.</param>
public class LlamaTemplateWrapper(LLamaWeights model, Func<string, bool, LLamaToken[]> tokenizer) : IChatTemplate
{
    public LLamaToken[] Apply(string role, string message, bool includeBefore = true, bool includeAfter = true)
    {
        var template = new LLamaTemplate(model);
        template.Add(role, message);
        template.AddAssistant = role == "user" && includeAfter;

        var bytes = template.Apply();
        var text = Encoding.UTF8.GetString(bytes);

        return FilterTemplateText(text, message, includeBefore, includeAfter);
    }

    public LLamaToken[] GetConversationStart()
    {
        var template = new LLamaTemplate(model);
        var bytes = template.Apply();
        if (bytes.Length == 0 && model.Vocab.BOS.HasValue) return [model.Vocab.BOS.Value];
        var text = Encoding.UTF8.GetString(bytes);
        return tokenizer(text, true);
    }

    private LLamaToken[] FilterTemplateText(string fullText, string message, bool includeBefore, bool includeAfter)
    {
        if (!includeBefore && !includeAfter) return tokenizer(message, false);

        // Find the message in the template text. Use special tokenization for the before and after parts but non-special for the message itself.
        int messageStart = fullText.IndexOf(message);
        if (messageStart == -1 || (includeBefore && includeAfter)) return [.. tokenizer(fullText[messageStart..], true), .. tokenizer(message, false), .. tokenizer(fullText[(messageStart + message.Length)..], true)];
        if (!includeBefore) return [.. tokenizer(message, false), .. tokenizer(fullText[(messageStart + message.Length)..], true)];
        /*if (!includeAfter)*/ return [.. tokenizer(fullText[messageStart..], true), .. tokenizer(message, false)];
    }
}
