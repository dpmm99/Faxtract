# Faxtract# Faxtract

Faxtract is an ASP.NET 8 MVC application designed for document extraction and processing (particularly flash cards, though you can give it whatever prompt you want) using LLamaSharp.

## Prerequisites

- .NET 8 SDK
- Windows OS with CUDA 12-compatible NVIDIA GPU 
  (Note: You can modify the LlamaSharp backend for other OS/GPU configurations)
- Visual Studio 2022 or another compatible IDE

## Setup

1. Clone the repository
2. Open the solution in Visual Studio 2022
3. Download an LLM model compatible with llama.cpp, e.g., one of the Phi-4 quantizations from [Hugging Face](https://huggingface.co/MaziyarPanahi/phi-4-GGUF/tree/main), and place it wherever you want
4. Put its path and filename in the `appsettings.json` file
5. Hit F5

## Features

- Document text extraction
- Content chunking with configurable size and overlap
- Batch inference for maximum throughput
- Real-time processing updates via SignalR
- Flash card generation from text content
- Configurable LLM parameters via `appsettings.json`

## Architecture Overview

The application's core processing is primarily handled through these components:

#### LlamaExecutor

`LlamaExecutor` manages the LLM (Large Language Model) interaction using LLamaSharp. Key responsibilities:

- Loads the language model from the specified path
- Manages the model's context window and batched execution
- Handles the system prompt to control the model's behavior (cached as a file for efficiency)
- Processes batches of text prompts (each is a chunk of file contents) in parallel
- Generates responses based on the input text
- Reports progress through an event system

#### WorkProcessor

`WorkProcessor` runs as a background service that orchestrates the entire processing pipeline:

- Retrieves batches of `TextChunk` objects from the `IWorkProvider`
- Submits these batches to the `LlamaExecutor` for processing
- Monitors processing progress and updates clients in real-time via SignalR
- Parses the LLM-generated responses into structured `FlashCard` objects
- Tracks processing metrics (tokens processed, processing speed)
- Handles error conditions and graceful shutdown

When a batch of text chunks is processed, `WorkProcessor` sends them to `LlamaExecutor`, which runs them through the LLM and returns the responses. These responses are then parsed into flash cards and stored for later use.

#### TextChunker

`TextChunker` is responsible for dividing document content into manageable chunks for LLM processing:

- Breaks down large text documents into smaller, (hopefully) semantically-coherent chunks
- Uses a multi-level chunking strategy that preserves document structure:
  - First splits by paragraphs/sections (double newlines)
  - Then by sentences for oversized sections
- Maintains positional information (start/end positions) for each chunk for later reference
- Uses a configurable maximum chunk size (default 800 tokens) to ensure chunks fit within model context limits--longer chunks improves contextual coherence but reduces the amount of performance gains you can get from batching
- Aims for a preferred minimum chunk size (default 600 tokens) to try to keep the chunk coherent outside the entire document context
- Provides streaming support via `ChunkStreamAsync` for memory-efficient processing of large documents
- Uses a simple token estimation heuristic (roughly 4 characters per token)--this could be switched to use the model's actual tokenizer

Including the text document's title, chapter title, etc. could be helpful for giving the LLM sufficient context (and that context should be shown to the student on the flash card as well), but this is not implemented.

## Performance Considerations

- The number of file chunks to process in parallel is configurable via the `WorkBatchSize` setting in `appsettings.json`
- Larger batch sizes increase throughput but require more GPU memory--larger batch sizes become harmful instead of beneficial soon after memory usage exceeds your total VRAM, if not sooner
- KV cache could also be quantized to reduce memory usage, but this option is not currently implemented in the application (you can easily hard-code it, though!)

## Trademark Disclaimers

- .NET, ASP.NET, Visual Studio, and Windows are trademarks of Microsoft Corporation.
- NVIDIA and CUDA are trademarks and/or registered trademarks of NVIDIA Corporation in the U.S. and/or other countries.
- LLamaSharp and llama.cpp are open-source projects and any references to them are for informational purposes only.
- Hugging Face is a trademark of Hugging Face, Inc.

This software is provided as-is, without warranty of any kind, express or implied.
