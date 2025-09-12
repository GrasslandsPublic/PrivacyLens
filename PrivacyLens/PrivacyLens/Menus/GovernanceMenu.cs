// Menus/GovernanceMenu.cs
using System;
using System.IO;
using System.Threading.Tasks;

using Microsoft.Extensions.Configuration;

using PrivacyLens.Models;
using PrivacyLens.Services;

namespace PrivacyLens.Menus
{
    public sealed class GovernanceMenu
    {
        private readonly GovernanceImportPipeline _pipeline;

        public GovernanceMenu()
        {
            var config = new ConfigurationBuilder()
                .AddJsonFile("appsettings.json", optional: false, reloadOnChange: true)
                .AddEnvironmentVariables()
                .Build();

            var chunking = new GptChunkingService(config);
            var embedding = new EmbeddingService(config);
            var vectorStore = new VectorStore(config);

            _pipeline = new GovernanceImportPipeline(
                chunking,
                embedding,
                vectorStore,
                config
            );

            // Default folder (support either key path)
            var defaultFolderRaw =
                config["Ingestion:DefaultFolderPath"] ??
                config["PrivacyLens:Paths:SourceDocuments"];

            if (!string.IsNullOrWhiteSpace(defaultFolderRaw))
            {
                var folder = Path.IsPathRooted(defaultFolderRaw)
                    ? defaultFolderRaw
                    : Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, defaultFolderRaw));

                if (Directory.Exists(folder))
                    _pipeline.SetFolderPath(folder);
            }
        }

        public void Show() => RunAsync().GetAwaiter().GetResult();

        public async Task RunAsync()
        {
            while (true)
            {
                DrawHeader();

                Console.WriteLine($"Current folder: '{_pipeline.DefaultFolderPath}'");
                Console.WriteLine("1) Set folder");
                Console.WriteLine("2) Import ALL files from folder");
                Console.WriteLine("3) Import ONE file");
                Console.WriteLine("0) Back/Exit");
                Console.Write("Select: ");

                var choice = Console.ReadLine()?.Trim();

                switch (choice)
                {
                    case "1":
                        Console.Write("Folder path: ");
                        var folder = Console.ReadLine();
                        if (!string.IsNullOrWhiteSpace(folder))
                        {
                            try
                            {
                                var final = Path.IsPathRooted(folder)
                                    ? folder
                                    : Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, folder));
                                _pipeline.SetFolderPath(final);
                                Console.WriteLine($"Folder set to: {_pipeline.DefaultFolderPath}");
                            }
                            catch (Exception ex)
                            {
                                Console.WriteLine($"Error: {ex.Message}");
                            }
                        }
                        Pause();
                        break;

                    case "2":
                        await ShowProgressAsync(allFiles: true);
                        break;

                    case "3":
                        Console.Write("File path: ");
                        var file = Console.ReadLine();
                        if (!string.IsNullOrWhiteSpace(file))
                        {
                            var final = Path.IsPathRooted(file)
                                ? file
                                : Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, file));
                            await ShowProgressAsync(allFiles: false, singleFile: final);
                        }
                        break;

                    case "0":
                        return;
                }
            }
        }

        private static void DrawHeader()
        {
            Console.Clear();
            Console.WriteLine("=== Governance Import ===");
        }

        private static void Pause()
        {
            Console.WriteLine();
            Console.Write("Press ENTER to continue...");
            Console.ReadLine();
        }

        /// <summary>
        /// Single-line progress UI with heartbeat and 429 countdown that resets on new Retry-After.
        /// </summary>
        private async Task ShowProgressAsync(bool allFiles, string? singleFile = null)
        {
            Console.Clear();
            Console.WriteLine(allFiles ? "Importing ALL files..." : $"Importing file: {Path.GetFileName(singleFile)}");
            Console.WriteLine();

            var prevCursor = Console.CursorVisible;
            Console.CursorVisible = false;

            var startTop = Console.CursorTop;
            var lastLen = 0;

            var renderLock = new object();
            ImportProgress? latest = null;

            // helpers ---------------------------------------------------------
            void UpdateLine(string line)
            {
                lock (renderLock)
                {
                    Console.SetCursorPosition(0, startTop);
                    if (line.Length < lastLen) line = line.PadRight(lastLen);
                    Console.Write(line);
                    lastLen = line.Length;
                }
            }

            static int ParseSecondsFromInfo(string? info)
            {
                if (string.IsNullOrWhiteSpace(info)) return -1;
                int i = 0;
                while (i < info.Length && char.IsWhiteSpace(info[i])) i++;
                int j = i;
                while (j < info.Length && char.IsDigit(info[j])) j++;
                return int.TryParse(info.Substring(i, j - i), out var s) ? s : -1;
            }

            string ComposeLine(ImportProgress p, string? overrideInfo = null, string? extraTail = null)
            {
                var percent = p.Total > 0 ? (int)(p.Current * 100.0 / p.Total) : 0;
                var file = p.File;
                if (file.Length > 60) file = "..." + file[^57..];

                var duration = p.StageElapsedMs.HasValue ? $"  ({p.StageElapsedMs.Value} ms)" : "";
                var info = overrideInfo ?? p.Info;
                var infoPart = string.IsNullOrWhiteSpace(info) ? "" : $"  [{info}]";
                var extra = string.IsNullOrWhiteSpace(extraTail) ? "" : $"  {extraTail}";

                return $"[{p.Current}/{p.Total}] {percent,3}%  {p.Stage,-11}  {file}{duration}{infoPart}{extra}";
            }
            // ----------------------------------------------------------------

            // Subscribe to progress
            var progress = new Progress<ImportProgress>(p => latest = p);

            // Kick off work
            var runTask = allFiles
                ? _pipeline.ImportAllAsync(progress)
                : _pipeline.ImportAsync(singleFile!, progress, 1, 1);

            // UI loop state
            string? lastStage = null;
            string? lastFile = null;
            string? lastInfo = null; // <--- track Info changes for 429 resets
            var stageStart = DateTimeOffset.UtcNow;
            var spinner = new[] { "|", "/", "-", "\\" };
            var si = 0;

            // 429 countdown state
            DateTimeOffset? waitStart = null;
            int waitInitialSeconds = 0;
            string waitSuffix = ""; // "(embedding)" etc.

            try
            {
                while (true)
                {
                    if (runTask.IsCompleted) break;

                    var snap = latest;
                    if (snap is not null)
                    {
                        // Detect stage/file change OR (for 429) a new Retry-After (Info change)
                        bool stageChanged = !string.Equals(snap.Stage, lastStage, StringComparison.Ordinal)
                                         || !string.Equals(snap.File, lastFile, StringComparison.Ordinal);

                        bool infoChangedFor429 = string.Equals(snap.Stage, "429 wait", StringComparison.OrdinalIgnoreCase)
                                              && !string.Equals(snap.Info, lastInfo, StringComparison.Ordinal);

                        if (stageChanged || infoChangedFor429)
                        {
                            stageStart = DateTimeOffset.UtcNow;

                            if (string.Equals(snap.Stage, "429 wait", StringComparison.OrdinalIgnoreCase))
                            {
                                waitInitialSeconds = ParseSecondsFromInfo(snap.Info);
                                waitStart = DateTimeOffset.UtcNow;

                                waitSuffix = "";
                                if (!string.IsNullOrWhiteSpace(snap.Info))
                                {
                                    int idx = snap.Info.IndexOf(' ');
                                    if (idx > 0) waitSuffix = snap.Info.Substring(idx).Trim();
                                }
                            }
                        }

                        if (string.Equals(snap.Stage, "429 wait", StringComparison.OrdinalIgnoreCase))
                        {
                            int remaining;
                            if (waitStart.HasValue && waitInitialSeconds > 0)
                            {
                                var elapsed = (int)(DateTimeOffset.UtcNow - waitStart.Value).TotalSeconds;
                                remaining = Math.Max(0, waitInitialSeconds - elapsed);
                            }
                            else
                            {
                                remaining = Math.Max(0, ParseSecondsFromInfo(snap.Info));
                                waitStart = DateTimeOffset.UtcNow;
                                waitInitialSeconds = remaining;
                            }

                            var liveInfo = $"{remaining}s{(string.IsNullOrWhiteSpace(waitSuffix) ? "" : " " + waitSuffix)}";
                            UpdateLine(ComposeLine(snap, overrideInfo: liveInfo));
                        }
                        else
                        {
                            // Heartbeat for normal stages
                            var elapsed = (int)(DateTimeOffset.UtcNow - stageStart).TotalSeconds;
                            var tail = $"t+{elapsed}s  {spinner[si]}";
                            si = (si + 1) % spinner.Length;

                            UpdateLine(ComposeLine(snap, overrideInfo: null, extraTail: tail));
                        }

                        lastStage = snap.Stage;
                        lastFile = snap.File;
                        lastInfo = snap.Info; // <--- track latest Info for 429
                    }

                    await Task.Delay(250);
                }

                await runTask; // surface any exception

                var done = "Import complete.";
                UpdateLine(done.PadRight(Math.Max(done.Length, lastLen)));
                Console.WriteLine();
            }
            catch (Exception ex)
            {
                var err = $"Error: {ex.Message}";
                UpdateLine(err.PadRight(Math.Max(err.Length, lastLen)));
                Console.WriteLine();
            }
            finally
            {
                Console.CursorVisible = prevCursor;
            }

            Console.WriteLine();
            Console.Write("Press ENTER to continue...");
            Console.ReadLine();
        }
    }
}
