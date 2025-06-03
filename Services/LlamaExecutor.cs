using LLama;
using LLama.Batched;
using LLama.Common;
using LLama.Native;
using System.Diagnostics.CodeAnalysis;
using System.Text;

namespace Faxtract.Services;

public class LlamaExecutor : IDisposable
{
    private static LLamaWeights? _model;
    private static BatchedExecutor? _executor;
    private static readonly object _modelLock = new();
    private readonly ModelParams _parameters;
    private readonly string _prePromptFile;
    private readonly string _prePromptText;
    private readonly IConfigurationSection _config;
    private readonly ILogger<LlamaExecutor> _logger;
    private readonly string _extraContextPrefix;
    private readonly string _extraContextSuffix;
    private int _prePromptTokenCount; // Track pre-prompt token count

    // Event for progress reporting
    public event Action<BatchProgress>? ProgressChanged;

    private record InferenceItem(
        StringBuilder ResponseBuilder,
        DistributionSamplingPipelineThatStops Sampler,
        StreamingTokenDecoder Decoder,
        Conversation Conversation,
        int OriginalIndex,
        string LastTokenText = "",
        int TokensGenerated = 0,
        int PromptTokens = 0,
        int GroupExtraContextTokens = 0,
        int GroupID = 0,
        bool IsCompleted = false,
        HashSet<string>? LineHistory = null);

    public record BatchProgress(int ContextMaxTokens, int UsedTokens, int NewTokens, IReadOnlyList<string> CurrentResponses, bool[] CompletedMask, IReadOnlyList<int> TokensPerResponse);

    public BatchedExecutor Executor => _executor ?? throw new InvalidOperationException("Executor not initialized");

    public LlamaExecutor(IConfiguration configuration, ILogger<LlamaExecutor> logger)
    {
        _config = configuration.GetSection("LLamaConfig");
        _logger = logger;

        _parameters = new ModelParams(_config["ModelPath"] ?? throw new Exception("No GGUF file specified. Set ModelPath in appsettings.json."))
        {
            //Context: pre-prompt size (105 currently) plus a few tokens for the chat template plus WorkBatchSize times enough for one input chunk and response.
            ContextSize = _config.GetValue("ContextSize", 160 + _config.GetValue<uint>("MaxTokens", 1024) * (uint)_config.GetValue("WorkBatchSize", 4)),
            BatchSize = _config.GetValue("BatchSize", (uint)4096),
            Threads = _config.GetValue("Threads", Environment.ProcessorCount),
            GpuLayerCount = _config.GetValue("GpuLayerCount", 0),
            FlashAttention = true,
            TensorBufferOverrides = [.. _config.GetValue("TensorBufferOverrides", string.Empty)!.Split(';', StringSplitOptions.RemoveEmptyEntries)
                .Select(p => p.Split('=', StringSplitOptions.RemoveEmptyEntries))
                .Select(p => new LLama.Abstractions.TensorBufferOverride(p[0], p[1]))],
            TypeK = Enum.Parse<GGMLType>(_config.GetValue("TypeK", nameof(GGMLType.GGML_TYPE_Q8_0))!, true), //Note: Other than both Q8_0 or F16, these options generally just crash llama.cpp. Tried F16/Q8_0 and Q8_0/Q4_0, for example, and those both crash. So it pretty much has to be F16/F16 or Q8_0/Q8_0.
            TypeV = Enum.Parse<GGMLType>(_config.GetValue("TypeV", nameof(GGMLType.GGML_TYPE_Q8_0))!, true),
        };

        _prePromptFile = Path.Join(AppContext.BaseDirectory, _config["PrePromptFile"] ?? "preprompt.state");
        _prePromptText = _config["PrePromptText"] ?? string.Empty;
        _extraContextPrefix = _config["ExtraContextPrefix"] ?? "(Context for the flash cards: ";
        _extraContextSuffix = _config["ExtraContextSuffix"] ?? ")\n";

        InitializeExecutor();
    }

    [MemberNotNull(nameof(_executor))]
    [MemberNotNull(nameof(_model))]
    private void InitializeExecutor()
    {
        lock (_modelLock)
        {
            if (_executor != null && _model != null)
                return;

            // Load model if not already loaded
            _model ??= LLamaWeights.LoadFromFile(_parameters);

            // Create the batched executor
            _executor = new BatchedExecutor(_model, _parameters);

            InitializeState();
        }
    }

    private void InitializeState()
    {
        var tokenCountFile = _prePromptFile + ".tokencount";

        if (!File.Exists(_prePromptFile))
        {
            // Create initial conversation with pre-prompt as the system message
            var conversation = _executor!.Create();
            var template = new LLamaTemplate(_model!);
            template.Add("system", _prePromptText);
            var prePromptBytes = template.Apply();
            var prePromptText = Encoding.UTF8.GetString(prePromptBytes);
            var prePromptTokens = _executor.Context.Tokenize(prePromptText);
            _prePromptTokenCount = prePromptTokens.Length; // Store token count

            conversation.Prompt(prePromptTokens);

            // Run inference to process pre-prompt
            _executor.Infer().Wait();

            // Save state and token count
            conversation.Save(_prePromptFile);
            File.WriteAllText(tokenCountFile, _prePromptTokenCount.ToString());
            conversation.Dispose();
        }
        else if (File.Exists(tokenCountFile))
        {
            // Load the token count if available
            if (int.TryParse(File.ReadAllText(tokenCountFile), out var count))
                _prePromptTokenCount = count;
            else
                _prePromptTokenCount = 0;
        }
        else
        {
            // If no token count file, set to 0 as fallback
            _prePromptTokenCount = 0;
            // Save estimated count for future use
            File.WriteAllText(tokenCountFile, _prePromptTokenCount.ToString());
        }
    }

    public Conversation CreateConversation()
    {
        return _executor!.Load(_prePromptFile);
    }

    public async Task<List<string>> GenerateResponses(List<string> prompts, List<string?> extraContexts, int contextMaxTokens = 0, CancellationToken cancellationToken = default)
    {
        if (contextMaxTokens <= 0)
            contextMaxTokens = (int)_parameters.ContextSize!;

        // Create decoders for each conversation
        var inferenceItems = new List<InferenceItem>();

        try
        {
            // Group prompts by extra context to optimize forking
            var groupedPrompts = GroupPromptsByContext(prompts, extraContexts);

            // Process each context group and prepare conversations
            var userMessageTemplateSuffix = await ProcessContextGroups(inferenceItems, groupedPrompts, cancellationToken);

            // Sort items by their original index to ensure they're processed in the correct order (not super important, but makes the UI more predictable)
            inferenceItems.Sort((a, b) => a.OriginalIndex.CompareTo(b.OriginalIndex));

            // Submit all prompts
            int contextConsumed = PromptConversations(prompts, extraContexts, inferenceItems, userMessageTemplateSuffix);

            // Main inference loop
            contextConsumed = await RunBatchInference(contextMaxTokens, inferenceItems, contextConsumed, cancellationToken);

            // Update once more so the UI doesn't repeat the last token
            ProgressChanged?.Invoke(new BatchProgress(
                    contextMaxTokens,
                    contextConsumed,
                    0,
                    new string[prompts.Count],
                    [.. inferenceItems.Select(item => item.IsCompleted)],
                    [.. inferenceItems.Select(item => item.TokensGenerated)]
                ));

            // Collect final responses in original order
            return [.. inferenceItems
                .OrderBy(item => item.OriginalIndex)
                .Select(item => item.ResponseBuilder.ToString())];
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error during LlamaExecutor processing: {Message}", ex.Message);
            throw;
        }
        finally
        {
            // Cleanup conversations
            foreach (var item in inferenceItems)
            {
                if (!item.Conversation.IsDisposed)
                    item.Conversation.Dispose();
            }
        }
    }

    private async Task<string?> ProcessContextGroups(
        List<InferenceItem> inferenceItems,
        Dictionary<string, List<(int Index, string Prompt)>> groupedPrompts,
        CancellationToken cancellationToken)
    {
        // Initialize base conversation from the saved state
        var baseConversation = CreateConversation();

        // Dictionaries to track forked conversations with their extra-context key and how many tokens they consume
        var groups = new Dictionary<int, (Conversation Conversation, int ContextTokens)>();

        string? userMessageTemplateSuffix = null;
        var groupIndex = 0;
        foreach (var group in groupedPrompts)
        {
            string contextKey = group.Key;
            Conversation sourceConversation;
            int contextConsumed = 0;

            if (string.IsNullOrEmpty(contextKey))
            {
                // Use the base conversation directly for empty context
                sourceConversation = baseConversation;
            }
            else if (groups.TryGetValue(groupIndex, out var existingConversation))
            {
                // Reuse existing conversation with same extra context
                sourceConversation = existingConversation.Conversation;
                contextConsumed = existingConversation.ContextTokens;
            }
            else
            {
                // Create a new conversation with this extra context by forking from the base
                var conversation = baseConversation.Fork();

                // Prepare a template with just the extra context
                var template = new LLamaTemplate(_model!);
                template.Add("user", _extraContextPrefix + contextKey + _extraContextSuffix);
                template.AddAssistant = true;

                // We DON'T want to add whatever portion of the template is after the prompt because this is an incomplete message
                // We'll get the full template string to extract the suffix part
                var extraContextString = Encoding.UTF8.GetString(template.Apply());

                // Find where the actual user message ends to extract the template suffix
                int messageEndPos = extraContextString.LastIndexOf(_extraContextSuffix) + _extraContextSuffix.Length;
                if (messageEndPos > 0 && messageEndPos < extraContextString.Length && userMessageTemplateSuffix == null)
                {
                    userMessageTemplateSuffix = extraContextString[messageEndPos..]; // Expected to be identical for all contexts
                }

                // Prompt the conversation with just the extra context part
                var extraContextTokens = _executor!.Context.Tokenize(
                    extraContextString[..messageEndPos]);
                conversation.Prompt(extraContextTokens);

                // Store the conversation for reuse
                groups[groupIndex] = (conversation, extraContextTokens.Length);
                sourceConversation = conversation;
                contextConsumed = extraContextTokens.Length;
            }

            // Process all prompts in this context group
            foreach (var (index, _) in group.Value)
            {
                // For the first prompt in each group, use the source conversation
                // For subsequent prompts, fork from the source
                var conversation = group.Value.Count > 1 && index != group.Value[0].Index
                    ? sourceConversation.Fork()
                    : sourceConversation;

                // Create and add a new InferenceItem with all required components
                inferenceItems.Add(new InferenceItem(
                    ResponseBuilder: new StringBuilder(),
                    Sampler: new DistributionSamplingPipelineThatStops(_model!, _config),
                    Decoder: new StreamingTokenDecoder(_executor!.Context),
                    Conversation: conversation,
                    OriginalIndex: index,
                    PromptTokens: 0,
                    GroupExtraContextTokens: string.IsNullOrEmpty(contextKey) ? 0 : contextConsumed,
                    GroupID: groupIndex
                ));
            }

            groupIndex++;
        }

        // Must run inference if we called Prompt on any conversation since they'll ALL be prompted again
        while (groups.Any(p => p.Value.Conversation.RequiresInference))
            await _executor!.Infer(cancellationToken);

        return userMessageTemplateSuffix;
    }

    private async Task<int> RunBatchInference(
        int contextMaxTokens,
        List<InferenceItem> inferenceItems,
        int contextConsumed,
        CancellationToken cancellationToken)
    {
        // Main inference loop
        var newTokens = 1;
        while (contextConsumed < contextMaxTokens && inferenceItems.Any(item => !item.IsCompleted) && newTokens > 0)
        {
            var result = await _executor!.Infer(cancellationToken);
            if (result != DecodeResult.Ok)
            {
                _logger.LogError("Batch inference failed after {ContextConsumed} tokens with result: {Result}; dropping 1 conversation", contextConsumed, result);

                // Find the last active conversation to dispose
                var disposeItemIndex = inferenceItems.FindLastIndex(item => !item.Conversation.IsDisposed && !item.IsCompleted);
                if (disposeItemIndex >= 0)
                {
                    contextConsumed = EndConversation(inferenceItems, contextConsumed, disposeItemIndex, inferenceItems[disposeItemIndex]);
                }

                continue;
            }

            // Sample and decode tokens for each active conversation
            newTokens = 0;
            for (var i = 0; i < inferenceItems.Count; i++)
            {
                var item = inferenceItems[i];
                if (item.IsCompleted || item.Conversation.RequiresInference) continue;

                newTokens++;
                var updatedItem = item with { TokensGenerated = item.TokensGenerated + 1 };

                var token = updatedItem.Sampler.Sample(_executor!.Context.NativeHandle, updatedItem.Conversation.GetSampleIndex());

                // Check for end of sequence
                if (token.IsEndOfGeneration(_model!.Vocab))
                {
                    contextConsumed = EndConversation(inferenceItems, contextConsumed, i, updatedItem);
                    continue;
                }

                updatedItem.Decoder.Add(token);
                updatedItem.Conversation.Prompt(token);
                var tokenText = updatedItem.Decoder.Read();

                //Certain models often get stuck repeating the exact same token, so temporarily ban any token that's been output twice in a row.
                if (tokenText == updatedItem.LastTokenText)
                    updatedItem.Sampler.BanToken(token, 3);

                // Check for duplicate lines when we encounter a newline character
                if (tokenText.Contains('\n'))
                {
                    // Get the current text and find the last line
                    // Get the last line from the StringBuilder
                    string lastLine = GetLastLine(updatedItem.ResponseBuilder);

                    if (updatedItem.LineHistory == null) updatedItem = updatedItem with { LineHistory = new HashSet<string>(StringComparer.OrdinalIgnoreCase) };

                    // Check if this line is a duplicate and not empty
                    if (!string.IsNullOrWhiteSpace(lastLine) && lastLine.Length > 2 && !updatedItem.LineHistory.Add(lastLine))
                    {
                        _logger.LogWarning("Conversation terminated due to repeating itself: {Line}", lastLine);

                        // This is a duplicate line - remove it from the StringBuilder
                        RemoveLastLine(updatedItem.ResponseBuilder);

                        // It's most likely stuck repeating itself, as smaller models especially tend to do, so just stop it here.
                        contextConsumed = EndConversation(inferenceItems, contextConsumed, i, updatedItem);
                        continue;
                    }
                }

                updatedItem.ResponseBuilder.Append(tokenText);
                inferenceItems[i] = updatedItem with { LastTokenText = tokenText };

                //TODO: Consider banning # and * (or ** if that's its own token) for one or two tokens at the start of each line since I'm using my TokenBanner sampler, thereby preventing dumber models from outputting "**Q:**" or "### Here are your flash cards" or whatever.
            }

            // Report progress
            ProgressChanged?.Invoke(new BatchProgress(
                contextMaxTokens,
                contextConsumed,
                newTokens,
                [.. inferenceItems.Select(item => item.LastTokenText)],
                [.. inferenceItems.Select(item => item.IsCompleted)],
                [.. inferenceItems.Select(item => item.TokensGenerated)]
            ));

            contextConsumed += newTokens;
        }

        return contextConsumed;
    }

    // Helper methods for string operations on StringBuilder
    private static string GetLastLine(StringBuilder sb)
    {
        int lastNewlineIndex = -1;
        for (int j = sb.Length - 1; j >= 0; j--)
        {
            if (sb[j] == '\n')
            {
                lastNewlineIndex = j;
                break;
            }
        }

        if (lastNewlineIndex == -1)
        {
            // No newline found, return entire content
            return sb.ToString().Trim();
        }
        else
        {
            // Return text after the last newline
            return sb.ToString(lastNewlineIndex + 1, sb.Length - lastNewlineIndex - 1).Trim();
        }
    }

    private static void RemoveLastLine(StringBuilder sb)
    {
        int lastNewlineIndex = -1;
        for (int j = sb.Length - 1; j >= 0; j--)
        {
            if (sb[j] == '\n')
            {
                lastNewlineIndex = j;
                break;
            }
        }

        if (lastNewlineIndex == -1)
        {
            // No newline found, clear the entire StringBuilder
            sb.Clear();
        }
        else
        {
            // Remove everything after the last newline
            sb.Length = lastNewlineIndex + 1;
        }
    }

    private static int EndConversation(List<InferenceItem> inferenceItems, int contextConsumed, int disposeItemIndex, InferenceItem itemToDispose)
    {
        // Free up some KV cache for the remaining conversations and wastes less time on inference for the dead conversation.
        //TODO: Can't find the source of the bug, not here and not in LlamaSharp, but it seems like the prompt tokens aren't being freed from the KV cache when we dispose the conversation. Might actually be a bug in llama.cpp, as it reports the KV cache being reduced, but then we run into NoKvSlot too soon.
        itemToDispose.Conversation.Dispose();

        // Subtract this conversation's tokens from contextConsumed
        contextConsumed -= itemToDispose.TokensGenerated + itemToDispose.PromptTokens;

        // Only remove the group extra tokens for the LAST item sharing that group (aka. extra context), not all of them.
        if (!inferenceItems.Any(p => p.GroupID == itemToDispose.GroupID && !p.IsCompleted && p != itemToDispose))
            contextConsumed -= itemToDispose.GroupExtraContextTokens;

        // Mark as completed
        inferenceItems[disposeItemIndex] = itemToDispose with
        {
            IsCompleted = true,
            LastTokenText = "",
            GroupExtraContextTokens = 0
        };
        return contextConsumed;
    }

    private int PromptConversations(
        List<string> prompts,
        List<string?> extraContexts,
        List<InferenceItem> inferenceItems,
        string? userMessageTemplateSuffix)
    {
        int contextConsumed = _prePromptTokenCount + inferenceItems.DistinctBy(p => p.GroupID).Sum(p => p.GroupExtraContextTokens);

        for (var i = 0; i < inferenceItems.Count; i++)
        {
            var originalIndex = inferenceItems[i].OriginalIndex;
            string prompt = prompts[originalIndex];
            string? extraContext = extraContexts[originalIndex];

            if (string.IsNullOrEmpty(extraContext))
            {
                // No extra context case - use standard template
                var template = new LLamaTemplate(_model!);
                template.Add("user", prompt);
                template.AddAssistant = true;

                var promptText = Encoding.UTF8.GetString(template.Apply());
                var promptTokens = _executor!.Context.Tokenize(promptText);
                inferenceItems[i] = inferenceItems[i] with { PromptTokens = promptTokens.Length };
                contextConsumed += promptTokens.Length;
                inferenceItems[i].Conversation.Prompt(promptTokens);
            }
            else
            {
                // Just add the prompt text and template suffix (no need for another template.Apply, as we did that when adding the extra context)
                string completePrompt = prompt + (userMessageTemplateSuffix ?? "");
                var promptTokens = _executor!.Context.Tokenize(completePrompt);

                inferenceItems[i] = inferenceItems[i] with { PromptTokens = promptTokens.Length };
                contextConsumed += promptTokens.Length;
                inferenceItems[i].Conversation.Prompt(promptTokens);
            }
        }

        return contextConsumed;
    }

    private static Dictionary<string, List<(int Index, string Prompt)>> GroupPromptsByContext(List<string> prompts, List<string?> extraContexts)
    {
        var groupedPrompts = new Dictionary<string, List<(int Index, string Prompt)>>();

        // Process extraContexts and group prompts
        for (int i = 0; i < prompts.Count; i++)
        {
            string contextKey = extraContexts[i] ?? string.Empty;
            if (!groupedPrompts.TryGetValue(contextKey, out var promptGroup))
            {
                groupedPrompts[contextKey] = promptGroup = [];
            }

            promptGroup.Add((i, prompts[i]));
        }

        return groupedPrompts;
    }

    public void Dispose()
    {
        // Only dispose local resources - static resources persist for the application lifetime
        GC.SuppressFinalize(this);
    }
}