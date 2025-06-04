using Faxtract.Interfaces;
using Faxtract.Models;
using Faxtract.Services;
using HtmlAgilityPack;
using Microsoft.AspNetCore.Mvc;

namespace Faxtract.Controllers
{
    public class HomeController(IWorkProvider workProvider, StorageService storageService) : Controller
    {
        public IActionResult Index()
        {
            return View();
        }

        [HttpGet]
        public async Task<IActionResult> GetFlashCardChartData()
        {
            var chartData = await storageService.GetFlashCardChartDataAsync();
            return Json(chartData);
        }

        [HttpGet]
        public async Task<IActionResult> GetFlashCardDetails([FromQuery] int chunkId)
        {
            var chartData = await storageService.GetFlashCardDetailsAsync(chunkId);
            return Json(chartData);
        }

        [HttpPost]
        public async Task<IActionResult> DeleteFlashCard([FromQuery] int flashCardId)
        {
            await storageService.DeleteFlashCardAsync(flashCardId);
            return StatusCode(200);
        }

        [HttpPost]
        public async Task<IActionResult> DeleteAllFlashCards([FromQuery] int chunkId)
        {
            await storageService.DeleteChunkAsync(chunkId);
            return StatusCode(200);
        }

        [HttpPost]
        public async Task<IActionResult> RetryChunk([FromQuery] int chunkId)
        {
            // Get the chunk data
            var chunk = await storageService.GetChunkAsync(chunkId);
            if (chunk == null)
            {
                return StatusCode(404);
            }

            // Delete existing flash cards for this chunk
            await storageService.DeleteChunkAsync(chunkId);

            // Resubmit the chunk for processing
            await storageService.SaveAsync([chunk]);
            workProvider.AddWork([chunk]);

            return StatusCode(200);
        }

        [HttpPost]
        public async Task<IActionResult> UploadFile([FromForm] List<IFormFile> files,
            [FromForm] string? extraContext = null,
            [FromForm] bool stripHtml = false)
        {
            if (files == null || files.Count == 0 || files.All(f => f.Length == 0))
            {
                return BadRequest(new { success = false, message = "No files uploaded" });
            }

            int totalChunks = 0;
            var chunker = new TextChunker();

            foreach (var file in files)
            {
                using var streamReader = new StreamReader(file.OpenReadStream());

                var fileChunks = new List<TextChunk>();
                // Pass the extraContext directly to the ChunkStreamAsync method
                await foreach (var chunk in chunker.ChunkStreamAsync(stripHtml ? ConvertHtmlToPlainText(streamReader) : streamReader, file.FileName, extraContext))
                {
                    fileChunks.Add(chunk);
                }

                await storageService.SaveAsync(fileChunks);
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

        //Can't stream HTML to plain text, so we read the whole thing into memory first
        public static StreamReader ConvertHtmlToPlainText(StreamReader html)
        {
            var doc = new HtmlDocument();
            doc.LoadHtml(html.ReadToEnd());

            var sw = new StringWriter();
            ConvertTo(doc.DocumentNode, sw);
            sw.Flush();
            return new StreamReader(new MemoryStream(System.Text.Encoding.UTF8.GetBytes(sw.ToString())));
        }

        private static void ConvertContentTo(HtmlNode node, TextWriter outText)
        {
            foreach (HtmlNode subnode in node.ChildNodes)
            {
                ConvertTo(subnode, outText);
            }
        }

        private static void ConvertTo(HtmlNode node, TextWriter outText)
        {
            string html;
            switch (node.NodeType)
            {
                case HtmlNodeType.Comment:
                    // don't output comments
                    break;

                case HtmlNodeType.Document:
                    ConvertContentTo(node, outText);
                    break;

                case HtmlNodeType.Text:
                    // script and style must not be output
                    string parentName = node.ParentNode.Name;
                    if ((parentName == "script") || (parentName == "style"))
                        break;

                    // get text
                    html = ((HtmlTextNode)node).Text;

                    // is it in fact a special closing node output as text?
                    if (HtmlNode.IsOverlappedClosingElement(html))
                        break;

                    // check the text is meaningful and not a bunch of whitespaces
                    if (html.Trim().Length > 0)
                    {
                        outText.Write(HtmlEntity.DeEntitize(html));
                    }
                    break;

                case HtmlNodeType.Element:
                    switch (node.Name)
                    {
                        case "p":
                            // treat paragraphs as crlf
                            outText.Write("\r\n");
                            break;
                        case "br":
                            outText.Write("\r\n");
                            break;
                    }

                    if (node.HasChildNodes)
                    {
                        ConvertContentTo(node, outText);
                    }
                    break;
            }
        }
    }
}
