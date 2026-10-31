# Faxtract -- Agent Instructions

## What this is
ASP.NET 10 MVC app for document text extraction and LLM-powered flash card generation using LLamaSharp. Windows + CUDA GPU only (unless you swap the backend NuGet).

## Commands
```
dotnet build Faxtract.sln
dotnet test Tests/Tests.csproj
dotnet run --project Faxtract.csproj          # needs LLAMA_PRESET env var or VS launch profile
```
Launch profiles in `Properties/launchSettings.json`: `Phi-4`, `MuseGlimmer-30B`, `Gemma4-26B`, `LlmAsJudge-Phi-4`. Each sets `LLAMA_PRESET` to load `appsettings.<preset>.json` on top of base `appsettings.json`.

## LLamaSharp backend: two modes
- **NuGet** (default): `LLamaSharp` + `LLamaSharp.Backend.Vulkan.Linux`/`.Windows` from NuGet. Set via `Directory.Build.props` or `Directory.Build.user.props`.
- **Local source**: Set `<UseProjectReferenceForLLamaSharp>true</UseProjectReferenceForLLamaSharp>` in `Directory.Build.user.props` (gitignored). Expects `..\..\NotMine\LLamaSharp\LLama\LLamaSharp.csproj` on disk. Only the solution file references the local path directly -- the csproj uses the conditional import.

## Architecture (high level)
```
HomeController.UploadFile → TextChunker.ChunkStreamAsync → StorageService.SaveAsync → MemoryWorkProvider.AddWork
                                                                                  ↓
                                                                          WorkProcessor (BackgroundService)
                                                                                  ↓
                                                                          LlamaExecutor (continuous batching loop)
                                                                                  ↓
                                                                          Sampling/TextBanner + TokenBanner (custom samplers)
```
- **WorkProcessor**: dequeues batches from `IWorkProvider`, dispatches to `LlamaExecutor`, parses LLM output → `FlashCard`, persists via `StorageService`, reports progress via SignalR.
- **LlamaExecutor**: shared pre-prompt context (saved to file for speed), per-request forked conversations, shared context groups by `ExtraContext` key, handles KV cache lifecycle.
- **Sampling/**: `TextBanner` (text-level ban/allow rules), `TokenBanner` (token-level ban/allow rules). Both implement `ICustomSampler`. Banned sequences trigger token-level bans + rewrites. Repetition detection lives in `LlamaExecutor.CheckForRepetition` / `CheckForLineRepetition`.
- **StorageService**: SQLite at `out/flashcards.db`. Tables: `TextChunks`, `FlashCards`.

## Key constraints & gotchas
- **TypeK/TypeV**: Must be both F16 or both Q8_0. Mixed combinations crash llama.cpp.
- **KV cache bug**: Disposed conversations don't fully free KV cache memory (llama.cpp/LLamaSharp bug). Setting `AllowContinuousBatching: true` in `appsettings.json` causes "no kv slot" crashes sooner. Leave it `false` unless the bug is fixed.
- **MoE models** (Qwen3-30B-A3B, Qwen3.6-35B-A3B, Gemma4-26B-A4B): Reduce `WorkBatchSize` and `MinimumWorkBatchSize` -- batching makes them slower, not faster.
- **Pre-prompt**: System prompt is tokenized once at startup and cached to a `.state` file + `.tokencount` file. Deleted at startup if the model changed.
- **FlashCard parsing**: Output format is strict -- lines starting with `Q:` followed by `A:`. `</think>` thinking blocks are stripped before parsing.
- **Token estimation**: `TextChunker` uses a rough 4 chars/token heuristic, not the actual tokenizer.

## Tests
MSTest project at `Tests/Tests.csproj`. Tests load a real GGUF model in `[ClassInitialize]` -- the model path is **hardcoded** (`/mnt/c/AI/Magistral-Small-2509-UD-Q5_K_XL.gguf`). You'll need to update this path or have the model to run the tests. Tests exercise `TextBanner` and `TokenBanner` custom sampler logic (banned/allowed sequences, rewinding).

## Model recommendations (from project notes)
Best results: **Phi-4**. Mistral-Small-3.2-24B is good but don't quantize its KV cache. Qwen3-30B-A3B is worse at following instructions. Gemma 3 and Qwen3 30B-A3B were tested and rated lower.

## Directory structure
```
Controllers/      → HomeController.cs (upload, CRUD, CSV download)
Hubs/             → WorkHub.cs (SignalR)
Interfaces/       → IWorkProvider.cs, IPromptTemplater.cs
Models/           → FlashCard, TextChunk, InferenceRequest, MemoryWorkProvider, WorkItem, WorkStatus
Sampling/         → TextBanner, TokenBanner, LlamaSamplers, circular buffer, repetition helpers
Services/         → LlamaExecutor, WorkProcessor, TextChunker, StorageService, LlamaTemplateWrapper, MistralSmall32Template
Views/            → Razor views
wwwroot/          → Frontend assets, SignalR client
appsettings*.json → Base config + preset overrides
```

## What's missing
No CI, no pre-commit hooks, no linter/formatter config, no `opencode.json`, no `.editorconfig`. Development is VS 2022-centric.
