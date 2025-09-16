// Services/WebScraperService.cs - Enhanced with reliable download handling
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using PuppeteerSharp;

namespace PrivacyLens.Services
{
    /// <summary>
    /// Production-grade web scraper with reliable document download capabilities.
    /// Implements CDP configuration, proper synchronization, and robust error handling.
    /// </summary>
    public class WebScraperService : IDisposable
    {
        private readonly string appPath;
        private IBrowser? browser;
        private readonly HashSet<string> downloadedContentHashes;
        private readonly HashSet<string> downloadedUrlHashes;
        private readonly HashSet<string> allowedDomains;
        private readonly object downloadLock = new object();

        // Document MIME types for detection - excludes JSON and other non-document types
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
            // Removed "application/octet-stream" - too generic, causes false positives
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
            public int DocumentsDownloaded { get; set; }
            public int Failed { get; set; }
            public DateTime StartTime { get; set; }
            public DateTime EndTime { get; set; }
            public string TargetUrl { get; set; } = string.Empty;
            public List<string> DownloadedFiles { get; set; } = new List<string>();
        }

        public WebScraperService(string? basePath = null)
        {
            appPath = basePath ?? AppDomain.CurrentDomain.BaseDirectory;
            downloadedContentHashes = new HashSet<string>();
            downloadedUrlHashes = new HashSet<string>();
            allowedDomains = new HashSet<string>();
        }

        /// <summary>
        /// Main scraping method with enhanced download handling
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

            Console.WriteLine($"\n🚀 Starting {target} scrape");
            Console.WriteLine($"📍 URL: {url}");
            Console.WriteLine($"🔖 Session: {result.SessionId}");
            Console.WriteLine($"⚙️  Mode: {(antiDetection ? "Stealth" : "Fast")}");
            Console.WriteLine($"📊 Max pages: {maxPages}\n");

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

                // Configure CDP for downloads BEFORE any navigation
                var downloadsPath = Path.Combine(evidencePath, "documents");
                await ConfigureCDPDownloadBehavior(page, downloadsPath);

                // Set up network interception
                await SetupNetworkInterceptionAsync(page, evidencePath);

                // Perform the crawl
                var stats = await CrawlWebsiteAsync(page, url, evidencePath, maxPages, antiDetection);

                result.PagesScraped = stats.Pages;
                result.DocumentsDownloaded = stats.Documents;
                result.Failed = stats.Failed;
                result.DownloadedFiles = stats.DownloadedFiles;

                // Save metadata
                await SaveScrapeMetadataAsync(result, evidencePath);

                Console.WriteLine($"\n✅ Scraping complete!");
                Console.WriteLine($"📄 Pages: {result.PagesScraped}");
                Console.WriteLine($"📁 Documents: {result.DocumentsDownloaded}");
                if (result.Failed > 0)
                    Console.WriteLine($"❌ Failed: {result.Failed}");
                Console.WriteLine($"\n📂 Evidence saved to:\n   {evidencePath}\n");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"\n❌ Error during scraping: {ex.Message}");
                throw;
            }
            finally
            {
                result.EndTime = DateTime.Now;
                await CloseBrowserAsync();
            }

            return result;
        }

        /// <summary>
        /// Configure Chrome DevTools Protocol for reliable downloads
        /// </summary>
        private async Task ConfigureCDPDownloadBehavior(IPage page, string downloadPath)
        {
            Directory.CreateDirectory(downloadPath);

            try
            {
                // This is MANDATORY for headless downloads to work
                await page.Client.SendAsync("Page.setDownloadBehavior", new
                {
                    behavior = "allow",
                    downloadPath = downloadPath
                });

                Console.WriteLine($"   ✅ CDP configured for downloads");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"   ⚠️  CDP configuration warning: {ex.Message}");
            }
        }

        /// <summary>
        /// Enhanced crawling with proper download handling
        /// </summary>
        private async Task<(int Pages, int Documents, int Failed, List<string> DownloadedFiles)> CrawlWebsiteAsync(
            IPage page,
            string startUrl,
            string evidencePath,
            int maxPages,
            bool antiDetection)
        {
            int pagesScraped = 0;
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
                    Console.WriteLine($"\n🔍 Scraping: {currentUrl}");

                    // Apply delay if anti-detection
                    if (antiDetection)
                        await Task.Delay(Random.Shared.Next(1500, 3000));

                    var response = await page.GoToAsync(currentUrl, new NavigationOptions
                    {
                        WaitUntil = new[] { WaitUntilNavigation.DOMContentLoaded }, // Changed from Networkidle0 which can timeout
                        Timeout = 30000
                    });

                    if (response != null && !response.Ok)
                    {
                        Console.WriteLine($"   ⚠️  Page returned status: {response.Status}");
                        if ((int)response.Status >= 400)
                        {
                            failed++;
                            continue;
                        }
                    }

                    var title = await page.GetTitleAsync();
                    var content = await page.GetContentAsync();

                    // Check for error pages
                    if (await IsErrorPageAsync(page, title))
                    {
                        Console.WriteLine("   ⚠️  Skipping error page");
                        continue;
                    }

                    // Scan for document links
                    var documentLinks = await ScanForDocumentLinksAsync(page);
                    if (documentLinks.Count > 0)
                    {
                        Console.WriteLine($"   📎 Found {documentLinks.Count} document link(s)");
                        foreach (var docUrl in documentLinks)
                        {
                            var downloadResult = await DownloadDocumentAsync(docUrl, page, evidencePath);
                            if (downloadResult.Success)
                            {
                                documentsDownloaded++;
                                if (!string.IsNullOrEmpty(downloadResult.FilePath))
                                    downloadedFiles.Add(downloadResult.FilePath);
                            }
                        }
                    }

                    // Save webpage
                    await SaveWebpageAsync(currentUrl, title, content, evidencePath);
                    pagesScraped++;
                    Console.WriteLine($"   ✅ Saved page: {title ?? "Untitled"}");

                    // Extract links for crawling
                    var links = await ExtractLinksAsync(page);
                    foreach (var link in links)
                        if (!visitedUrls.Contains(link) && IsAllowedUrl(link))
                            pagesToVisit.Enqueue(link);
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"   ❌ Error: {ex.Message}");
                    failed++;
                }
            }

            return (pagesScraped, documentsDownloaded, failed, downloadedFiles);
        }

        /// <summary>
        /// Simple, reliable document download using direct HTTP client
        /// </summary>
        /// <summary>
        /// Simple, reliable document download using direct HTTP client
        /// </summary>
        private async Task<(bool Success, string? FilePath)> DownloadDocumentAsync(
            string url, IPage page, string evidencePath)
        {
            try
            {
                // Check if already downloaded
                var urlHash = ComputeHash(Encoding.UTF8.GetBytes(url));
                bool alreadyDownloaded = false;
                lock (downloadLock)
                {
                    if (downloadedUrlHashes.Contains(urlHash))
                    {
                        alreadyDownloaded = true;
                    }
                    else
                    {
                        downloadedUrlHashes.Add(urlHash);
                    }
                }

                if (alreadyDownloaded)
                {
                    Console.WriteLine($"   ⏭️  Already downloaded: {url}");
                    return (false, null);
                }

                var downloadsPath = Path.Combine(evidencePath, "documents");
                Directory.CreateDirectory(downloadsPath);

                // Extract filename from URL
                var filename = GetFileNameFromUrl(url);
                filename = CleanupFilenameString(filename);
                if (!Path.HasExtension(filename))
                    filename += ".pdf";

                Console.WriteLine($"   ⬇️  Downloading: {filename}");

                // Method 1: Direct HTTP download (simplest and most reliable)
                using (var httpClient = new System.Net.Http.HttpClient())
                {
                    httpClient.DefaultRequestHeaders.Add("User-Agent", "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/120.0.0.0 Safari/537.36");
                    httpClient.Timeout = TimeSpan.FromSeconds(30);

                    try
                    {
                        // Get cookies from current page and add to request
                        var cookies = await page.GetCookiesAsync();
                        if (cookies.Any())
                        {
                            var cookieString = string.Join("; ", cookies.Select(c => $"{c.Name}={c.Value}"));
                            httpClient.DefaultRequestHeaders.Add("Cookie", cookieString);
                        }

                        // Download the file
                        var response = await httpClient.GetAsync(url);

                        if (response.IsSuccessStatusCode)
                        {
                            var fileBytes = await response.Content.ReadAsByteArrayAsync();

                            if (fileBytes.Length > 0)
                            {
                                // Check if it's actually a PDF (or other valid document)
                                if (!IsValidDocument(fileBytes))
                                {
                                    Console.WriteLine($"   ⚠️  Invalid document format (possibly HTML error page)");
                                    return (false, null);
                                }

                                // Generate unique filename
                                var filePath = Path.Combine(downloadsPath, SanitizeFilename(filename));
                                int counter = 1;
                                while (File.Exists(filePath))
                                {
                                    var nameWithoutExt = Path.GetFileNameWithoutExtension(filename);
                                    var ext = Path.GetExtension(filename);
                                    filePath = Path.Combine(downloadsPath, SanitizeFilename($"{nameWithoutExt}_{counter}{ext}"));
                                    counter++;
                                }

                                // Save the file
                                await File.WriteAllBytesAsync(filePath, fileBytes);

                                // Verify and deduplicate
                                var fileInfo = new FileInfo(filePath);
                                if (fileInfo.Exists && fileInfo.Length > 0)
                                {
                                    var contentHash = await ComputeFileHashAsync(filePath);

                                    bool isDuplicate = false;
                                    lock (downloadLock)
                                    {
                                        if (downloadedContentHashes.Contains(contentHash))
                                        {
                                            isDuplicate = true;
                                        }
                                        else
                                        {
                                            downloadedContentHashes.Add(contentHash);
                                        }
                                    }

                                    if (isDuplicate)
                                    {
                                        Console.WriteLine("   ⚠️  Duplicate content detected, removing");
                                        File.Delete(filePath);
                                        return (false, null);
                                    }

                                    Console.WriteLine($"   ✅ Downloaded: {Path.GetFileName(filePath)} ({FormatFileSize(fileInfo.Length)})");

                                    // Save metadata
                                    await SaveDownloadMetadataAsync(url, filePath, contentHash);

                                    return (true, filePath);
                                }
                            }
                            else
                            {
                                Console.WriteLine($"   ⚠️  Empty file received");
                            }
                        }
                        else
                        {
                            Console.WriteLine($"   ❌ HTTP {(int)response.StatusCode}: {response.ReasonPhrase}");
                        }
                    }
                    catch (System.Net.Http.HttpRequestException httpEx)
                    {
                        Console.WriteLine($"   ❌ Network error: {httpEx.Message}");
                    }
                    catch (TaskCanceledException)
                    {
                        Console.WriteLine($"   ❌ Download timeout");
                    }
                }

                // Method 2: If HTTP fails, try browser page evaluation (for JavaScript-protected downloads)
                try
                {
                    Console.WriteLine($"   🔄 Trying browser-based download...");

                    var browser = page.Browser;
                    if (browser == null) return (false, null);

                    var downloadPage = await browser.NewPageAsync();
                    try
                    {
                        // Set download behavior
                        await downloadPage.Client.SendAsync("Page.setDownloadBehavior", new
                        {
                            behavior = "allow",
                            downloadPath = downloadsPath
                        });

                        // Record files before
                        var filesBefore = Directory.GetFiles(downloadsPath)
                            .Where(f => !f.EndsWith(".meta.json", StringComparison.OrdinalIgnoreCase))
                            .ToHashSet();

                        // Navigate to the URL
                        try
                        {
                            await downloadPage.GoToAsync(url, new NavigationOptions
                            {
                                WaitUntil = new[] { WaitUntilNavigation.DOMContentLoaded },
                                Timeout = 15000
                            });
                        }
                        catch (NavigationException ex) when (ex.Message.Contains("net::ERR_ABORTED"))
                        {
                            // Expected for downloads
                        }

                        // Wait a bit for download to start
                        await Task.Delay(3000);

                        // Check for new files
                        var filesAfter = Directory.GetFiles(downloadsPath)
                            .Where(f => !f.EndsWith(".meta.json", StringComparison.OrdinalIgnoreCase))
                            .ToHashSet();

                        var newFiles = filesAfter.Except(filesBefore).ToList();
                        if (newFiles.Any())
                        {
                            var downloadedFile = newFiles.First();

                            // Wait for file to be fully written
                            await Task.Delay(2000);

                            var fileInfo = new FileInfo(downloadedFile);
                            if (fileInfo.Exists && fileInfo.Length > 0)
                            {
                                var contentHash = await ComputeFileHashAsync(downloadedFile);

                                bool isDuplicate = false;
                                lock (downloadLock)
                                {
                                    if (downloadedContentHashes.Contains(contentHash))
                                    {
                                        isDuplicate = true;
                                    }
                                    else
                                    {
                                        downloadedContentHashes.Add(contentHash);
                                    }
                                }

                                if (!isDuplicate)
                                {
                                    Console.WriteLine($"   ✅ Downloaded via browser: {Path.GetFileName(downloadedFile)} ({FormatFileSize(fileInfo.Length)})");
                                    await SaveDownloadMetadataAsync(url, downloadedFile, contentHash);
                                    return (true, downloadedFile);
                                }
                            }
                        }
                    }
                    finally
                    {
                        await downloadPage.CloseAsync();
                    }
                }
                catch (Exception browserEx)
                {
                    Console.WriteLine($"   ⚠️  Browser method error: {browserEx.Message}");
                }

            }
            catch (Exception ex)
            {
                Console.WriteLine($"   ❌ Download error: {ex.Message}");
            }

            Console.WriteLine($"   ⚠️  All download methods failed for: {url}");
            return (false, null);
        }

        /// <summary>
        /// Check if byte array looks like a valid document (not HTML error page)
        /// </summary>
        private bool IsValidDocument(byte[] fileBytes)
        {
            if (fileBytes == null || fileBytes.Length < 10)
                return false;

            // Check for PDF signature
            if (fileBytes[0] == 0x25 && fileBytes[1] == 0x50 && fileBytes[2] == 0x44 && fileBytes[3] == 0x46) // %PDF
                return true;

            // Check for ZIP/Office signatures (DOCX, XLSX, PPTX are ZIP files)
            if (fileBytes[0] == 0x50 && fileBytes[1] == 0x4B) // PK
                return true;

            // Check for old Office format
            if (fileBytes[0] == 0xD0 && fileBytes[1] == 0xCF && fileBytes[2] == 0x11 && fileBytes[3] == 0xE0) // OLE
                return true;

            // Check if it's HTML (error page)
            var text = System.Text.Encoding.UTF8.GetString(fileBytes.Take(500).ToArray()).ToLower();
            if (text.Contains("<!doctype") || text.Contains("<html") || text.Contains("error") || text.Contains("404"))
                return false;

            // Default to valid for other formats
            return true;
        }

        /// <summary>
        /// Clean up duplicate extensions in filename string
        /// </summary>
        private string CleanupFilenameString(string filename)
        {
            // Fix patterns like .pdf.pdf or _as_pdfSomething.pdf
            filename = Regex.Replace(filename, @"(picture_)?as_pdf", "", RegexOptions.IgnoreCase);
            filename = Regex.Replace(filename, @"(\.\w{3,4})\1$", "$1"); // Remove duplicate extensions
            filename = Regex.Replace(filename, @"\.pdf\.pdf$", ".pdf", RegexOptions.IgnoreCase);
            return filename;
        }

        /// <summary>
        /// Check if response looks like a document
        /// </summary>
        private bool IsDocumentResponse(IResponse response)
        {
            if (response.Headers.TryGetValue("content-type", out var contentType))
            {
                var mimeType = contentType.Split(';')[0].Trim().ToLower();

                // Common document MIME types
                return mimeType.Contains("pdf") ||
                       mimeType.Contains("document") ||
                       mimeType.Contains("msword") ||
                       mimeType.Contains("excel") ||
                       mimeType.Contains("spreadsheet") ||
                       mimeType.Contains("powerpoint") ||
                       mimeType.Contains("presentation") ||
                       mimeType.Contains("zip") ||
                       mimeType == "application/octet-stream";
            }

            return false;
        }

        /// <summary>
        /// Clean up filenames with duplicate extensions
        /// </summary>
        private string CleanupFilename(string filePath)
        {
            var dir = Path.GetDirectoryName(filePath);
            var filename = Path.GetFileName(filePath);

            // Fix double extensions like .pdf.pdf
            var pattern = @"(\.\w{3,4})\1$"; // Matches duplicate extensions
            if (System.Text.RegularExpressions.Regex.IsMatch(filename, pattern))
            {
                filename = System.Text.RegularExpressions.Regex.Replace(filename, pattern, "$1");
                var newPath = Path.Combine(dir, filename);

                if (File.Exists(filePath) && !File.Exists(newPath))
                {
                    File.Move(filePath, newPath);
                    return newPath;
                }
            }

            return filePath;
        }

        /// <summary>
        /// Extract filename from URL
        /// </summary>
        private string GetFileNameFromUrl(string url)
        {
            try
            {
                var uri = new Uri(url);
                var filename = Path.GetFileName(uri.LocalPath);

                // Clean up the filename
                if (!string.IsNullOrEmpty(filename))
                {
                    // URL decode the filename
                    filename = Uri.UnescapeDataString(filename);

                    // Remove duplicate extensions
                    var pattern = @"(\.\w{3,4})\1$";
                    if (Regex.IsMatch(filename, pattern))
                    {
                        filename = Regex.Replace(filename, pattern, "$1");
                    }

                    return filename;
                }
            }
            catch { }

            return "document";
        }

        /// <summary>
        /// Wait for download to complete using filesystem polling
        /// </summary>
        private async Task<string?> WaitForDownloadAsync(
            string downloadPath,
            Dictionary<string, DateTime> filesBefore,
            int timeoutMs = 30000)
        {
            var startTime = DateTime.UtcNow;
            var timeout = TimeSpan.FromMilliseconds(timeoutMs);
            string? lastNewFile = null;

            while (DateTime.UtcNow - startTime < timeout)
            {
                // Get all non-temporary files
                var currentFiles = Directory.GetFiles(downloadPath)
                    .Where(f => !f.EndsWith(".crdownload", StringComparison.OrdinalIgnoreCase) &&
                                !f.EndsWith(".tmp", StringComparison.OrdinalIgnoreCase) &&
                                !f.EndsWith(".part", StringComparison.OrdinalIgnoreCase) &&
                                !f.EndsWith(".downloading", StringComparison.OrdinalIgnoreCase))
                    .Select(f => new FileInfo(f))
                    .Where(f => f.Length > 0) // Ensure file has content
                    .ToList();

                // Find new files
                foreach (var file in currentFiles)
                {
                    if (!filesBefore.ContainsKey(file.FullName) ||
                        file.CreationTimeUtc > filesBefore[file.FullName])
                    {
                        // New file found, but wait a bit to ensure it's fully written
                        if (lastNewFile == file.FullName)
                        {
                            // Same file seen twice, likely complete
                            return file.FullName;
                        }
                        lastNewFile = file.FullName;
                    }
                }

                await Task.Delay(500);
            }

            return lastNewFile; // Return last new file even if timeout
        }

        /// <summary>
        /// Enhanced network interception setup
        /// </summary>
        private async Task SetupNetworkInterceptionAsync(IPage page, string evidencePath)
        {
            var downloadsPath = Path.Combine(evidencePath, "documents");
            var activeDownloads = new HashSet<string>();

            page.Response += async (sender, e) =>
            {
                try
                {
                    var response = e.Response;
                    if (!response.Ok) return;

                    // Skip if URL suggests it's not a document
                    var url = response.Url.ToLower();
                    if (url.Contains(".json") ||
                        url.Contains(".js") ||
                        url.Contains(".css") ||
                        url.Contains("/api/") ||
                        url.Contains("/feed/") ||
                        url.Contains("/ajax/"))
                        return;

                    // Skip if already processing
                    lock (activeDownloads)
                    {
                        if (activeDownloads.Contains(response.Url))
                            return;
                        activeDownloads.Add(response.Url);
                    }

                    try
                    {
                        if (IsDownloadResponse(response))
                        {
                            Console.WriteLine($"   🔍 Intercepted potential download: {response.Url}");

                            // The CDP download behavior should handle this
                            // We just need to wait for it to complete
                            var filesBefore = Directory.GetFiles(downloadsPath)
                                .Select(f => new FileInfo(f))
                                .ToDictionary(f => f.FullName, f => f.CreationTimeUtc);

                            var downloadedFile = await WaitForDownloadAsync(downloadsPath, filesBefore, 10000);
                            if (!string.IsNullOrEmpty(downloadedFile))
                            {
                                Console.WriteLine($"   ✅ Network intercept saved: {Path.GetFileName(downloadedFile)}");
                            }
                        }
                    }
                    finally
                    {
                        lock (activeDownloads)
                        {
                            activeDownloads.Remove(response.Url);
                        }
                    }
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"   ⚠️  Response handler error: {ex.Message}");
                }
            };

            // Enable request interception
            await page.SetRequestInterceptionAsync(true);
            page.Request += async (sender, e) => await e.Request.ContinueAsync();
        }

        /// <summary>
        /// Initialize browser with production-ready configuration
        /// </summary>
        private async Task InitializeBrowserAsync(bool antiDetection)
        {
            Console.WriteLine("🌐 Initializing browser...");

            var browserFetcher = new BrowserFetcher();
            await browserFetcher.DownloadAsync();

            var args = new List<string>
            {
                "--no-sandbox",
                "--disable-setuid-sandbox",
                "--disable-dev-shm-usage",
                "--disable-gpu",
                "--no-first-run",
                "--no-zygote",
                "--deterministic-fetch",
                "--disable-features=IsolateOrigins",
                "--disable-site-isolation-trials",
                // Handle HTTPS certificate errors (useful for self-signed certs)
                "--ignore-certificate-errors",
                "--ignore-certificate-errors-spki-list",
                // Additional stability flags
                "--disable-accelerated-2d-canvas",
                "--disable-breakpad",
                "--disable-features=TranslateUI",
                "--disable-ipc-flooding-protection",
                "--disable-renderer-backgrounding",
                "--force-color-profile=srgb",
                "--metrics-recording-only",
                "--mute-audio",
                "--no-default-browser-check"
            };

            if (antiDetection)
            {
                args.Add("--disable-blink-features=AutomationControlled");
                args.Add("--user-agent=Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/120.0.0.0 Safari/537.36");
            }

            var launchOptions = new LaunchOptions
            {
                Headless = true,
                Args = args.ToArray(),
                DefaultViewport = new ViewPortOptions { Width = 1920, Height = 1080 },
                Timeout = 60000,
                IgnoreDefaultArgs = false
            };

            browser = await Puppeteer.LaunchAsync(launchOptions);
            Console.WriteLine("✅ Browser initialized");
        }

        /// <summary>
        /// Check if response indicates a file download
        /// </summary>
        private bool IsDownloadResponse(IResponse response)
        {
            // Check Content-Type
            if (response.Headers.TryGetValue("content-type", out var contentType))
            {
                var mimeType = contentType.Split(';')[0].Trim().ToLower();

                // Skip HTML, JSON, XML, and other non-document types
                if (mimeType == "text/html" ||
                    mimeType == "application/json" ||
                    mimeType == "application/xml" ||
                    mimeType == "text/xml" ||
                    mimeType.StartsWith("image/") ||
                    mimeType.StartsWith("video/") ||
                    mimeType.StartsWith("audio/"))
                    return false;

                if (DocumentMimeTypes.Contains(mimeType))
                    return true;

                // Check for octet-stream only if there's a download disposition
                if (mimeType == "application/octet-stream")
                {
                    if (response.Headers.TryGetValue("content-disposition", out var disposition))
                    {
                        return disposition.Contains("attachment", StringComparison.OrdinalIgnoreCase);
                    }
                    return false;
                }
            }

            // Check Content-Disposition
            if (response.Headers.TryGetValue("content-disposition", out var contentDisposition))
            {
                if (contentDisposition.Contains("attachment", StringComparison.OrdinalIgnoreCase))
                    return true;
            }

            return false;
        }

        /// <summary>
        /// Extract filename from Content-Disposition header
        /// </summary>
        private string? ExtractFilenameFromHeaders(IDictionary<string, string> headers)
        {
            if (headers.TryGetValue("content-disposition", out var disposition))
            {
                try
                {
                    if (ContentDispositionHeaderValue.TryParse(disposition, out var parsed))
                    {
                        var filename = parsed.FileNameStar ?? parsed.FileName;
                        if (!string.IsNullOrWhiteSpace(filename))
                        {
                            // Remove quotes if present
                            filename = filename.Trim('"');
                            return SanitizeFilename(filename);
                        }
                    }
                }
                catch
                {
                    // Fallback to regex if parsing fails
                    var match = System.Text.RegularExpressions.Regex.Match(
                        disposition, @"filename[*]?=[""']?([^;""']+)");
                    if (match.Success)
                        return SanitizeFilename(match.Groups[1].Value);
                }
            }
            return null;
        }

        /// <summary>
        /// Scan page for document links
        /// </summary>
        private async Task<List<string>> ScanForDocumentLinksAsync(IPage page)
        {
            return await page.EvaluateFunctionAsync<List<string>>(@"
                () => {
                    const links = [];
                    const anchors = document.querySelectorAll('a[href]');
                    const extensions = ['.pdf', '.doc', '.docx', '.xls', '.xlsx', '.ppt', '.pptx', '.txt', '.csv', '.rtf', '.zip'];
                    
                    anchors.forEach(anchor => {
                        const href = anchor.href;
                        if (href && extensions.some(ext => href.toLowerCase().includes(ext))) {
                            links.push(href);
                        }
                    });
                    
                    // Also check for download attribute
                    document.querySelectorAll('a[download]').forEach(anchor => {
                        if (anchor.href && !links.includes(anchor.href)) {
                            links.push(anchor.href);
                        }
                    });
                    
                    return [...new Set(links)]; // Remove duplicates
                }
            ");
        }

        /// <summary>
        /// Extract all links from page
        /// </summary>
        private async Task<List<string>> ExtractLinksAsync(IPage page)
        {
            return await page.EvaluateFunctionAsync<List<string>>(@"
                () => {
                    const links = [];
                    document.querySelectorAll('a[href]').forEach(anchor => {
                        const href = anchor.href;
                        if (href && 
                            !href.includes('#') && 
                            !href.startsWith('mailto:') &&
                            !href.startsWith('tel:') && 
                            !href.startsWith('javascript:') &&
                            !href.startsWith('data:')) {
                            links.push(href);
                        }
                    });
                    return [...new Set(links)]; // Remove duplicates
                }
            ");
        }

        /// <summary>
        /// Save webpage content and metadata
        /// </summary>
        private async Task SaveWebpageAsync(string url, string? title, string content, string evidencePath)
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
            await File.WriteAllTextAsync(metaPath, JsonSerializer.Serialize(metadata,
                new JsonSerializerOptions { WriteIndented = true }));
        }

        /// <summary>
        /// Save download metadata
        /// </summary>
        private async Task SaveDownloadMetadataAsync(string originalUrl, string filePath, string contentHash)
        {
            var fileInfo = new FileInfo(filePath);
            var metadata = new
            {
                OriginalUrl = originalUrl,
                Filename = fileInfo.Name,
                FilePath = filePath,
                Size = fileInfo.Length,
                Hash = contentHash,
                DownloadTime = DateTime.UtcNow
            };

            var metaPath = Path.ChangeExtension(filePath, ".meta.json");
            await File.WriteAllTextAsync(metaPath, JsonSerializer.Serialize(metadata,
                new JsonSerializerOptions { WriteIndented = true }));
        }

        /// <summary>
        /// Save scrape session metadata
        /// </summary>
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
                Duration = (result.EndTime - result.StartTime).TotalSeconds,
                DownloadedFiles = result.DownloadedFiles.Select(Path.GetFileName).ToList()
            };

            var metadataPath = Path.Combine(evidencePath, "scrape_metadata.json");
            await File.WriteAllTextAsync(metadataPath, JsonSerializer.Serialize(metadata,
                new JsonSerializerOptions { WriteIndented = true }));
        }

        /// <summary>
        /// Create directory structure for evidence
        /// </summary>
        private async Task CreateDirectoryStructureAsync(string evidencePath)
        {
            Directory.CreateDirectory(Path.Combine(evidencePath, "webpages"));
            Directory.CreateDirectory(Path.Combine(evidencePath, "documents"));
            Directory.CreateDirectory(Path.Combine(evidencePath, "screenshots"));
            await Task.CompletedTask;
        }

        /// <summary>
        /// Setup allowed domains for crawling
        /// </summary>
        private void SetupAllowedDomains(string url)
        {
            var uri = new Uri(url);
            var mainDomain = uri.Host.Replace("www.", "");

            allowedDomains.Clear();
            allowedDomains.Add(mainDomain);

            // Add common subdomains
            var subdomains = new[] { "www", "docs", "support", "help", "privacy", "legal", "download", "files", "cdn" };
            foreach (var sub in subdomains)
                allowedDomains.Add($"{sub}.{mainDomain}");

            Console.WriteLine($"📍 Restricting to domain: {mainDomain} and subdomains");
        }

        /// <summary>
        /// Check if URL is allowed for crawling
        /// </summary>
        private bool IsAllowedUrl(string url)
        {
            try
            {
                var uri = new Uri(url);
                if (uri.Scheme != "http" && uri.Scheme != "https")
                    return false;

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

        /// <summary>
        /// Check if page is an error page
        /// </summary>
        private async Task<bool> IsErrorPageAsync(IPage page, string? title)
        {
            var text = await page.EvaluateFunctionAsync<string>("() => document.body?.innerText || ''");
            var indicators = new[] { "404", "not found", "error", "forbidden", "access denied", "page not found" };
            var content = $"{title} {text}".ToLower();
            return indicators.Any(indicator => content.Contains(indicator)) && text.Length < 1000;
        }

        /// <summary>
        /// Sanitize filename for filesystem
        /// </summary>
        private string SanitizeFilename(string name)
        {
            var invalid = Path.GetInvalidFileNameChars();
            var sanitized = string.Join("_", name.Split(invalid, StringSplitOptions.RemoveEmptyEntries));
            return sanitized.Length > 100 ? sanitized.Substring(0, 100) : sanitized;
        }

        /// <summary>
        /// Compute SHA256 hash of data
        /// </summary>
        private string ComputeHash(byte[] data)
        {
            using var sha256 = SHA256.Create();
            var hashBytes = sha256.ComputeHash(data);
            return BitConverter.ToString(hashBytes).Replace("-", "").ToLower();
        }

        /// <summary>
        /// Compute hash of file content
        /// </summary>
        private async Task<string> ComputeFileHashAsync(string filePath)
        {
            using var stream = File.OpenRead(filePath);
            using var sha256 = SHA256.Create();
            var hashBytes = await sha256.ComputeHashAsync(stream);
            return BitConverter.ToString(hashBytes).Replace("-", "").ToLower();
        }

        /// <summary>
        /// Format file size for display
        /// </summary>
        private string FormatFileSize(long bytes)
        {
            string[] sizes = { "B", "KB", "MB", "GB" };
            double size = bytes;
            int order = 0;

            while (size >= 1024 && order < sizes.Length - 1)
            {
                order++;
                size /= 1024;
            }

            return $"{size:0.##} {sizes[order]}";
        }

        /// <summary>
        /// Close browser gracefully
        /// </summary>
        private async Task CloseBrowserAsync()
        {
            if (browser != null)
            {
                await browser.CloseAsync();
                browser = null;
            }
        }

        public void Dispose()
        {
            CloseBrowserAsync().GetAwaiter().GetResult();
        }
    }
}