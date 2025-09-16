// Services/WebScraperService.cs - Enhanced with large file streaming support
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using PuppeteerSharp;

namespace PrivacyLens.Services
{
    /// <summary>
    /// Production-grade web scraper with intelligent content filtering.
    /// Only saves meaningful content, skips navigation/menu pages.
    /// </summary>
    public class WebScraperService : IDisposable
    {
        private readonly string appPath;
        private IBrowser? browser;
        private readonly HashSet<string> downloadedContentHashes;
        private readonly HashSet<string> downloadedUrlHashes;
        private readonly HashSet<string> allowedDomains;
        private readonly object downloadLock = new object();

        // Content filtering thresholds
        private const int MinimumContentLength = 1000; // ~200 words
        private const int MinimumWordCount = 100;
        private const double MaxLinkToWordRatio = 0.1; // Max 10% links to words

        // Document MIME types for detection
        private static readonly HashSet<string> DocumentMimeTypes = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "application/pdf",
            "application/msword",
            "application/vnd.openxmlformats-officedocument.wordprocessingml.document",
            "application/vnd.ms-excel",
            "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet",
            "application/vnd.ms-powerpoint",
            "application/vnd.openxmlformats-officedocument.presentationml.presentation",
            "text/plain",
            "text/csv",
            "application/rtf",
            "application/zip",
            "application/x-zip-compressed"
        };
        // File extensions for DOM scanning
        private static readonly string[] DocumentExtensions =
        {
            ".pdf", ".doc", ".docx", ".xls", ".xlsx", ".ppt", ".pptx",
            ".txt", ".csv", ".rtf", ".zip"
        };
        public enum ScrapeTarget
        {
            CorporateWebsite,
            ApplicationWebsite
        }

        public class ScrapeResult
        {
            public string SessionId { get; set; } = string.Empty;
            public string EvidencePath { get; set; } = string.Empty;
            public int PagesScraped { get; set; }
            public int PagesSkipped { get; set; }
            public int DocumentsDownloaded { get; set; }
            public int Failed { get; set; }
            public DateTime StartTime { get; set; }
            public DateTime EndTime { get; set; }
            public string TargetUrl { get; set; } = string.Empty;
            public List<string> DownloadedFiles { get; set; } = new List<string>();
        }

        // Add a new record to pass structured download results
        private record DownloadResult(bool Success, string? FilePath, bool IsLargeFile = false);

        public WebScraperService(string? basePath = null)
        {
            appPath = basePath ?? AppDomain.CurrentDomain.BaseDirectory;
            downloadedContentHashes = new HashSet<string>();
            downloadedUrlHashes = new HashSet<string>();
            allowedDomains = new HashSet<string>();
        }

        /// <summary>
        /// Main scraping method with enhanced content filtering
        /// </summary>
        public async Task<ScrapeResult> ScrapeWebsiteAsync(
            string url,
            ScrapeTarget target,
            bool antiDetection = false,
            int maxPages = 50)
        {
            var timestamp = DateTime.Now.ToString("yyyy-MM-dd_HH-mm-ss");
            string evidencePath = target == ScrapeTarget.CorporateWebsite
                ? Path.Combine(appPath, "governance", "corporate-scrapes", $"scrape_{timestamp}")
                : Path.Combine(appPath, "assessments", "application-scrapes", $"scrape_{timestamp}");
            var result = new ScrapeResult
            {
                SessionId = $"scrape_{timestamp}",
                EvidencePath = evidencePath,
                TargetUrl = url,
                StartTime = DateTime.Now
            };

            Console.WriteLine($"\n?? Starting {target} scrape");
            Console.WriteLine($"?? URL: {url}");
            Console.WriteLine($"?? Session: {result.SessionId}");
            Console.WriteLine($"?? Mode: {(antiDetection ? "Stealth" : "Fast")}");
            Console.WriteLine($"?? Max pages: {maxPages}");
            Console.WriteLine($"?? Content filtering: ENABLED\n");

            try
            {
                // Create directory structure
                await CreateDirectoryStructureAsync(evidencePath);
                // Set up allowed domains
                SetupAllowedDomains(url);
                // Initialize browser with proper configuration
                await InitializeBrowserAsync(antiDetection);
                // Create page with proper viewport
                var page = await browser!.NewPageAsync();
                await page.SetViewportAsync(new ViewPortOptions { Width = 1920, Height = 1080 });
                // Configure CDP for downloads
                var downloadsPath = Path.Combine(evidencePath, "documents");
                await ConfigureCDPDownloadBehavior(page, downloadsPath);

                // Set up network interception (this tracks documents automatically)
                var networkDocumentCount = 0;
                page.Response += async (sender, e) =>
                {
                    try
                    {
                        var response = e.Response;
                        var contentType = response.Headers.ContainsKey("content-type") ?
                            response.Headers["content-type"] : "";

                        if (DocumentMimeTypes.Any(mt => contentType.Contains(mt, StringComparison.OrdinalIgnoreCase)))
                        {
                            byte[] buffer = null;
                            try
                            {
                                using (var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10)))
                                {
                                    buffer = await response.BufferAsync();
                                }
                            }
                            catch (Exception bufferEx)
                            {
                                Console.WriteLine($"   ??  Could not intercept document (may download separately): {bufferEx.Message}");
                                return;
                            }

                            if (buffer != null && buffer.Length > 0)
                            {
                                var rawFilename = ExtractFilenameFromUrl(response.Url) ?? "document";
                                rawFilename = RemoveDuplicateExtension(rawFilename);
                                Console.WriteLine($"   ?? Intercepted document: {rawFilename} ({buffer.Length / 1024} KB)");

                                var filePath = await SaveNetworkDocument(response.Url, buffer, response.Headers, evidencePath);
                                if (!string.IsNullOrEmpty(filePath))
                                {
                                    Interlocked.Increment(ref networkDocumentCount);
                                }
                            }
                        }
                    }
                    catch (Exception ex)
                    {
                        Console.WriteLine($"   ??  Network intercept error (continuing): {ex.Message.Split('\n')[0]}");
                    }
                };

                var stats = await CrawlWebsiteAsync(page, url, evidencePath, maxPages, antiDetection);
                result.PagesScraped = stats.Pages;
                result.PagesSkipped = stats.Skipped;
                result.DocumentsDownloaded = stats.Documents + networkDocumentCount;
                result.Failed = stats.Failed;
                result.DownloadedFiles = stats.DownloadedFiles;

                await SaveScrapeMetadataAsync(result, evidencePath);
                Console.WriteLine($"\n? Scraping complete!");
                Console.WriteLine($"?? Pages saved: {result.PagesScraped}");
                Console.WriteLine($"? Pages skipped: {result.PagesSkipped}");
                Console.WriteLine($"?? Documents: {result.DocumentsDownloaded}");
                if (result.Failed > 0)
                    Console.WriteLine($"? Failed: {result.Failed}");
                Console.WriteLine($"\n?? Evidence saved to:\n   {evidencePath}\n");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"\n? Error during scraping: {ex.Message}");
                throw;
            }
            finally
            {
                result.EndTime = DateTime.Now;
                await CloseBrowserAsync();
            }

            return result;
        }

        private async Task<(int Pages, int Skipped, int Documents, int Failed, List<string> DownloadedFiles)>
            CrawlWebsiteAsync(IPage page, string startUrl, string evidencePath, int maxPages, bool antiDetection)
        {
            int pagesScraped = 0;
            int pagesSkipped = 0;
            int documentsDownloaded = 0;
            int failed = 0;
            var downloadedFiles = new List<string>();
            var pagesToVisit = new Queue<string>();
            var visitedUrls = new HashSet<string>();
            pagesToVisit.Enqueue(startUrl);

            while (pagesToVisit.Count > 0 && pagesScraped < maxPages)
            {
                var currentUrl = pagesToVisit.Dequeue();
                if (visitedUrls.Contains(currentUrl) || !IsAllowedUrl(currentUrl))
                    continue;
                visitedUrls.Add(currentUrl);

                try
                {
                    Console.WriteLine($"\n?? Scraping: {currentUrl}");
                    if (antiDetection)
                        await Task.Delay(Random.Shared.Next(1500, 3000));

                    var response = await page.GoToAsync(currentUrl, new NavigationOptions
                    {
                        WaitUntil = new[] { WaitUntilNavigation.Networkidle2 },
                        Timeout = 30000
                    });

                    if (response != null && !response.Ok)
                    {
                        Console.WriteLine($"   ??  Page returned status: {response.Status}");
                        if ((int)response.Status >= 400)
                        {
                            failed++;
                            continue;
                        }
                    }

                    var title = await page.GetTitleAsync();
                    var content = await page.GetContentAsync();

                    if (await IsErrorPageAsync(page, title))
                    {
                        Console.WriteLine("   ? Skipping error page");
                        pagesSkipped++;
                        continue;
                    }

                    var textContent = ExtractTextFromHtml(content);
                    var contentAnalysis = AnalyzeContent(textContent, title, currentUrl);

                    var documentLinks = await ScanForDocumentLinksAsync(page);
                    if (documentLinks.Count > 0)
                    {
                        Console.WriteLine($"   ?? Found {documentLinks.Count} document link(s)");
                        foreach (var docUrl in documentLinks)
                        {
                            // ** MODIFIED to handle large files **
                            var downloadResult = await DownloadDocumentAsync(docUrl, page, evidencePath);
                            if (downloadResult.IsLargeFile)
                            {
                                // Hand off to the HttpClient downloader
                                downloadResult = await DownloadLargeFileAsync(docUrl, evidencePath);
                            }

                            if (downloadResult.Success)
                            {
                                documentsDownloaded++;
                                if (!string.IsNullOrEmpty(downloadResult.FilePath))
                                    downloadedFiles.Add(downloadResult.FilePath);
                            }
                        }
                    }

                    if (!contentAnalysis.IsValuable)
                    {
                        if (documentLinks.Count > 0)
                        {
                            Console.WriteLine($"   ? Skipping {contentAnalysis.Reason} page (but got {documentLinks.Count} documents): {title}");
                        }
                        else
                        {
                            Console.WriteLine($"   ? Skipping {contentAnalysis.Reason}: {title}");
                        }
                        pagesSkipped++;
                    }
                    else
                    {
                        await SaveWebpageAsync(currentUrl, title, content, evidencePath, contentAnalysis);
                        pagesScraped++;
                        Console.WriteLine($"   ? Saved page: {title ?? "Untitled"} ({contentAnalysis.WordCount} words)");
                    }

                    var links = await ExtractLinksAsync(page);
                    Console.WriteLine($"   ?? Found {links.Count} links on page.");
                    if (links.Any())
                    {
                        foreach (var link in links.Take(5))
                        {
                            Console.WriteLine($"      -> {link}");
                        }
                        if (links.Count > 5)
                        {
                            Console.WriteLine($"      ... and {links.Count - 5} more.");
                        }
                    }

                    foreach (var link in links)
                        if (!visitedUrls.Contains(link) && IsAllowedUrl(link))
                            pagesToVisit.Enqueue(link);
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"   ? Error: {ex.Message}");
                    failed++;
                }
            }
            return (pagesScraped, pagesSkipped, documentsDownloaded, failed, downloadedFiles);
        }

        private string ExtractTextFromHtml(string html)
        {
            if (string.IsNullOrWhiteSpace(html)) return string.Empty;
            var text = Regex.Replace(html, @"<script[^>]*>[\s\S]*?</script>", " ", RegexOptions.IgnoreCase | RegexOptions.Multiline);
            text = Regex.Replace(text, @"<style[^>]*>[\s\S]*?</style>", " ", RegexOptions.IgnoreCase | RegexOptions.Multiline);
            text = Regex.Replace(text, @"", " ");
            text = Regex.Replace(text, @"<[^>]+>", " ");
            text = System.Net.WebUtility.HtmlDecode(text);
            text = Regex.Replace(text, @"\s+", " ");
            return text.Trim();
        }

        private ContentAnalysis AnalyzeContent(string textContent, string? title, string url)
        {
            var analysis = new ContentAnalysis();
            if (textContent.Length < MinimumContentLength)
            {
                analysis.IsValuable = false;
                analysis.Reason = "too short";
                return analysis;
            }
            var words = textContent.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            analysis.WordCount = words.Length;
            if (analysis.WordCount < MinimumWordCount)
            {
                analysis.IsValuable = false;
                analysis.Reason = "insufficient content";
                return analysis;
            }
            var linkCount = Regex.Matches(textContent, @"https?://").Count + Regex.Matches(textContent, @"www\.").Count;
            var linkRatio = (double)linkCount / analysis.WordCount;
            if (linkRatio > MaxLinkToWordRatio)
            {
                analysis.IsValuable = false;
                analysis.Reason = "navigation/menu page";
                return analysis;
            }
            var navKeywords = new[] { "sitemap", "menu", "navigation", "index", "directory", "404", "error" };
            var titleLower = (title ?? "").ToLower();
            var urlLower = url.ToLower();
            if (navKeywords.Any(k => titleLower.Contains(k) || urlLower.Contains(k)))
            {
                analysis.IsValuable = false;
                analysis.Reason = "navigation page";
                return analysis;
            }
            var valuableKeywords = new[] { "policy", "privacy", "compliance", "governance", "security", "procedure", "standard", "guideline", "regulation", "data", "personal", "information", "consent", "rights", "obligation" };
            var textLower = textContent.ToLower();
            analysis.ValueScore = valuableKeywords.Count(k => textLower.Contains(k));
            if (analysis.ValueScore >= 3)
            {
                analysis.IsValuable = true;
                analysis.Reason = "governance content";
                analysis.ContentType = "governance";
                return analysis;
            }
            var sentences = Regex.Split(textContent, @"[.!?]+");
            var avgSentenceLength = sentences.Average(s => s.Split(' ').Length);
            if (avgSentenceLength < 5)
            {
                analysis.IsValuable = false;
                analysis.Reason = "list/menu structure";
                return analysis;
            }
            analysis.IsValuable = true;
            analysis.Reason = "general content";
            analysis.ContentType = "general";
            return analysis;
        }

        private async Task SaveWebpageAsync(string url, string? title, string content, string evidencePath, ContentAnalysis analysis)
        {
            var urlHash = ComputeHash(Encoding.UTF8.GetBytes(url)).Substring(0, 8);
            var safeName = SanitizeFilename(new Uri(url).AbsolutePath.Trim('/').Replace('/', '_'));
            if (string.IsNullOrEmpty(safeName)) safeName = "index";
            var htmlPath = Path.Combine(evidencePath, "webpages", $"{safeName}_{urlHash}.html");
            await File.WriteAllTextAsync(htmlPath, content);
            var metadata = new { Url = url, Title = title, ScrapedAt = DateTime.UtcNow, ContentLength = content.Length, analysis.WordCount, analysis.ContentType, analysis.ValueScore, analysis.IsValuable };
            var metaPath = Path.Combine(evidencePath, "webpages", $"{safeName}_{urlHash}.meta.json");
            await File.WriteAllTextAsync(metaPath, JsonSerializer.Serialize(metadata, new JsonSerializerOptions { WriteIndented = true }));
        }

        private class ContentAnalysis
        {
            public bool IsValuable { get; set; } = false;
            public string Reason { get; set; } = "";
            public int WordCount { get; set; }
            public int ValueScore { get; set; }
            public string ContentType { get; set; } = "unknown";
        }

        #region Helper Methods

        private async Task CreateDirectoryStructureAsync(string evidencePath)
        {
            Directory.CreateDirectory(evidencePath);
            Directory.CreateDirectory(Path.Combine(evidencePath, "webpages"));
            Directory.CreateDirectory(Path.Combine(evidencePath, "documents"));
            Directory.CreateDirectory(Path.Combine(evidencePath, "_metadata"));
            await Task.CompletedTask;
        }

        private void SetupAllowedDomains(string startUrl)
        {
            var uri = new Uri(startUrl);
            allowedDomains.Clear();
            string host = uri.Host;
            string mainDomain = host;
            if (host.StartsWith("www.", StringComparison.OrdinalIgnoreCase))
            {
                mainDomain = host.Substring(4);
            }
            allowedDomains.Add(mainDomain);
            Console.WriteLine($"?? Scrape scope set to domain: {mainDomain} (and all its subdomains)");
        }

        private bool IsAllowedUrl(string url)
        {
            try
            {
                var uri = new Uri(url);
                string host = uri.Host;
                return allowedDomains.Any(domain =>
                    host.Equals(domain, StringComparison.OrdinalIgnoreCase) ||
                    host.EndsWith("." + domain, StringComparison.OrdinalIgnoreCase));
            }
            catch { return false; }
        }

        private async Task InitializeBrowserAsync(bool antiDetection)
        {
            var options = new LaunchOptions { Headless = true, Args = antiDetection ? new[] { "--disable-blink-features=AutomationControlled", "--no-sandbox" } : new[] { "--no-sandbox" } };
            browser = await Puppeteer.LaunchAsync(options);
        }

        private async Task CloseBrowserAsync()
        {
            if (browser != null)
            {
                await browser.CloseAsync();
                browser = null;
            }
        }

        private async Task ConfigureCDPDownloadBehavior(IPage page, string downloadPath)
        {
            Directory.CreateDirectory(downloadPath);
            try
            {
                await page.Client.SendAsync("Page.setDownloadBehavior", new { behavior = "allow", downloadPath = downloadPath });
            }
            catch (Exception ex)
            {
                Console.WriteLine($"   ??  CDP configuration warning: {ex.Message}");
            }
        }

        private async Task<string?> SaveNetworkDocument(string url, byte[] buffer, Dictionary<string, string> headers, string evidencePath)
        {
            var contentHash = ComputeHash(buffer.Take(4096).ToArray());
            lock (downloadLock)
            {
                if (downloadedContentHashes.Contains(contentHash))
                {
                    Console.WriteLine($"   ? Duplicate document skipped");
                    return null;
                }
                downloadedContentHashes.Add(contentHash);
            }
            var filename = ExtractFilenameFromUrl(url) ?? $"document_{contentHash.Substring(0, 8)}";
            filename = RemoveDuplicateExtension(filename);
            var extension = Path.GetExtension(filename);
            if (string.IsNullOrEmpty(extension))
            {
                extension = GetExtensionFromMimeType(headers.GetValueOrDefault("content-type", ""));
                filename = filename + extension;
            }
            var safeName = SanitizeFilename(filename);
            var filePath = Path.Combine(evidencePath, "documents", safeName);
            await File.WriteAllBytesAsync(filePath, buffer);
            await SaveDownloadMetadataAsync(url, filePath, contentHash);
            Console.WriteLine($"   ? Saved via network: {safeName} ({buffer.Length / 1024} KB)");
            return filePath;
        }

        private string RemoveDuplicateExtension(string filename)
        {
            if (string.IsNullOrEmpty(filename)) return filename;
            var extension = Path.GetExtension(filename);
            if (string.IsNullOrEmpty(extension)) return filename;
            var doubleExtension = extension + extension;
            if (filename.EndsWith(doubleExtension, StringComparison.OrdinalIgnoreCase))
            {
                return filename.Substring(0, filename.Length - extension.Length);
            }
            return filename;
        }

        // *** NEW METHOD for streaming large files ***
        private async Task<DownloadResult> DownloadLargeFileAsync(string docUrl, string evidencePath)
        {
            try
            {
                using var client = new HttpClient();
                client.DefaultRequestHeaders.Add("User-Agent", "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/108.0.0.0 Safari/537.36");

                using var response = await client.GetAsync(docUrl, HttpCompletionOption.ResponseHeadersRead);
                response.EnsureSuccessStatusCode();

                var headers = response.Content.Headers.ToDictionary(h => h.Key.ToLowerInvariant(), h => h.Value.FirstOrDefault() ?? "");
                if (response.Headers.TryGetValues("content-disposition", out var values))
                {
                    headers["content-disposition"] = values.FirstOrDefault() ?? "";
                }

                var filename = ExtractFilenameFromHeaders(headers) ?? ExtractFilenameFromUrl(docUrl) ?? $"document_{Guid.NewGuid():N}.bin";
                filename = RemoveDuplicateExtension(filename);
                var extension = Path.GetExtension(filename);
                if (string.IsNullOrEmpty(extension))
                {
                    filename += GetExtensionFromMimeType(headers.GetValueOrDefault("content-type"));
                }

                var safeName = SanitizeFilename(filename);
                var filePath = Path.Combine(evidencePath, "documents", safeName);

                var urlHash = ComputeHash(Encoding.UTF8.GetBytes(docUrl));
                lock (downloadLock)
                {
                    if (downloadedUrlHashes.Contains(urlHash)) return new DownloadResult(false, null);
                    downloadedUrlHashes.Add(urlHash);
                }

                Console.WriteLine($"   ?? Streaming large file: {safeName}");
                await using var stream = await response.Content.ReadAsStreamAsync();
                await using var fileStream = new FileStream(filePath, FileMode.Create, FileAccess.Write, FileShare.None);
                await stream.CopyToAsync(fileStream);

                await SaveDownloadMetadataAsync(docUrl, filePath, "HASH_SKIPPED_LARGE_FILE");
                Console.WriteLine($"   ? Saved large file: {safeName} ({fileStream.Length / 1024 / 1024} MB)");
                return new DownloadResult(true, filePath);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"   ?? Large file download failed for {docUrl}: {ex.Message}");
                return new DownloadResult(false, null);
            }
        }

        private async Task<DownloadResult> DownloadDocumentAsync(string docUrl, IPage page, string evidencePath)
        {
            try
            {
                var urlHash = ComputeHash(Encoding.UTF8.GetBytes(docUrl));
                lock (downloadLock)
                {
                    if (downloadedUrlHashes.Contains(urlHash)) return new DownloadResult(false, null);
                    downloadedUrlHashes.Add(urlHash);
                }

                // ** MODIFIED Javascript to detect large files **
                var jsCode = @"
                    async (url) => {
                        try {
                            const controller = new AbortController();
                            const timeoutId = setTimeout(() => controller.abort(), 15000);
                            const response = await fetch(url, { method: 'GET', credentials: 'same-origin', signal: controller.signal });
                            clearTimeout(timeoutId);

                            if (!response.ok) {
                                return { success: false, status: response.status };
                            }

                            const contentLength = response.headers.get('content-length');
                            if (contentLength && parseInt(contentLength) > 50 * 1024 * 1024) { // 50MB limit
                                return { success: false, error: 'File too large', isLargeFile: true };
                            }

                            const buffer = await response.arrayBuffer();
                            const bytes = new Uint8Array(buffer);
                            return {
                                success: true,
                                data: Array.from(bytes),
                                contentType: response.headers.get('content-type') || '',
                                contentDisposition: response.headers.get('content-disposition') || ''
                            };
                        } catch (error) {
                            return { success: false, error: error.message };
                        }
                    }
                ";

                JsonElement resultJson;
                try
                {
                    using (var cts = new CancellationTokenSource(TimeSpan.FromSeconds(20)))
                    {
                        resultJson = await page.EvaluateFunctionAsync<JsonElement>(jsCode, docUrl);
                    }
                }
                catch (Exception evalEx)
                {
                    Console.WriteLine($"   ??  Download evaluation failed: {evalEx.Message.Split('\n')[0]}");
                    return new DownloadResult(false, null);
                }

                if (!resultJson.TryGetProperty("success", out var successProp) || !successProp.GetBoolean())
                {
                    // ** MODIFIED to check for the isLargeFile flag **
                    if (resultJson.TryGetProperty("isLargeFile", out var isLarge) && isLarge.GetBoolean())
                    {
                        return new DownloadResult(false, null, IsLargeFile: true);
                    }
                    if (resultJson.TryGetProperty("error", out var errorProp))
                    {
                        Console.WriteLine($"   ?? Download failed: {errorProp.GetString()}");
                    }
                    return new DownloadResult(false, null);
                }

                if (!resultJson.TryGetProperty("data", out var dataProp))
                {
                    return new DownloadResult(false, null);
                }
                var dataArray = dataProp.EnumerateArray().Select(e => (byte)e.GetInt32()).ToArray();
                if (dataArray.Length == 0) return new DownloadResult(false, null);
                if (IsLikelyHtmlError(dataArray))
                {
                    Console.WriteLine($"   ??  Received HTML error page instead of document: {docUrl}");
                    return new DownloadResult(false, null);
                }
                var contentHash = ComputeHash(dataArray.Take(4096).ToArray());
                lock (downloadLock)
                {
                    if (downloadedContentHashes.Contains(contentHash)) return new DownloadResult(false, null);
                    downloadedContentHashes.Add(contentHash);
                }

                var headers = new Dictionary<string, string>();
                if (resultJson.TryGetProperty("contentType", out var contentTypeProp)) headers["content-type"] = contentTypeProp.GetString() ?? "";
                if (resultJson.TryGetProperty("contentDisposition", out var contentDispProp)) headers["content-disposition"] = contentDispProp.GetString() ?? "";

                var filename = ExtractFilenameFromHeaders(headers) ?? ExtractFilenameFromUrl(docUrl) ?? $"document_{urlHash.Substring(0, 8)}";
                var extension = Path.GetExtension(filename);
                if (string.IsNullOrEmpty(extension))
                {
                    filename += GetExtensionFromMimeType(headers.GetValueOrDefault("content-type", ""));
                }
                var safeName = SanitizeFilename(filename);
                var filePath = Path.Combine(evidencePath, "documents", safeName);
                await File.WriteAllBytesAsync(filePath, dataArray);
                await SaveDownloadMetadataAsync(docUrl, filePath, contentHash);
                Console.WriteLine($"   ? Downloaded: {safeName} ({dataArray.Length / 1024} KB)");
                return new DownloadResult(true, filePath);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"   ??  Download failed for {docUrl}: {ex.Message}");
                return new DownloadResult(false, null);
            }
        }


        private bool IsLikelyHtmlError(byte[] buffer)
        {
            if (buffer.Length < 100) return false;
            var start = Encoding.UTF8.GetString(buffer.Take(500).ToArray()).ToLower();
            if (start.Contains("<!doctype html") || start.Contains("<html") || start.Contains("<head") || start.Contains("<?xml"))
            {
                if (start.Contains("error") || start.Contains("404") || start.Contains("403") || start.Contains("unauthorized") || start.Contains("access denied"))
                {
                    return true;
                }
                return true;
            }
            if (buffer.Length >= 4)
            {
                var pdfSignature = new byte[] { 0x25, 0x50, 0x44, 0x46 }; // %PDF
                if (buffer.Take(4).SequenceEqual(pdfSignature)) return false;
            }
            return false;
        }

        private async Task<bool> IsErrorPageAsync(IPage page, string? title)
        {
            if (string.IsNullOrEmpty(title)) return false;
            var errorIndicators = new[] { "404", "403", "500", "error", "not found", "forbidden", "denied" };
            var titleLower = title.ToLower();
            if (errorIndicators.Any(e => titleLower.Contains(e)))
            {
                var bodyText = await page.EvaluateFunctionAsync<string>("() => document.body?.innerText || ''");
                return bodyText.Length < 500;
            }
            return false;
        }

        private string ComputeHash(byte[] data)
        {
            using var sha256 = SHA256.Create();
            var hash = sha256.ComputeHash(data);
            return BitConverter.ToString(hash).Replace("-", "").ToLower();
        }

        private string SanitizeFilename(string filename)
        {
            var invalid = Path.GetInvalidFileNameChars();
            var sanitized = string.Join("_", filename.Split(invalid));
            return sanitized.Length > 200 ? sanitized.Substring(0, 200) : sanitized;
        }

        private string? ExtractFilenameFromUrl(string url)
        {
            try
            {
                var uri = new Uri(url);
                var path = uri.AbsolutePath;
                if (!string.IsNullOrEmpty(path) && path != "/")
                {
                    var filename = Path.GetFileName(path);
                    if (!string.IsNullOrEmpty(filename)) return filename;
                }
            }
            catch { }
            return null;
        }

        private string? ExtractFilenameFromHeaders(Dictionary<string, string> headers)
        {
            if (headers.TryGetValue("content-disposition", out var disposition))
            {
                try
                {
                    var content = ContentDispositionHeaderValue.Parse(disposition);
                    return content.FileName?.Trim('"');
                }
                catch
                {
                    var match = Regex.Match(disposition, @"filename[*]?=[""']?([^;""']+)");
                    if (match.Success) return match.Groups[1].Value;
                }
            }
            return null;
        }

        private string GetExtensionFromMimeType(string? mimeType)
        {
            if (string.IsNullOrEmpty(mimeType)) return ".bin";
            return mimeType.ToLower() switch
            {
                var mt when mt.Contains("pdf") => ".pdf",
                var mt when mt.Contains("word") => ".docx",
                var mt when mt.Contains("excel") => ".xlsx",
                var mt when mt.Contains("powerpoint") => ".pptx",
                var mt when mt.Contains("text") => ".txt",
                var mt when mt.Contains("csv") => ".csv",
                var mt when mt.Contains("zip") => ".zip",
                _ => ".bin"
            };
        }

        private async Task<List<string>> ScanForDocumentLinksAsync(IPage page)
        {
            return await page.EvaluateFunctionAsync<List<string>>(@"
                () => {
                    const links = [];
                    const anchors = document.querySelectorAll('a[href]');
                    const extensions = ['.pdf', '.doc', '.docx', '.xls', '.xlsx', '.ppt', '.pptx'];
                    anchors.forEach(anchor => {
                        const href = anchor.href;
                        if (href && extensions.some(ext => href.toLowerCase().includes(ext))) {
                            links.push(href);
                        }
                    });
                    document.querySelectorAll('a[download]').forEach(anchor => {
                        if (anchor.href && !links.includes(anchor.href)) {
                            links.push(anchor.href);
                        }
                    });
                    return [...new Set(links)];
                }
            ");
        }

        private async Task<List<string>> ExtractLinksAsync(IPage page)
        {
            return await page.EvaluateFunctionAsync<List<string>>(@"
                () => {
                    const links = [];
                    document.querySelectorAll('a[href]').forEach(anchor => {
                        const href = anchor.href;
                        if (href && !href.includes('#') && !href.startsWith('mailto:') && !href.startsWith('tel:') && !href.startsWith('javascript:') && !href.startsWith('data:')) {
                            links.push(href);
                        }
                    });
                    return [...new Set(links)];
                }
            ");
        }

        private async Task SaveDownloadMetadataAsync(string originalUrl, string filePath, string contentHash)
        {
            var fileInfo = new FileInfo(filePath);
            var metadata = new { OriginalUrl = originalUrl, Filename = fileInfo.Name, FilePath = filePath, Size = fileInfo.Length, Hash = contentHash, DownloadTime = DateTime.UtcNow };
            var metaPath = Path.ChangeExtension(filePath, ".meta.json");
            await File.WriteAllTextAsync(metaPath, JsonSerializer.Serialize(metadata, new JsonSerializerOptions { WriteIndented = true }));
        }

        private async Task SaveScrapeMetadataAsync(ScrapeResult result, string evidencePath)
        {
            var metadata = new { result.SessionId, result.TargetUrl, result.StartTime, result.EndTime, Duration = (result.EndTime - result.StartTime).TotalMinutes, Stats = new { PagesSaved = result.PagesScraped, PagesSkipped = result.PagesSkipped, Documents = result.DocumentsDownloaded, Failed = result.Failed }, Files = result.DownloadedFiles };
            var metaPath = Path.Combine(evidencePath, "_metadata", "scrape_summary.json");
            Directory.CreateDirectory(Path.GetDirectoryName(metaPath)!);
            await File.WriteAllTextAsync(metaPath, JsonSerializer.Serialize(metadata, new JsonSerializerOptions { WriteIndented = true }));
        }

        #endregion

        public void Dispose()
        {
            browser?.Dispose();
        }
    }
}