using Faxtract.Hubs;
using Faxtract.Interfaces;
using Faxtract.Models;
using Microsoft.AspNetCore.SignalR;
using System;
using System.Collections.Concurrent;
using System.Text.Json;

namespace Faxtract.Services;

public class WorkProcessor(IWorkProvider workProvider, IHubContext<WorkHub> hubContext, LlamaExecutor executor, IConfiguration configuration, ILogger<WorkProcessor> logger) : BackgroundService
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

    public static IEnumerable<dynamic> CurrentWork => _currentWork
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
        try
        {
            var continuation = false;
            while (!stoppingToken.IsCancellationRequested)
            {
                var batch = workProvider.GetNextBatch(_workBatchSize);
                if (batch.Count == 0)
                {
                    await Task.Delay(1000, stoppingToken);
                    continuation = false;
                    continue;
                }

                if (!continuation)
                {
                    _processingStartTime = DateTime.UtcNow;
                    _totalTokensProcessed = 0;
                    continuation = true;
                }

                foreach (var item in batch)
                    _currentWork[item] = ("Queued", "");
                await BroadcastStatus();

                try
                {
                    // Process entire batch at once
                    var flashCards = await ProcessBatchWithLLM(batch, stoppingToken);

                    SaveToJsonFile(flashCards); //Temporarily save to a file; later, we'll put it in a database

                    // Update status for all items in batch
                    foreach (var (item, flashCard) in batch.Zip(flashCards))
                    {
                        _currentWork[item] = (flashCard == null ? "Failed" : "Completed", _currentWork[item].Response);
                        if (flashCard != null)
                        {
                            Interlocked.Increment(ref _processedCount);
                        }
                    }
                }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
                {
                    logger.LogInformation("Batch processing cancelled due to shutdown request");
                    foreach (var item in batch)
                        _currentWork[item] = ("Cancelled", _currentWork[item].Response);
                    throw;
                }
                catch (Exception ex)
                {
                    logger.LogError(ex, "Batch processing failed");
                    foreach (var item in batch)
                        _currentWork[item] = ("Failed", _currentWork[item].Response);
                }

                await BroadcastStatus();
                foreach (var item in batch)
                    _currentWork.TryRemove(item, out _);
            }
        }
        finally
        {
            // Clear any remaining work items on shutdown
            foreach (var key in _currentWork.Keys)
            {
                _currentWork[key] = ("Cancelled", _currentWork[key].Response);
                _currentWork.TryRemove(key, out _);
            }
            await BroadcastStatus();
        }
    }

    private string SaveToJsonFile<T>(T data)
    {
        var guid = Guid.NewGuid().ToString();
        var filePath = Path.Join(AppContext.BaseDirectory, $"{guid}.json");

        File.WriteAllText(filePath, JsonSerializer.Serialize(data));

        return guid;
    }

    private async Task<List<FlashCard>> ProcessBatchWithLLM(List<TextChunk> batch, CancellationToken ct)
    {
        // Prepare prompts for each chunk
        var prompts = batch.ConvertAll(chunk => chunk.Content);

        // Subscribe to progress updates
        var progressHandler = new Action<LlamaExecutor.BatchProgress>(progress =>
        {
            for (int i = 0; i < batch.Count; i++)
            {
                var chunkTokens = progress.TokensPerResponse[i];

                // Calculate max tokens for this conversation:
                // context size minus all OTHER conversations' tokens (total - this conversation's tokens)
                var tokensUsedByOthers = progress.UsedTokens - chunkTokens;
                var maxTokensForThisConversation = progress.ContextMaxTokens - tokensUsedByOthers;

                var message = progress.CompletedMask[i]
                    ? "Processing complete"
                    : $"Processing ({chunkTokens}/{maxTokensForThisConversation} tokens)";

                _currentWork[batch[i]] = (message, progress.CurrentResponses[i]);
            }
            Interlocked.Add(ref _totalTokensProcessed, progress.NewTokens);
            _ = BroadcastStatus();
        });

        _totalTokensProcessed = 0;
        executor.ProgressChanged += progressHandler;
        try
        {
            var responses = await executor.GenerateResponses(prompts, 0, ct);
            // Parse FlashCards only once at the end
            return responses.Zip(batch)
                .SelectMany(pair => FlashCard.ParseFromText(pair.First, pair.Second))
                .Where(card => card != null)
                .ToList()!;
        }
        finally
        {
            executor.ProgressChanged -= progressHandler;
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
