using Faxtract.Interfaces;
using Faxtract.Models;
using Faxtract.Sampling;
using LLama;
using LLama.Batched;
using LLama.Common;
using LLama.Native;
using Microsoft.VisualBasic;
using System.Collections.Concurrent;
using System.Diagnostics.CodeAnalysis;
using System.Text;

namespace Faxtract.Services;

public class LlamaExecutor : IDisposable
{
    // Configuration and state fields
    private static LLamaWeights? _model;
    private static BatchedExecutor? _executor;
    private static readonly object _modelLock = new();
    private readonly ModelParams _parameters;
    private readonly IConfigurationSection _config;
    private readonly ILogger<LlamaExecutor> _logger;

    // Continuous batching toggle and settings
    private readonly bool _allowContinuousBatching;
    private readonly int _maxParallelConversations;
    private bool _didWork;

    // Pre-prompt and context settings
    private readonly string _prePromptFile;
    private readonly string _prePromptText;
    private readonly string _extraContextPrefix;
    private readonly string _extraContextSuffix;
    private int _prePromptTokenCount;

    // Continuous batching state
    private readonly ConcurrentQueue<InferenceRequest> _pendingWork = new();
    private readonly ConcurrentDictionary<int, InferenceItem> _activeItems = new();
    private readonly ConcurrentDictionary<int, InferenceItem> _recentlyCompletedItems = new();
    private readonly Dictionary<string, SharedContextGroup> _sharedContextGroups = [];
    private readonly Task? _processingLoopTask;
    private readonly CancellationTokenSource? _loopCts;
    private int _currentContextTokens;

    public event Action<BatchProgress>? ProgressChanged;

    #region Inner Classes for State Management
    private class SharedContextGroup(Conversation conversation, int tokenCount)
    {
        public Conversation Conversation { get; set; } = conversation;
        public int TokenCount { get; set; } = tokenCount;
        public int ReferenceCount { get; set; } = 0;

        /// <summary>
        /// Indicates whether the initial prompt for this shared context has been
        /// processed by an 'Infer' call.
        /// </summary>
        public bool IsProcessed { get; set; } // Always starts as not processed.
    }

    private enum InferenceItemState
    {
        /// <summary>
        /// A new shared context has been created for this item.
        /// The shared context's prompt has been added to the batch, but not yet processed by an 'Infer' call.
        /// The item's specific prompt has not been added yet.
        /// </summary>
        AwaitingSharedContextProcessing,

        /// <summary>
        /// The item's specific prompt has been added to the batch, but not yet processed by an 'Infer' call.
        /// </summary>
        AwaitingPromptProcessing,

        /// <summary>
        /// The item's prompt has been processed, and it's now actively generating tokens.
        /// It is ready for sampling after each 'Infer' call.
        /// </summary>
        Generating
    }

    private class InferenceItem(InferenceRequest request, SharedContextGroup sharedContext, LLamaToken[] promptTokens, StreamingTokenDecoder decoder, DistributionSamplingPipelineThatStops sampler, InferenceItemState initialState)
    {
        // Existing properties
        public StringBuilder ResponseBuilder { get; } = new();
        public DistributionSamplingPipelineThatStops Sampler { get; } = sampler;
        public StreamingTokenDecoder Decoder { get; } = decoder;
        public Queue<LLamaToken> RecentTokens { get; } = new(21);
        public string LastTokenText { get; set; } = "";
        public int TokensGenerated { get; set; }
        public SharedContextGroup SharedContext { get; } = sharedContext;
        public InferenceRequest Request { get; } = request;
        public bool IsInThinkingMode { get; set; }
        public Dictionary<string, int>? LineHistory { get; set; }
        public List<int> GeneratedTokenLengths { get; } = []; //Isn't strictly necessary; can be determined from RecentTokens. But then RecentTokens needs to be long enough for all rewinds.

        /// <summary>
        /// The current state of the inference item in the batching lifecycle.
        /// </summary>
        public InferenceItemState State { get; set; } = initialState;

        /// <summary>
        /// The conversation for this specific request. This is only assigned after the
        /// shared context is processed and the conversation can be forked.
        /// </summary>
        public Conversation? Conversation { get; set; }

        /// <summary>
        /// The tokenized prompt for this specific request, held temporarily until the
        /// shared context is processed and the conversation can be forked.
        /// </summary>
        public LLamaToken[] PromptTokens { get; } = promptTokens;

        public int LastRewindCharCount { get; set; }
    }
    #endregion

    public record ResponseInfo(string Text, int RewindChars = 0);
    public record BatchProgress(int ContextMaxTokens, int UsedTokens, int NewTokens, IReadOnlyDictionary<int, ResponseInfo> CurrentResponses, IReadOnlySet<int> CompletedMask, IReadOnlyDictionary<int, int> TokensPerResponse);

    public BatchedExecutor Executor => _executor ?? throw new InvalidOperationException("Executor not initialized");

    public LlamaExecutor(IConfiguration configuration, ILogger<LlamaExecutor> logger)
    {
        _config = configuration.GetSection("LLamaConfig");
        _logger = logger;

        _allowContinuousBatching = _config.GetValue("AllowContinuousBatching", false);
        _maxParallelConversations = _config.GetValue("WorkBatchSize", 4);

        _parameters = new ModelParams(_config["ModelPath"] ?? throw new Exception("No GGUF file specified."))
        {
            //Context: pre-prompt size (105 currently) plus a few tokens for the chat template plus WorkBatchSize times enough for one input chunk and response.
            ContextSize = _config.GetValue("ContextSize", 160 + _config.GetValue<uint>("MaxTokens", 1024) * (uint)_config.GetValue("WorkBatchSize", 4)),
            BatchSize = _config.GetValue("BatchSize", (uint)4096),
            Threads = _config.GetValue("Threads", Environment.ProcessorCount),
            GpuLayerCount = _config.GetValue("GpuLayerCount", 0),
            //FlashAttention = true,
            TensorBufferOverrides = [.. _config.GetValue("TensorBufferOverrides", string.Empty)!.Split(';', StringSplitOptions.RemoveEmptyEntries)
                .Select(p => p.Split('=', StringSplitOptions.RemoveEmptyEntries))
                .Select(p => new LLama.Abstractions.TensorBufferOverride(p[0], p[1]))],
            TensorSplits = new LLama.Abstractions.TensorSplitsCollection([..
                _config.GetValue("TensorSplits", string.Empty)!.Split(',', StringSplitOptions.RemoveEmptyEntries).Select(float.Parse)]),
            //TODO: UseJinja = _config.GetValue("UseTemplateFromGguf", false), //But this isn't in LlamaSharp yet.
            TypeK = Enum.Parse<GGMLType>(_config.GetValue("TypeK", nameof(GGMLType.GGML_TYPE_F16))!, true), //Note: Other than both Q8_0 or F16, these options generally just crash llama.cpp. Tried F16/Q8_0 and Q8_0/Q4_0, for example, and those both crash. So it pretty much has to be F16/F16 or Q8_0/Q8_0.
            TypeV = Enum.Parse<GGMLType>(_config.GetValue("TypeV", nameof(GGMLType.GGML_TYPE_F16))!, true),
        };

        _prePromptFile = Path.Join(AppContext.BaseDirectory, _config["PrePromptFile"] ?? "preprompt.state");
        _prePromptText = _config["PrePromptText"] ?? string.Empty;
        _extraContextPrefix = _config["ExtraContextPrefix"] ?? "(Context for the flash cards: ";
        _extraContextSuffix = _config["ExtraContextSuffix"] ?? ")\n";

        InitializeExecutor();

        _logger.LogInformation("LlamaExecutor starting in Continuous Batching mode.");
        _loopCts = new CancellationTokenSource();
        _processingLoopTask = Task.Run(() => ProcessingLoop(_loopCts.Token));
    }

    /// <summary>
    /// Enqueues a collection of inference requests into the continuous batching queue.
    /// This method returns immediately and does not wait for the requests to be processed.
    /// The caller is responsible for tracking completion via the TaskCompletionSource on each request.
    /// </summary>
    public void EnqueueRequests(IEnumerable<InferenceRequest> requests)
    {
        foreach (var request in requests)
        {
            _pendingWork.Enqueue(request);
        }
    }

    private async Task ProcessingLoop(CancellationToken stoppingToken)
    {
        _currentContextTokens = _prePromptTokenCount;

        while (!stoppingToken.IsCancellationRequested)
        {
            if (_allowContinuousBatching || _activeItems.IsEmpty)
            {
                // Even in non-continuous batching mode, llama.cpp crashes, running out of KV cache.
                if (!_allowContinuousBatching && _didWork) { _executor?.Dispose(); _executor = null; InitializeExecutor(); _didWork = false; }

                // 1. Add new work if there's space
                while (_activeItems.Count < _maxParallelConversations && _pendingWork.TryPeek(out var request))
                {
                    if (TryCreateInferenceItem(request) is not { } newItem)
                    {
                        // Not enough context space; stop trying to add more for now.
                        break;
                    }

                    _pendingWork.TryDequeue(out _); //We used TryPeek to decide if we're *allowed* to do more work, so dequeue now. The alternative (dequeue and re-enqueue) would get the queue out of order, which is detrimental to performance when different requests have different extraContext.
                    _activeItems[request.Id] = newItem;
                    _logger.LogDebug("Added request {Id} to active batch with state {State}. Active count: {Count}", request.Id, newItem.State, _activeItems.Count);
                    _didWork = true;
                }
            }

            if (_activeItems.IsEmpty)
            {
                await Task.Delay(100, stoppingToken);
                continue;
            }

            // 2. Run one inference step for the entire active batch
            // This processes all staged prompts (both shared ones that need forked afterward and specific conversations).
            var result = await _executor!.Infer(stoppingToken);
            if (result != DecodeResult.Ok)
            {
                _logger.LogError("Batch inference failed after {ContextConsumed} tokens with result: {Result}; dropping 1 conversation", _currentContextTokens, result);
                // Simple recovery: find and remove a failing conversation. A real implementation might need more robust error handling.
                var problematicItem = _activeItems.Values.FirstOrDefault(i => i.Conversation?.RequiresInference == true);
                if (problematicItem != null) HandleCompletedItem(problematicItem, "Inference Failed");
                continue;
            }

            // 3. Process results and transition states for each item in the batch
            int newTokensInStep = ProcessInferenceStep();

            // 4. Report progress
            ReportProgress(newTokensInStep);
            _currentContextTokens += newTokensInStep;

            if (newTokensInStep == 0 && _activeItems.Values.All(i => i.Conversation?.RequiresInference == false))
            {
                // This can happen if all conversations finish in the same step.
                // The completion handling inside ProcessInferenceStep already cleaned them up.
                // We add a small delay to prevent a tight loop if no new work is coming in.
                await Task.Delay(10, stoppingToken);
            }
        }
    }

    private int ProcessInferenceStep()
    {
        int newTokensInStep = 0;
        // Use a list to iterate as we may modify the _activeItems dictionary
        foreach (var item in _activeItems.Values.ToList())
        {
            item.LastRewindCharCount = 0;
            switch (item.State)
            {
                case InferenceItemState.AwaitingSharedContextProcessing:
                    // The shared context has now been processed by the 'Infer' call.
                    // It's time to fork the conversation and add the specific prompt.
                    item.SharedContext.IsProcessed = true;

                    item.Conversation = item.SharedContext.Conversation.Fork();
                    item.Conversation.Prompt(item.PromptTokens);
                    _currentContextTokens += item.PromptTokens.Length;

                    // This item now needs its specific prompt processed.
                    item.State = InferenceItemState.AwaitingPromptProcessing;
                    _logger.LogDebug("Request {Id} transitioned to AwaitingPromptProcessing.", item.Request.Id);
                    break;

                case InferenceItemState.AwaitingPromptProcessing:
                    // The specific prompt has now been processed. Ready for generation.
                    // The check 'RequiresInference' is now false for this item--Infer() isn't guaranteed to run ALL the inference (e.g., during prompt processing).
                    if (item.Conversation!.RequiresInference) continue;
                    item.State = InferenceItemState.Generating;
                    _logger.LogDebug("Request {Id} transitioned to Generating. Sampling first token.", item.Request.Id);
                    // Fall-through to immediately sample the first token
                    goto case InferenceItemState.Generating;

                case InferenceItemState.Generating:
                    if (item.Conversation!.RequiresInference)
                    {
                        // We might still be prompt processing later active items if earlier ones were more than n_batch (llama.cpp's tokens-to-process-per-pass) / work batch count (this program's configuration).
                        continue;
                    }

                    newTokensInStep++;
                    var token = item.Sampler.Sample(_executor!.Context.NativeHandle, item.Conversation.GetSampleIndex());
                    item.TokensGenerated++;
                    if (item.Sampler.TokensToRewind > 0)
                    {
                        Rewind(item);
                        continue; //TODO: Do we have to Prompt in order to rewind successfully?
                    }

                    if (token.IsEndOfGeneration(_model!.Vocab) || CheckForRepetition(item, token))
                    {
                        HandleCompletedItem(item);
                        continue;
                    }

                    item.Decoder.Add(token);
                    item.Conversation.Prompt(token); // This sets RequiresInference to true for the next loop
                    var tokenText = item.Decoder.Read();

                    //Certain models often get stuck repeating the exact same token, so temporarily ban any token that's been output twice in a row.
                    if (tokenText == item.LastTokenText) item.Sampler.BanToken(token, 3);
                    if (CheckForLineRepetition(item, tokenText))
                    {
                        HandleCompletedItem(item, "Terminated due to line repetition.");
                        continue;
                    }

                    item.ResponseBuilder.Append(tokenText);
                    item.GeneratedTokenLengths.Add(tokenText.Length);
                    item.LastTokenText = tokenText;
                    break;
            }
        }
        return newTokensInStep;
    }

    private static void Rewind(InferenceItem item)
    {
        // The sampler requested a rewind, so do it--we have to rewind the conversation, sampler, response, *and* recent tokens.
        // Note: TokenBanner and TextBanner each maintain their own limited state based on their own max expected rewind, so
        // rewinding may not work as intended if they're both used and have very different ban sequence lengths or if you rewind
        // for other reasons.
        item.Conversation!.Rewind(item.Sampler.TokensToRewind + 1); //+1 because I'm re-prompting it with the last known ALLOWED token.
        item.Sampler.Rewind(item.Sampler.TokensToRewind);

        int charactersToRewind = item.GeneratedTokenLengths
            .GetRange(item.GeneratedTokenLengths.Count - item.Sampler.TokensToRewind, item.Sampler.TokensToRewind).Sum();
        item.ResponseBuilder.Remove(item.ResponseBuilder.Length - charactersToRewind, charactersToRewind);
        item.GeneratedTokenLengths.RemoveRange(item.GeneratedTokenLengths.Count - item.Sampler.TokensToRewind, item.Sampler.TokensToRewind);

        // Rebuild RecentTokens without those last ones
        var recentTokensList = item.RecentTokens.ToList();
        recentTokensList.RemoveRange(recentTokensList.Count - item.Sampler.TokensToRewind, item.Sampler.TokensToRewind);
        item.RecentTokens.Clear();
        foreach (var t in recentTokensList) item.RecentTokens.Enqueue(t);

        // Have to adjust the state after rewinding
        //TODO: I should be able to keep the last n inferred logits and rewind one less token, and adjust the logits for that next point, re-sample, re-Prompt... to save rerunning inference for *one* token.
        item.Conversation!.Prompt(recentTokensList[^1]); // This sets RequiresInference to true for the next loop

        // For UI update
        item.LastRewindCharCount = charactersToRewind;
    }

    private void HandleCompletedItem(InferenceItem item, string? reason = null)
    {
        if (!_activeItems.TryRemove(item.Request.Id, out _)) return; // Already handled
        _recentlyCompletedItems[item.Request.Id] = item; // For reporting progress one last time

        var response = item.ResponseBuilder.ToString();
        if (reason != null) // Early termination is NOT an exception; should still try to parse flash cards from the response.
        {
            _logger.LogWarning("Conversation {Id} completed prematurely. Reason: {Reason}", item.Request.Id, reason);
        }
        item.Request.Tcs.TrySetResult(response);

        // Clean up resources and update token count
        _currentContextTokens -= item.PromptTokens.Length + item.TokensGenerated;

        // Free up some KV cache for the remaining conversations and wastes less time on inference for the dead conversation.
        //TODO: Can't find the source of the bug, not here and not in LlamaSharp, but it seems like the prompt tokens aren't being freed from the KV cache when we dispose the conversation. Might actually be a bug in llama.cpp, as it reports the KV cache being reduced, but then we run into NoKvSlot too soon.
        item.Conversation?.Dispose();

        // Only remove the group extra tokens + dispose the fork for the LAST item sharing that context.
        if (--item.SharedContext.ReferenceCount == 0 && item.SharedContext.TokenCount > 0)
        {
            _logger.LogDebug("Last reference to shared context '{Key}' released. Freeing {TokenCount} tokens.", item.SharedContext.Conversation.ToString(), item.SharedContext.TokenCount);
            _currentContextTokens -= item.SharedContext.TokenCount;
            item.SharedContext.Conversation.Dispose();
            _sharedContextGroups.Remove(item.Request.ExtraContext ?? "");
        }

        _logger.LogDebug("Removed request {Id} from active batch. Active count: {Count}. Context used: {Tokens}", item.Request.Id, _activeItems.Count, _currentContextTokens);
    }

    private InferenceItem? TryCreateInferenceItem(InferenceRequest request)
    {
        var template = GetTemplate();
        var promptTokens = template.Apply("user", request.Prompt, false, true);
        var contextKey = request.ExtraContext ?? string.Empty;

        InferenceItem create(SharedContextGroup group)
        {
            var newItem = new InferenceItem(request, group, promptTokens,
                new StreamingTokenDecoder(_executor!.Context),
                new DistributionSamplingPipelineThatStops(_model!, _config),
                // The state depends on whether the group itself has been processed yet.
                group.IsProcessed ? InferenceItemState.AwaitingPromptProcessing : InferenceItemState.AwaitingSharedContextProcessing);

            //Allow banning text sequences from the config.
            newItem.Sampler.BanTextSequences(_config.GetSection("BannedTextSequences").Get<string[]>()?
                .Select(c => new KeyValuePair<string, int>(c, -1)) ?? []);
            //TODO: Could even use temporary text-allow sequences like ["\n", "A.", "A. "] to force proper question/answer format after a line break.

            return newItem;
        }

        if (!_sharedContextGroups.TryGetValue(contextKey, out var group))
        {
            // --- This is a new context group ---
            LLamaToken[] extraContextTokens = [];
            if (!string.IsNullOrEmpty(request.ExtraContext))
            {
                extraContextTokens = template.Apply("user", _extraContextPrefix + request.ExtraContext + _extraContextSuffix, true, false);
            }

            // Check if there's enough space for the shared context AND the new prompt.
            if (_currentContextTokens + extraContextTokens.Length + promptTokens.Length + 1000 > _parameters.ContextSize)
            {
                _logger.LogInformation("Not enough context space for new group and prompt. Required: {Shared} + {Prompt}, Available: {Available}",
                    extraContextTokens.Length, promptTokens.Length, (int)_parameters.ContextSize! - _currentContextTokens);
                return null;
            }

            var baseConversation = CreateConversation();
            if (extraContextTokens.Length > 0)
            {
                baseConversation.Prompt(extraContextTokens);
            }

            group = new SharedContextGroup(baseConversation, extraContextTokens.Length);
            _sharedContextGroups[contextKey] = group;
            _currentContextTokens += extraContextTokens.Length;

            // Create the item in a state that indicates its shared context needs processing.
            // We pass the promptTokens to be used *after* the shared context is processed.
            var newItem = create(group);

            group.ReferenceCount++;
            return newItem;
        }
        else
        {
            // --- Group already exists ---
            if (_currentContextTokens + promptTokens.Length + 1000 > _parameters.ContextSize)
            {
                _logger.LogInformation("Not enough context space for prompt in existing group. Required: {Prompt}, Available: {Available}",
                    promptTokens.Length, (int)_parameters.ContextSize! - _currentContextTokens);
                return null;
            }

            // The item can be created in a state that indicates its specific prompt needs processing.
            var newItem = create(group);

            // If the group is already processed, we can fork and prompt immediately.
            if (newItem.State == InferenceItemState.AwaitingPromptProcessing)
            {
                newItem.Conversation = group.Conversation.Fork();
                newItem.Conversation.Prompt(promptTokens);
                _currentContextTokens += promptTokens.Length;
            }

            group.ReferenceCount++;
            return newItem;
        }
    }

    private void ReportProgress(int newTokens)
    {
        if (ProgressChanged == null) return;

        // Combine active and recently completed items for reporting
        var allItems = _activeItems.Concat(_recentlyCompletedItems).ToDictionary(kv => kv.Key, kv => kv.Value);

        var completed = _recentlyCompletedItems.Keys.ToHashSet();

        // Build a map id -> ResponseInfo (text + rewind count)
        var responses = allItems.ToDictionary(
            kv => kv.Key,
            kv =>
            {
                var itm = kv.Value;
                var info = new ResponseInfo(itm.LastTokenText ?? "", itm.LastRewindCharCount);
                // Clear the per-item rewind count after reporting so it's one-shot
                itm.LastRewindCharCount = 0;
                return info;
            });

        var tokensPer = allItems.ToDictionary(kv => kv.Key, kv => kv.Value.TokensGenerated); //TODO: Make this consistent. Why have an object in one spot and parallel dictionaries/hash sets otherwise?

        ProgressChanged.Invoke(new BatchProgress(
            (int)_parameters.ContextSize!,
            _currentContextTokens,
            newTokens,
            responses,
            completed,
            tokensPer
        ));

        // Clear the recently completed items after reporting
        foreach (var item in _recentlyCompletedItems)
        {
            item.Value.LastTokenText = "";
        }
        _recentlyCompletedItems.Clear();
    }

    #region Utility and Helper Methods
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

    private static bool IsMistral32Small() => _model!.Metadata.GetValueOrDefault("general.name", "").Contains("Mistral-Small-3.2-24B-Instruct-2506") || _model!.Metadata.GetValueOrDefault("general.basename", "").Contains("Mistral-Small-3.2-24B-Instruct-2506");

    private static IChatTemplate GetTemplate() => IsMistral32Small()
        ? new MistralSmall32Template(_model!, (text, isSpecial) => _executor!.Context.Tokenize(text, false, isSpecial))
        : new LlamaTemplateWrapper(_model!, (text, isSpecial) => _executor!.Context.Tokenize(text, false, isSpecial));

    private void InitializeState()
    {
        var tokenCountFile = _prePromptFile + ".tokencount";

        if (!File.Exists(_prePromptFile))
        {
            // Create initial conversation with pre-prompt as the system message
            var conversation = _executor!.Create();
            //LLamaTemplate version is a bit different because I don't think you can request JUST the BOS token from it.
            var template = GetTemplate();
            var conversationStartTokens = template.GetConversationStart();
            var systemTokens = template.Apply("system", _prePromptText);
            var prePromptTokens = conversationStartTokens.Concat(systemTokens).ToArray();

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

    public Conversation CreateConversation() => _executor!.Load(_prePromptFile);

    private static bool CheckForRepetition(InferenceItem item, LLamaToken token)
    {
        //Try to break the Repetition Curse, but it might be better to just cancel this chunk if that happens
        item.RecentTokens.Enqueue(token);
        if (item.RecentTokens.Count > 20)
        {
            item.RecentTokens.Dequeue();
            if (item.RecentTokens.Distinct().Count() < 5)
            {
                foreach (var tokenToBan in item.RecentTokens.Distinct()) item.Sampler.BanToken(tokenToBan, 20);
                return true; // Terminate
            }
        }
        return false;
    }
    private bool CheckForLineRepetition(InferenceItem item, string tokenText)
    {
        // Check for duplicate lines when we encounter a newline character
        if (tokenText.Contains('\n'))
        {
            // Get the current text and find the last line
            // Get the last line from the StringBuilder
            string lastLine = GetLastLine(item.ResponseBuilder);

            bool isThinkingStart = lastLine.Contains("<think>");
            bool isThinkingEnd = lastLine.Contains("</think>");

            // Update thinking mode state
            bool wasInThinkingMode = item.IsInThinkingMode;
            item.IsInThinkingMode = isThinkingStart || (wasInThinkingMode && !isThinkingEnd);

            // Initialize line history if needed
            item.LineHistory ??= new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);

            // Reset line history counts if we're exiting thinking mode
            if (isThinkingEnd && wasInThinkingMode)
            {
                item.LineHistory = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            }

            // Check if this line is a duplicate and not empty
            if (!string.IsNullOrWhiteSpace(lastLine) && lastLine.Length > 2 && !lastLine.StartsWith("A:"))
            {
                // Track the line and its count
                if (item.LineHistory.TryGetValue(lastLine, out int count))
                {
                    // Line already exists, increment count
                    item.LineHistory[lastLine] = count + 1;

                    // Check if we've exceeded the allowed repetition limit
                    int maxRepeats = item.IsInThinkingMode ? 3 : 0;

                    if (count >= maxRepeats)
                    {
                        _logger.LogWarning("Conversation terminated due to repeating itself: {Line} (repeated {Count} times)", lastLine, count + 1);

                        // This is a duplicate line - remove it from the StringBuilder
                        RemoveLastLine(item.ResponseBuilder);

                        // It's most likely stuck repeating itself, as smaller models especially tend to do, so just stop it here.
                        HandleCompletedItem(item, "Non-answer line repetition");
                        return true; // Terminate
                    }
                }
                else
                {
                    // New line, add to dictionary with count 1
                    item.LineHistory[lastLine] = 1;
                }
            }
        }
        return false;
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

    public void Dispose()
    {
        _loopCts?.Cancel();
        _processingLoopTask?.Wait(); // Wait for loop to finish cleanup
        _loopCts?.Dispose();

        // Clear any remaining work
        while (_pendingWork.TryDequeue(out var request))
        {
            request.Tcs.TrySetCanceled();
        }
        foreach (var item in _activeItems.Values)
        {
            item.Request.Tcs.TrySetCanceled();
        }

        foreach (var group in _sharedContextGroups.Values) group.Conversation.Dispose();
        _sharedContextGroups.Clear();

        GC.SuppressFinalize(this);
    }
    #endregion
}