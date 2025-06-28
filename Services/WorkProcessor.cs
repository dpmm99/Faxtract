using Faxtract.Hubs;
using Faxtract.Interfaces;
using Faxtract.Models;
using Microsoft.AspNetCore.SignalR;
using System.Collections.Concurrent;
using System.Text.Json;
using static Faxtract.Services.LlamaExecutor;

namespace Faxtract.Services;

public class WorkProcessor(IWorkProvider workProvider, IHubContext<WorkHub> hubContext, LlamaExecutor executor, StorageService storageService, IConfiguration configuration, ILogger<WorkProcessor> logger) : BackgroundService
{
    private readonly int _workBatchSize = configuration.GetSection("LLamaConfig").GetValue("WorkBatchSize", 4);
    private static int _processedCount;
    private static readonly ConcurrentDictionary<TextChunk, (string Status, string Response)> _currentWork = new();
    private static DateTime? _processingStartTime;
    private static long _totalTokensProcessed;
    public static long TotalTokensProcessed => _totalTokensProcessed;
    public static long MaxTokens { get; private set; }

    public static double? TokensPerSecond
    {
        get
        {
            if (_processingStartTime == null || _totalTokensProcessed == 0)
                return null;

            var duration = (DateTime.UtcNow - _processingStartTime.Value).TotalSeconds;
            return duration > 0 ? _totalTokensProcessed / duration : 0;
        }
    }

    public static IEnumerable<dynamic> CurrentWork => _currentWork.Where(p => p.Value.Status != "Queued")
        .Select(kv => new
        {
            id = kv.Key,
            status = kv.Value.Status,
            response = kv.Value.Response
        });
    public static int ProcessedCount => _processedCount;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        MaxTokens = configuration.GetSection("LLamaConfig").GetValue("MaxTokens", 1024) * _workBatchSize;
        var minimumWorkBatchSize = configuration.GetSection("LLamaConfig").GetValue("MinimumWorkBatchSize", 1);
        var maxWaitTimeSeconds = configuration.GetSection("LLamaConfig").GetValue("MaxBatchWaitTimeSeconds", 30);

        // Subscribe to global progress updates from the executor once.
        executor.ProgressChanged += OnLlamaProgressChanged;

        try
        {
            DateTime? waitStartTime = null;
            while (!stoppingToken.IsCancellationRequested)
            {
                var remainingCount = workProvider.GetRemainingCount();
                var canProcess = _currentWork.Count < _workBatchSize * 2;

                // Batching and throttling logic
                if (!canProcess || (remainingCount > 0 && remainingCount < minimumWorkBatchSize))
                {
                    if (!canProcess)
                    {
                        await Task.Delay(2000, stoppingToken); // Wait if we're throttled
                        continue;
                    }

                    waitStartTime ??= DateTime.UtcNow;
                    if ((DateTime.UtcNow - waitStartTime.Value).TotalSeconds <= maxWaitTimeSeconds)
                    {
                        await Task.Delay(1000, stoppingToken);
                        continue;
                    }
                }
                waitStartTime = null;

                var batch = workProvider.GetNextBatch(_workBatchSize * 2 - _currentWork.Count);
                if (batch.Count == 0)
                {
                    await Task.Delay(1000, stoppingToken);
                    continue;
                }

                //Reset the performance information because nothing was processing--don't want to say 0.1 tokens per second if we did a few chunks at 1000/second, waited an hour, and started another chunk.
                if (_currentWork.IsEmpty)
                {
                    _processingStartTime = null;
                    _totalTokensProcessed = 0;
                }
                _processingStartTime ??= DateTime.UtcNow;

                // Add items to the central tracking dictionary
                foreach (var item in batch)
                    _currentWork[item] = ("Queued", "");

                await BroadcastStatus();

                DispatchBatchForProcessing(batch, stoppingToken);
            }
        }
        finally
        {
            // Unsubscribe on shutdown
            executor.ProgressChanged -= OnLlamaProgressChanged;
            _currentWork.Clear();
        }
    }

    private static string SaveToJsonFile<T>(T data)
    {
        var guid = Guid.NewGuid().ToString();
        Directory.CreateDirectory(Path.Join(AppContext.BaseDirectory, "out"));
        var filePath = Path.Join(AppContext.BaseDirectory, "out", $"{guid}.json");

        File.WriteAllText(filePath, JsonSerializer.Serialize(data));

        return guid;
    }

    /// <summary>
    /// Handles global progress updates for ALL active work.
    /// Updates both aggregate counters and individual item statuses.
    /// </summary>
    private void OnLlamaProgressChanged(BatchProgress progress)
    {
        // Update the global token counter
        Interlocked.Add(ref _totalTokensProcessed, progress.NewTokens);

        // Update individual item statuses with token information
        foreach (var (id, tokenCount) in progress.TokensPerResponse)
        {
            // Find the TextChunk corresponding to this id
            var chunk = _currentWork.Keys.FirstOrDefault(c => c.Id == id);
            if (chunk == null) continue;

            // Calculate max tokens for this conversation:
            // context size minus all OTHER conversations' tokens (total - this conversation's tokens)
            var tokensUsedByOthers = progress.UsedTokens - tokenCount;
            var maxTokensForThisConversation = progress.ContextMaxTokens - tokensUsedByOthers;

            // Create status message based on completion status
            var message = progress.CompletedMask.Contains(id)
                ? "Processing complete"
                : $"Processing ({tokenCount}/{maxTokensForThisConversation} tokens)";

            // Update the status and response for this chunk
            if (_currentWork.TryGetValue(chunk, out var currentValue))
            {
                _currentWork[chunk] = (message, progress.CurrentResponses.TryGetValue(id, out var response) ? response : currentValue.Response);
            }
        }

        // Broadcast the updated status
        _ = BroadcastStatus();
    }

    /// <summary>
    /// Creates inference requests for a batch of text chunks, enqueues them with the LlamaExecutor,
    /// and attaches a continuation to each request to handle its completion individually.
    /// </summary>
    private void DispatchBatchForProcessing(List<TextChunk> batch, CancellationToken ct)
    {
        // 1. Create an InferenceRequest for each chunk.
        //    We need to keep the original chunk associated with its request.
        var requestsWithSource = batch.ConvertAll(chunk =>
        {
            return (SourceChunk: chunk, Request: new InferenceRequest(
                chunk.Id,
                chunk.Content,
                chunk.ExtraContext,
                new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously)
            ));
        });

        // 2. Attach an individual completion handler to each request's task.
        foreach (var (SourceChunk, Request) in requestsWithSource)
        {
            // Use ContinueWith to handle completion (success or failure) asynchronously
            // for each individual item.
            _ = Request.Tcs.Task.ContinueWith(
                // The lambda becomes async so we can use await inside the handler
                async completedTask => await HandleRequestCompletion(completedTask, SourceChunk),
                ct, // Pass the cancellation token
                TaskContinuationOptions.None,
                TaskScheduler.Default
            );
        }

        // 3. Enqueue all requests with the executor. This returns immediately.
        executor.EnqueueRequests(requestsWithSource.Select(r => r.Request));
    }

    /// <summary>
    /// This callback executes when a single dispatched request is finished.
    /// It's responsible for updating the state for that specific item.
    /// </summary>
    private async Task HandleRequestCompletion(Task<string> task, TextChunk originalChunk)
    {
        try
        {
            if (task.IsFaulted)
            {
                logger.LogError(task.Exception, "A single request processing task failed for chunk {ChunkId}.", originalChunk.Id);
                if (_currentWork.ContainsKey(originalChunk))
                    _currentWork[originalChunk] = ("Failed: " + (task.Exception?.InnerException?.Message ?? "Unknown error"), "");
            }
            else if (task.IsCanceled)
            {
                logger.LogWarning("A single request processing task was canceled for chunk {ChunkId}.", originalChunk.Id);
                if (_currentWork.ContainsKey(originalChunk))
                    _currentWork[originalChunk] = ("Cancelled", "");
            }
            else // Task completed successfully
            {
                var llmResponse = await task; // Get the result string
                var flashCards = FlashCard.ParseFromText(llmResponse, originalChunk).ToList();

                if (flashCards.Count != 0)
                {
                    SaveToJsonFile(flashCards);
                    await storageService.SaveAsync(flashCards);

                    if (_currentWork.ContainsKey(originalChunk))
                    {
                        _currentWork[originalChunk] = ($"Completed: {flashCards.Count} cards created", "");
                        Interlocked.Increment(ref _processedCount);
                    }
                }
                else
                {
                    if (_currentWork.ContainsKey(originalChunk))
                    {
                        _currentWork[originalChunk] = ("Failed: No flashcards parsed from LLM response.", "");
                    }
                }
            }
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "An unexpected error occurred in HandleRequestCompletion for chunk {ChunkId}.", originalChunk.Id);
            if (_currentWork.ContainsKey(originalChunk))
                _currentWork[originalChunk] = ("Failed", "Internal completion error");
        }
        finally
        {
            // Remove the finished item from the display/tracking dictionary after a slight delay.
            await Task.Delay(5000);
            _currentWork.TryRemove(originalChunk, out _);

            await BroadcastStatus();
        }
    }

    private async Task BroadcastStatus()
    {
        await hubContext.Clients.All.SendAsync("UpdateStatus",
            ProcessedCount,
            workProvider.GetRemainingCount(),
            CurrentWork,
            TokensPerSecond,
            TotalTokensProcessed);
    }
}