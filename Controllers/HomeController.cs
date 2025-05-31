using Faxtract.Interfaces;
using Faxtract.Models;
using Faxtract.Services;
using Microsoft.AspNetCore.Mvc;

namespace Faxtract.Controllers
{
    public class HomeController(IWorkProvider workProvider) : Controller
    {
        public IActionResult Index()
        {
            return View();
        }

        [HttpPost]
        public async Task<IActionResult> UploadFile([FromForm] List<IFormFile> files)
        {
            if (files == null || !files.Any() || files.All(f => f.Length == 0))
            {
                return BadRequest(new { success = false, message = "No files uploaded" });
            }

            int totalChunks = 0;
            var chunker = new TextChunker();

            foreach (var file in files)
            {
                using var streamReader = new StreamReader(file.OpenReadStream());

                var fileChunks = new List<TextChunk>();
                await foreach (var chunk in chunker.ChunkStreamAsync(streamReader, file.FileName))
                {
                    fileChunks.Add(chunk);
                }

                workProvider.AddWork(fileChunks);
                totalChunks += fileChunks.Count;
            }

            return Json(new
            {
                success = true,
                filesProcessed = files.Count,
                chunksAdded = totalChunks,
                message = $"Successfully uploaded {files.Count} file(s) and added {totalChunks} chunks for processing."
            });
        }
    }
}
