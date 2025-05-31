using Faxtract.Interfaces;
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
        public async Task<IActionResult> UploadFile(IFormFile file)
        {
            if (file == null || file.Length == 0)
            {
                return BadRequest("No file uploaded");
            }

            var chunker = new TextChunker();
            using var streamReader = new StreamReader(file.OpenReadStream());

            await foreach (var chunk in chunker.ChunkStreamAsync(streamReader))
            {
                workProvider.AddWork([chunk]);
            }

            return RedirectToAction(nameof(Index));
        }
    }
}
