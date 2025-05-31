using LLama;
using LLama.Batched;
using LLama.Common;
using LLama.Native;
using System.Diagnostics;
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
    private int _prePromptTokenCount; // Track pre-prompt token count

    // Event for progress reporting
    public event Action<BatchProgress>? ProgressChanged;

    public record BatchProgress(int ContextMaxTokens, int UsedTokens, int NewTokens, IReadOnlyList<string> CurrentResponses, bool[] CompletedMask, IReadOnlyList<int> TokensPerResponse);

    public BatchedExecutor Executor => _executor ?? throw new InvalidOperationException("Executor not initialized");

    public LlamaExecutor(IConfiguration configuration, ILogger<LlamaExecutor> logger)
    {
        _config = configuration.GetSection("LLamaConfig");
        _logger = logger;

        _parameters = new ModelParams(_config["ModelPath"])
        {
            //Context: pre-prompt size (105 currently) plus a few tokens for the chat template plus WorkBatchSize times enough for one input chunk and response.
            ContextSize = _config.GetValue("ContextSize", 160 + _config.GetValue<uint>("MaxTokens", 1024) * (uint)_config.GetValue("WorkBatchSize", 4)),
            BatchSize = _config.GetValue("BatchSize", (uint)4096),
            Threads = _config.GetValue("Threads", Environment.ProcessorCount),
            GpuLayerCount = _config.GetValue("GpuLayerCount", 0),
            FlashAttention = true,

            //TODO: Could support appsettings for TypeK, TypeV, etc.
        };

        _prePromptFile = _config["PrePromptFile"] ?? "preprompt.state";
        _prePromptText = _config["PrePromptText"] ?? string.Empty;

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

    public async Task<List<string>> GenerateResponses(List<string> prompts, int contextMaxTokens = 0, CancellationToken cancellationToken = default)
    {
        if (contextMaxTokens <= 0)
            contextMaxTokens = (int)_parameters.ContextSize!;

        // Create decoders for each conversation
        var responses = new List<StringBuilder>();
        var samplers = new List<DistributionSamplingPipelineThatStops>();
        var decoders = new List<StreamingTokenDecoder>();
        var conversations = new List<Conversation>();

        try
        {
            // Initialize conversations from the saved state
            var conversation = CreateConversation();
            //Shouldn't be needed here, as the above should be loading a fully prepared KV cache: await _executor!.Infer();
            foreach (var _ in prompts)
            {
                //Don't fork off a conversation for the first one; just use the original fork. Otherwise, we're wasting part of the batch. Not sure it actually hurts anything, though.
                conversations.Add(conversations.Count == 0 ? conversation : conversation.Fork());
                decoders.Add(new StreamingTokenDecoder(_executor!.Context));
                responses.Add(new StringBuilder());
                //We need separate samplers per conversation due to the token banning logic.
                samplers.Add(new DistributionSamplingPipelineThatStops(_model!, _config));
            }

            // Submit all prompts
            var contextConsumed = 0;
            for (var i = 0; i < prompts.Count; i++)
            {
                //TODO: Need to also include the enclosing topic hierarchy so the flash cards know what context the *student* would have.
                var template = new LLamaTemplate(_model!);
                template.Add("user", prompts[i]);
                template.AddAssistant = true;

                var promptText = Encoding.UTF8.GetString(template.Apply());
                var promptTokens = _executor!.Context.Tokenize(promptText);
                contextConsumed += promptTokens.Length;
                conversations[i].Prompt(promptTokens);
            }

            var lastTokenTexts = new string[prompts.Count];
            var completedMask = new bool[prompts.Count];
            var tokensPerConversation = new int[prompts.Count];

            // Main inference loop
            var newTokens = 1;
            while (contextConsumed < contextMaxTokens && completedMask.Contains(false) && newTokens > 0)
            {
                var result = await _executor!.Infer(cancellationToken);
                if (result != DecodeResult.Ok)
                {
                    _logger.LogError("Batch inference failed after {ContextConsumed} tokens with result: {Result}", contextConsumed, result);
                    Debugger.Break();
                }

                // Sample and decode tokens for each active conversation
                newTokens = 0;
                for (var i = 0; i < conversations.Count; i++)
                {
                    if (completedMask[i] || conversations[i].RequiresInference) continue;
                    newTokens++;
                    tokensPerConversation[i]++;

                    var token = samplers[i].Sample(_executor!.Context.NativeHandle, conversations[i].GetSampleIndex());

                    // Check for end of sequence
                    if (token.IsEndOfGeneration(_model!.Vocab))
                    {
                        completedMask[i] = true;
                        lastTokenTexts[i] = "";
                        conversations[i].Dispose(); // Free up some KV cache for the remaining conversations and wastes less time on inference for the dead conversation.
                        continue;
                    }

                    decoders[i].Add(token);
                    conversations[i].Prompt(token);
                    var tokenText = decoders[i].Read();
                    //Certain models often get stuck repeating the exact same token, so temporarily ban any token that's been output twice in a row.
                    if (tokenText == lastTokenTexts[i]) samplers[i].BanToken(token, 3);
                    lastTokenTexts[i] = tokenText;
                    responses[i].Append(lastTokenTexts[i]);

                    //TODO: Consider banning # and * (or ** if that's its own token) for one or two tokens at the start of each line since I'm using my TokenBanner sampler, thereby preventing dumber models from outputting "**Q:**" or "### Here are your flash cards" or whatever.
                }

                // Report progress
                ProgressChanged?.Invoke(new BatchProgress(
                    contextMaxTokens,
                    contextConsumed,
                    newTokens,
                    lastTokenTexts,
                    [.. completedMask],
                    [.. tokensPerConversation]
                ));

                contextConsumed += newTokens;
            }

            // Collect final responses
            return responses.ConvertAll(p => p.ToString());
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error during LlamaExecutor processing: {Message}", ex.Message);
            throw;
        }
        finally
        {
            // Cleanup conversations
            foreach (var conversation in conversations)
            {
                conversation.Dispose();
            }
        }
    }

    public void Dispose()
    {
        // Only dispose local resources - static resources persist for the application lifetime
        GC.SuppressFinalize(this);
    }
}