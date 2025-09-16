// Services/WebScraperService.cs — Patched
// - Derives file extension from content-type
// - Avoids saving HTML responses under /documents
// - Keeps URL/content hash dedupe
// - Preserves existing crawl & evidence layout

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using PuppeteerSharp;

namespace PrivacyLens.Services
{
    public class WebScraperService
    {
        private readonly string appPath;
        private IBrowser? browser;
        private readonly HashSet<string> downloadedContentHashes;
        private readonly HashSet<string> allowedDomains;

        // Document MIME types used earlier; retained for reference,
        // but extension/doctype is now determined from Content-Type via helper.
        private static readonly HashSet<string> DocumentMimeTypes = new HashSet<string>
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
            "application/zip"
        };

        // File extensions to look for when scanning DOM anchors (unchanged)
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
            public int DocumentsDownloaded { get; set; }
            public int Failed { get; set; }
            public DateTime StartTime { get; set; }
            public DateTime EndTime { get; set; }
            public string TargetUrl { get; set; } = string.Empty;
        }

        public WebScraperService(string? basePath = null)
        {
            appPath = basePath ?? AppDomain.CurrentDomain.BaseDirectory;
            downloadedContentHashes = new HashSet<string>();
            allowedDomains = new HashSet<string>();
        }

        /// <summary>
        /// Main scraping method that handles both corporate and application websites
        /// </summary>
        public async Task<ScrapeResult> ScrapeWebsiteAsync(
            string url,
            ScrapeTarget target,
            bool antiDetection = false,
            int maxPages = 50)
        {
            // Determine evidence path based on target
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

            Console.WriteLine($"\n🔎 Starting {target} scrape");
            Console.WriteLine($" URL: {url}");
            Console.WriteLine($" Session: {result.SessionId}");
            Console.WriteLine($" Mode: {(antiDetection ? "Stealth" : "Fast")}");
            Console.WriteLine($" Max pages: {maxPages}\n");

            try
            {
                // Create directory structure
                await CreateDirectoryStructureAsync(evidencePath);

                // Set up allowed domains
                SetupAllowedDomains(url);

                // Initialize browser
                await InitializeBrowserAsync(antiDetection);

                // Create page context
                var page = await browser!.NewPageAsync();

                // Set up network interception for document detection
                await SetupNetworkInterceptionAsync(page, evidencePath);

                // Perform the crawl
                var stats = await CrawlWebsiteAsync(page, url, evidencePath, maxPages, antiDetection);
                result.PagesScraped = stats.Pages;
                result.DocumentsDownloaded = stats.Documents;
                result.Failed = stats.Failed;

                // Save metadata
                await SaveScrapeMetadataAsync(result, evidencePath);

                Console.WriteLine($"\n✅ Scraping complete!");
                Console.WriteLine($" Pages: {result.PagesScraped}");
                Console.WriteLine($" Documents: {result.DocumentsDownloaded}");
                Console.WriteLine($" Failed: {result.Failed}");
                Console.WriteLine($"\n📁 Evidence saved to:\n {evidencePath}\n");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"\n✗ Error during scraping: {ex.Message}");
                throw;
            }
            finally
            {
                result.EndTime = DateTime.Now;
                await browser?.CloseAsync()!;
            }

            return result;
        }

        private async Task<(int Pages, int Documents, int Failed)> CrawlWebsiteAsync(
            IPage page,
            string startUrl,
            string evidencePath,
            int maxPages,
            bool antiDetection)
        {
            int pagesScraped = 0;
            int documentsDownloaded = 0;
            int failed = 0;

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
                    Console.WriteLine($" ••• Scraping: {currentUrl}");

                    // Apply delay if anti-detection
                    if (antiDetection)
                        await Task.Delay(Random.Shared.Next(1500, 3000));

                    var response = await page.GoToAsync(currentUrl, new NavigationOptions
                    {
                        WaitUntil = new[] { WaitUntilNavigation.DOMContentLoaded },
                        Timeout = 30000
                    });

                    if (!response.Ok)
                    {
                        Console.WriteLine($"   ◦ Page returned status: {response.Status}");
                        failed++;
                        continue;
                    }

                    var title = await page.GetTitleAsync();
                    var content = await page.GetContentAsync();

                    // Check for error pages
                    if (await IsErrorPageAsync(page, title))
                    {
                        Console.WriteLine("   ◦ Skipping error page");
                        continue;
                    }

                    // Scan for document links (DOM heuristic)
                    var documentLinks = await ScanForDocumentLinksAsync(page);
                    if (documentLinks.Count > 0)
                    {
                        Console.WriteLine($"   ◦ Found {documentLinks.Count} document link(s)");
                        foreach (var docUrl in documentLinks)
                        {
                            if (await DownloadDocumentAsync(docUrl, page, evidencePath))
                            {
                                documentsDownloaded++;
                            }
                        }
                    }

                    // Save webpage
                    await SaveWebpageAsync(currentUrl, title, content, evidencePath);
                    pagesScraped++;
                    Console.WriteLine($"   ✓ Saved page: {title ?? "Untitled"}");

                    // Extract links for crawling
                    var links = await ExtractLinksAsync(page);
                    foreach (var link in links)
                        if (!visitedUrls.Contains(link) && IsAllowedUrl(link))
                            pagesToVisit.Enqueue(link);
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"   ◦ Error: {ex.Message}");
                    failed++;
                }
            }

            return (pagesScraped, documentsDownloaded, failed);
        }

        private async Task<bool> DownloadDocumentAsync(string url, IPage page, string evidencePath)
        {
            try
            {
                var browser = page.Browser;
                if (browser == null) return false;

                var downloadPage = await browser.NewPageAsync();
                try
                {
                    var response = await downloadPage.GoToAsync(url, new NavigationOptions
                    {
                        WaitUntil = new[] { WaitUntilNavigation.DOMContentLoaded },
                        Timeout = 30000
                    });

                    if (response.Ok)
                    {
                        // Use response content-type for extension decision
                        var contentType = response.Headers.ContainsKey("content-type")
                            ? response.Headers["content-type"].Split(';')[0].Trim()
                            : "";

                        // Do NOT store HTML as a document
                        if (string.Equals(contentType, "text/html", StringComparison.OrdinalIgnoreCase))
                            return false;

                        if (!IsDocumentContentType(contentType))
                            return false;

                        var buffer = await response.BufferAsync();

                        // Check for duplicates by URL and by content hash
                        var urlHash = ComputeHash(Encoding.UTF8.GetBytes(response.Url));
                        if (downloadedContentHashes.Contains(urlHash))
                            return false;

                        var contentHash = ComputeHash(buffer);
                        if (downloadedContentHashes.Contains(contentHash))
                            return false;

                        downloadedContentHashes.Add(urlHash);
                        downloadedContentHashes.Add(contentHash);

                        // Build filename from URL and content-type
                        var filename = ExtractFilename(response.Url);
                        var safeName = SanitizeFilename(filename);

                        var extFromType = ExtensionFromContentType(contentType);
                        if (!Path.HasExtension(safeName))
                        {
                            safeName += string.IsNullOrEmpty(extFromType) ? ".bin" : extFromType;
                        }
                        else
                        {
                            if (!string.IsNullOrEmpty(extFromType) &&
                                !safeName.EndsWith(extFromType, StringComparison.OrdinalIgnoreCase))
                            {
                                safeName = Path.ChangeExtension(safeName, extFromType);
                            }
                        }

                        var docPath = Path.Combine(evidencePath, "documents", safeName);
                        int counter = 1;
                        while (File.Exists(docPath))
                        {
                            var nameWithoutExt = Path.GetFileNameWithoutExtension(safeName);
                            var ext = Path.GetExtension(safeName);
                            safeName = $"{nameWithoutExt}_{counter}{ext}";
                            docPath = Path.Combine(evidencePath, "documents", safeName);
                            counter++;
                        }

                        await File.WriteAllBytesAsync(docPath, buffer);
                        Console.WriteLine($"   ◦ Downloaded: {safeName}");

                        // Save metadata
                        var metadata = new
                        {
                            OriginalUrl = url,
                            Filename = safeName,
                            Size = buffer.Length,
                            Hash = contentHash,
                            DownloadTime = DateTime.UtcNow
                        };
                        var metaPath = Path.Combine(evidencePath, "documents", $"{safeName}.meta.json");
                        await File.WriteAllTextAsync(metaPath, JsonSerializer.Serialize(metadata, new JsonSerializerOptions { WriteIndented = true }));

                        return true;
                    }
                }
                finally
                {
                    await downloadPage.CloseAsync();
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"   ◦ Download failed: {ex.Message}");
            }

            return false;
        }

        private async Task InitializeBrowserAsync(bool antiDetection)
        {
            var browserFetcher = new BrowserFetcher();
            await browserFetcher.DownloadAsync();

            var launchOptions = new LaunchOptions
            {
                Headless = true,
                Args = new[] { "--no-sandbox", "--disable-dev-shm-usage" }
            };

            if (antiDetection)
            {
                var args = launchOptions.Args.ToList();
                args.Add("--disable-blink-features=AutomationControlled");
                launchOptions.Args = args.ToArray();
            }

            browser = await Puppeteer.LaunchAsync(launchOptions);
        }

        private async Task SetupNetworkInterceptionAsync(IPage page, string evidencePath)
        {
            await page.SetRequestInterceptionAsync(true);

            page.Response += async (sender, e) =>
            {
                try
                {
                    var response = e.Response;
                    var contentType = response.Headers.ContainsKey("content-type")
                        ? response.Headers["content-type"].Split(';')[0].Trim()
                        : "";

                    // Only treat as document if it's a known doc type; skip HTML
                    if (!response.Ok || !IsDocumentContentType(contentType) || contentType.Equals("text/html", StringComparison.OrdinalIgnoreCase))
                        return;

                    // Avoid duplicate URL processing
                    var urlHash = ComputeHash(Encoding.UTF8.GetBytes(response.Url));
                    if (downloadedContentHashes.Contains(urlHash))
                        return;

                    Console.WriteLine($"   ◦ Intercepted document: {response.Url}");
                    var buffer = await response.BufferAsync();

                    // Mark URL + content hash as processed
                    downloadedContentHashes.Add(urlHash);
                    var contentHash = ComputeHash(buffer);
                    if (downloadedContentHashes.Contains(contentHash))
                        return;

                    downloadedContentHashes.Add(contentHash);

                    var filename = ExtractFilename(response.Url);
                    var safeName = SanitizeFilename(filename);

                    var extFromType = ExtensionFromContentType(contentType);
                    if (!Path.HasExtension(safeName))
                    {
                        safeName += string.IsNullOrEmpty(extFromType) ? ".bin" : extFromType;
                    }
                    else
                    {
                        if (!string.IsNullOrEmpty(extFromType) &&
                            !safeName.EndsWith(extFromType, StringComparison.OrdinalIgnoreCase))
                        {
                            safeName = Path.ChangeExtension(safeName, extFromType);
                        }
                    }

                    var docPath = Path.Combine(evidencePath, "documents", safeName);
                    int counter = 1;
                    while (File.Exists(docPath))
                    {
                        var nameWithoutExt = Path.GetFileNameWithoutExtension(safeName);
                        var ext = Path.GetExtension(safeName);
                        safeName = $"{nameWithoutExt}_{counter}{ext}";
                        docPath = Path.Combine(evidencePath, "documents", safeName);
                        counter++;
                    }

                    await File.WriteAllBytesAsync(docPath, buffer);
                    Console.WriteLine($"   ◦ Saved: {safeName}");
                }
                catch
                {
                    // Silent fail for network interception
                }
            };

            page.Request += async (sender, e) => await e.Request.ContinueAsync();
        }

        private async Task<List<string>> ScanForDocumentLinksAsync(IPage page)
        {
            return await page.EvaluateFunctionAsync<List<string>>(@" 
                () => {
                    const links = [];
                    const anchors = document.querySelectorAll('a[href]');
                    const extensions = ['.pdf', '.doc', '.docx', '.xls', '.xlsx', '.ppt', '.pptx', '.txt', '.csv', '.rtf', '.zip'];
                    anchors.forEach(anchor => {
                        const href = anchor.href;
                        if (extensions.some(ext => href.toLowerCase().includes(ext))) {
                            links.push(href);
                        }
                    });
                    return links;
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
                        if (href && !href.includes('#') && !href.startsWith('mailto:') &&
                            !href.startsWith('tel:') && !href.startsWith('javascript:')) {
                            links.push(href);
                        }
                    });
                    return links;
                }
            ");
        }

        private async Task SaveWebpageAsync(string url, string title, string content, string evidencePath)
        {
            var urlHash = ComputeHash(Encoding.UTF8.GetBytes(url)).Substring(0, 8);
            var safeName = SanitizeFilename(new Uri(url).AbsolutePath.Trim('/').Replace('/', '_'));
            if (string.IsNullOrEmpty(safeName)) safeName = "index";

            var htmlPath = Path.Combine(evidencePath, "webpages", $"{safeName}_{urlHash}.html");
            await File.WriteAllTextAsync(htmlPath, content);

            var metadata = new
            {
                Url = url,
                Title = title,
                ScrapedAt = DateTime.UtcNow,
                ContentLength = content.Length
            };
            var metaPath = Path.Combine(evidencePath, "webpages", $"{safeName}_{urlHash}.meta.json");
            await File.WriteAllTextAsync(metaPath, JsonSerializer.Serialize(metadata, new JsonSerializerOptions { WriteIndented = true }));
        }

        private async Task SaveScrapeMetadataAsync(ScrapeResult result, string evidencePath)
        {
            var metadata = new
            {
                result.SessionId,
                result.TargetUrl,
                result.StartTime,
                result.EndTime,
                Stats = new
                {
                    Pages = result.PagesScraped,
                    Documents = result.DocumentsDownloaded,
                    Failed = result.Failed
                },
                Duration = (result.EndTime - result.StartTime).TotalSeconds
            };
            var metadataPath = Path.Combine(evidencePath, "scrape_metadata.json");
            await File.WriteAllTextAsync(metadataPath, JsonSerializer.Serialize(metadata, new JsonSerializerOptions { WriteIndented = true }));
        }

        private async Task CreateDirectoryStructureAsync(string evidencePath)
        {
            Directory.CreateDirectory(Path.Combine(evidencePath, "webpages"));
            Directory.CreateDirectory(Path.Combine(evidencePath, "documents"));
            await Task.CompletedTask;
        }

        private void SetupAllowedDomains(string url)
        {
            var uri = new Uri(url);
            var mainDomain = uri.Host.Replace("www.", "");
            allowedDomains.Clear();
            allowedDomains.Add(mainDomain);

            // Add common subdomains
            var subdomains = new[] { "www", "docs", "support", "help", "privacy", "legal" };
            foreach (var sub in subdomains)
                allowedDomains.Add($"{sub}.{mainDomain}");
        }

        private bool IsAllowedUrl(string url)
        {
            try
            {
                var uri = new Uri(url);
                if (uri.Scheme != "http" && uri.Scheme != "https") return false;
                var domain = uri.Host.Replace("www.", "");
                return allowedDomains.Any(allowed =>
                    domain.Equals(allowed, StringComparison.OrdinalIgnoreCase) ||
                    domain.EndsWith($".{allowed}", StringComparison.OrdinalIgnoreCase));
            }
            catch
            {
                return false;
            }
        }

        private async Task<bool> IsErrorPageAsync(IPage page, string title)
        {
            var text = await page.EvaluateFunctionAsync<string>("() => document.body?.innerText || ''");
            var indicators = new[] { "404", "not found", "error", "forbidden", "access denied" };
            var content = $"{title} {text}".ToLower();
            return indicators.Any(indicator => content.Contains(indicator)) && text.Length < 1000;
        }

        private string ExtractFilename(string url)
        {
            try
            {
                var uri = new Uri(url);
                var filename = Path.GetFileName(uri.LocalPath);

                if (!string.IsNullOrEmpty(filename))
                {
                    // Remove query parameters if any
                    var queryIndex = filename.IndexOf('?');
                    if (queryIndex > 0)
                        filename = filename.Substring(0, queryIndex);

                    // Fix duplicate extensions (e.g., file.pdf.pdf -> file.pdf)
                    var ext = Path.GetExtension(filename);
                    if (!string.IsNullOrEmpty(ext))
                    {
                        var nameWithoutExt = Path.GetFileNameWithoutExtension(filename);
                        if (nameWithoutExt.EndsWith(ext, StringComparison.OrdinalIgnoreCase))
                            filename = nameWithoutExt;
                    }

                    return filename;
                }

                return $"document_{Guid.NewGuid():N}.dat";
            }
            catch
            {
                return $"document_{Guid.NewGuid():N}.dat";
            }
        }

        private string SanitizeFilename(string name)
        {
            var invalid = Path.GetInvalidFileNameChars();
            var sanitized = string.Join("_", name.Split(invalid, StringSplitOptions.RemoveEmptyEntries));
            return sanitized.Length > 100 ? sanitized.Substring(0, 100) : sanitized;
        }

        private string ComputeHash(byte[] data)
        {
            using var sha256 = SHA256.Create();
            var hashBytes = sha256.ComputeHash(data.Take(4096).ToArray());
            return BitConverter.ToString(hashBytes).Replace("-", "").ToLower();
        }

        // ===== New helpers: content-type → extension, and doc type predicate =====

        private static string ExtensionFromContentType(string contentType)
        {
            contentType = (contentType ?? "").Split(';')[0].Trim().ToLowerInvariant();
            return contentType switch
            {
                "application/pdf" => ".pdf",
                "application/msword" => ".doc",
                "application/vnd.openxmlformats-officedocument.wordprocessingml.document" => ".docx",
                "application/vnd.ms-excel" => ".xls",
                "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet" => ".xlsx",
                "application/vnd.ms-powerpoint" => ".ppt",
                "application/vnd.openxmlformats-officedocument.presentationml.presentation" => ".pptx",
                "text/plain" => ".txt",
                "text/csv" => ".csv",
                "application/rtf" => ".rtf",
                "application/zip" => ".zip",
                "text/html" => ".html",
                _ => ""
            };
        }

        private static bool IsDocumentContentType(string contentType)
        {
            var ext = ExtensionFromContentType(contentType);
            return !string.IsNullOrEmpty(ext) && ext != ".html";
        }
    }
}
