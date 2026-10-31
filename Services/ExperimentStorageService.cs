using Faxtract.Models;
using System.Text.Json;

namespace Faxtract.Services;

/// <summary>
/// Persists LLM experiment data to JSONL files in a separate directory from flash card storage.
/// Each original line gets its own JSONL file keyed by a hash of the line content.
/// </summary>
public class ExperimentStorageService
{
    private readonly string _basePath = Path.Join(AppContext.BaseDirectory, "out", "experiments");
    private readonly JsonSerializerOptions _jsonOptions = new() { WriteIndented = false };

    public ExperimentStorageService()
    {
        Directory.CreateDirectory(_basePath);
    }

    private string GetFilePath(string lineHash)
    {
        return Path.Join(_basePath, $"{Math.Abs(lineHash.GetHashCode()):X8}.jsonl");
    }

    /// <summary>
    /// Saves an experiment step to the appropriate JSONL file.
    /// </summary>
    public async Task SaveAsync(string lineHash, string lineContent, InferenceRequest request,
        string response, string? feedback = null)
    {
        var step = new ExperimentStep
        {
            LineHash = lineHash,
            LineContent = lineContent,
            Iteration = request.Iteration,
            Kind = request.PrePromptKind,
            Response = response,
            Feedback = feedback,
            Timestamp = DateTime.UtcNow
        };

        var json = JsonSerializer.Serialize(step, _jsonOptions);
        await File.AppendAllTextAsync(GetFilePath(lineHash), json + "\n", System.Text.Encoding.UTF8);
    }

    /// <summary>
    /// Marks an experiment as complete by writing a final marker to the JSONL file.
    /// </summary>
    public async Task SaveFinalAsync(InferenceRequest request, string finalResponse)
    {
        var lineHash = request.Id.GetHashCode().ToString("X8");
        var finalLine = JsonSerializer.Serialize(new { Type = "Final", Iteration = request.Iteration, Response = finalResponse }, _jsonOptions);
        await File.AppendAllTextAsync(GetFilePath(lineHash), finalLine + "\n", System.Text.Encoding.UTF8);
    }

    /// <summary>
    /// Gets the latest non-judgment step for a given line hash (the most recent correction or initial generation).
    /// </summary>
    public async Task<ExperimentStep?> GetLatestNonJudgmentAsync(string lineHash)
    {
        var path = GetFilePath(lineHash);
        if (!File.Exists(path)) return null;

        var lines = (await File.ReadAllLinesAsync(path)).Reverse();
        foreach (var line in lines)
        {
            try
            {
                var step = JsonSerializer.Deserialize<ExperimentStep>(line);
                if (step != null && step.Kind != PrePromptKind.Judgment)
                    return step;
            }
            catch
            {
                // Skip malformed lines (e.g., final markers)
                continue;
            }
        }
        return null;
    }

    /// <summary>
    /// Gets all steps for a given line hash.
    /// </summary>
    public async Task<List<ExperimentStep>> GetAllAsync(string lineHash)
    {
        var path = GetFilePath(lineHash);
        if (!File.Exists(path)) return [];

        var steps = new List<ExperimentStep>();
        foreach (var line in await File.ReadAllLinesAsync(path))
        {
            try
            {
                var step = JsonSerializer.Deserialize<ExperimentStep>(line);
                if (step != null) steps.Add(step);
            }
            catch
            {
                // Skip malformed lines
                continue;
            }
        }
        return steps;
    }

    /// <summary>
    /// Gets all experiment files in the directory.
    /// </summary>
    public async Task<List<string>> GetAllLineHashesAsync()
    {
        var hashes = new List<string>();
        if (!Directory.Exists(_basePath)) return hashes;

        foreach (var file in Directory.GetFiles(_basePath, "*.jsonl"))
        {
            var hash = Path.GetFileNameWithoutExtension(file);
            hashes.Add(hash);
        }
        return hashes;
    }

    /// <summary>
    /// Deletes all experiment data for a given line hash.
    /// </summary>
    public Task DeleteAsync(string lineHash)
    {
        var path = GetFilePath(lineHash);
        if (File.Exists(path))
        {
            File.Delete(path);
        }
        return Task.CompletedTask;
    }

    /// <summary>
    /// Deletes all experiment data.
    /// </summary>
    public Task DeleteAllAsync()
    {
        if (Directory.Exists(_basePath))
        {
            var files = Directory.GetFiles(_basePath, "*.jsonl");
            foreach (var file in files)
            {
                File.Delete(file);
            }
        }
        return Task.CompletedTask;
    }
}
