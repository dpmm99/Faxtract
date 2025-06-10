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
        public async Task<IActionResult> DeleteChunk([FromQuery] int chunkId)
        {
            await storageService.DeleteChunkAsync(chunkId);
            return StatusCode(200);
        }

        [HttpPost]
        public async Task<IActionResult> RestoreChunk([FromBody] StorageService.FlashCardDetails flashCardDetails)
        {
            if (flashCardDetails?.Chunk == null)
            {
                return BadRequest(new { success = false, message = "No chunk data provided" });
            }

            try
            {
                var restoredDetails = await storageService.RestoreChunkAsync(flashCardDetails);
                return Json(new
                {
                    success = true,
                    chunkId = restoredDetails.Chunk?.Id,
                    flashCardCount = restoredDetails.FlashCards.Count,
                    flashCards = restoredDetails.FlashCards.ConvertAll(fc => fc.Id),
                    message = $"Successfully restored chunk and {restoredDetails.FlashCards.Count} flash cards."
                });
            }
            catch (Exception ex)
            {
                return StatusCode(500, new { success = false, message = $"Error restoring chunk: {ex.Message}" });
            }
        }

        [HttpPost]
        public async Task<IActionResult> UpdateFlashCard([FromBody] FlashCardUpdate flashCard)
        {
            await storageService.UpdateFlashCard(flashCard.Id, flashCard.Question, flashCard.Answer);
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

            return Json(new { id = chunk.Id });
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
                var fileChunks = new List<TextChunk>();
                // Check if file is PDF
                if (Path.GetExtension(file.FileName).Equals(".pdf", StringComparison.OrdinalIgnoreCase))
                {
                    // Process PDF file
                    await using var stream = file.OpenReadStream();
                    var pdfText = ExtractTextFromPdf(stream);
                    using var streamReader = new StreamReader(new MemoryStream(System.Text.Encoding.UTF8.GetBytes(pdfText)));

                    // Process the extracted text
                    await foreach (var chunk in chunker.ChunkStreamAsync(streamReader, file.FileName, extraContext))
                    {
                        fileChunks.Add(chunk);
                    }
                }
                else
                {
                    // Process non-PDF file
                    using var streamReader = new StreamReader(file.OpenReadStream());

                    // Pass the extraContext directly to the ChunkStreamAsync method
                    await foreach (var chunk in chunker.ChunkStreamAsync(stripHtml ? ConvertHtmlToPlainText(streamReader) : streamReader, file.FileName, extraContext))
                    {
                        fileChunks.Add(chunk);
                    }
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

        [HttpGet]
        public async Task<IActionResult> DownloadFlashCardsAsCsv()
        {
            var flashCards = await storageService.GetAllFlashCardsForCsvAsync();

            var csv = new System.Text.StringBuilder();
            csv.AppendLine("Question,Answer,FileId");

            foreach (var card in flashCards)
            {
                // Escape CSV fields properly
                string question = $"\"{card.Question.Replace("\"", "\"\"")}\"";
                string answer = $"\"{card.Answer.Replace("\"", "\"\"")}\"";
                string fileId = $"\"{card.FileId.Replace("\"", "\"\"")}\"";

                csv.AppendLine($"{question},{answer},{fileId}");
            }

            var bytes = System.Text.Encoding.UTF8.GetBytes(csv.ToString());
            return new FileContentResult(bytes, "text/csv")
            {
                FileDownloadName = $"flashcards-{DateTime.Now:yyyy-MM-dd}.csv"
            };
        }

        /// <summary>
        /// Extracts text content from a PDF file via PdfPig
        /// </summary>
        /// <param name="pdfStream">The PDF file stream</param>
        /// <returns>Extracted text from all pages of the PDF</returns>
        private static string ExtractTextFromPdf(Stream pdfStream)
        {
            using var pdfDocument = UglyToad.PdfPig.PdfDocument.Open(pdfStream);
            var textBuilder = new System.Text.StringBuilder();

            foreach (var page in pdfDocument.GetPages())
            {
                var letters = page.Letters;

                // Code from the readme
                // 1. Extract words using nearest neighbor approach
                var wordExtractor = UglyToad.PdfPig.DocumentLayoutAnalysis.WordExtractor.NearestNeighbourWordExtractor.Instance;
                var words = wordExtractor.GetWords(letters);

                // 2. Segment page to detect text blocks/paragraphs
                var pageSegmenter = UglyToad.PdfPig.DocumentLayoutAnalysis.PageSegmenter.DocstrumBoundingBoxes.Instance;
                var textBlocks = pageSegmenter.GetBlocks(words);

                // 3. Sort blocks in reading order
                var readingOrder = UglyToad.PdfPig.DocumentLayoutAnalysis.ReadingOrderDetector.UnsupervisedReadingOrderDetector.Instance;

                // Extract text from blocks preserving structure
                foreach (var block in (IEnumerable<UglyToad.PdfPig.DocumentLayoutAnalysis.TextBlock>)readingOrder.Get(textBlocks))
                {
                    textBuilder.Append(block.Text);
                    textBuilder.AppendLine(); // Add line break between blocks
                    textBuilder.AppendLine(); // Extra line break for paragraph separation
                }

                // Page separator
                textBuilder.AppendLine("---");
            }

            return textBuilder.ToString();
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
