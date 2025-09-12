// Diagnostics/TraceLog.cs
using System;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace PrivacyLens.Diagnostics
{
    /// <summary>
    /// Lightweight async file logger for ingestion runs (thread-safe appends).
    /// Creates a single file per document so you can trace what happened.
    /// </summary>
    public sealed class TraceLog : IAsyncDisposable
    {
        private readonly string _path;
        private readonly SemaphoreSlim _gate = new(1, 1);
        private readonly bool _utc;

        public string Path => _path;

        public TraceLog(string directory, string baseFileName, bool utcTimestamps = true)
        {
            Directory.CreateDirectory(directory);
            _path = System.IO.Path.Combine(directory, $"{baseFileName}.log");
            _utc = utcTimestamps;
        }

        public async Task InitAsync(string header)
        {
            await _gate.WaitAsync();
            try
            {
                using var sw = new StreamWriter(new FileStream(_path, FileMode.Create, FileAccess.Write, FileShare.Read), Encoding.UTF8);
                var ts = _utc ? DateTime.UtcNow : DateTime.Now;
                await sw.WriteLineAsync($"# {header}");
                await sw.WriteLineAsync($"# start={ts:O}");
            }
            finally { _gate.Release(); }
        }

        public async Task WriteLineAsync(string line)
        {
            await _gate.WaitAsync();
            try
            {
                using var sw = new StreamWriter(new FileStream(_path, FileMode.Append, FileAccess.Write, FileShare.Read), Encoding.UTF8);
                var ts = _utc ? DateTime.UtcNow : DateTime.Now;
                await sw.WriteLineAsync($"{ts:O}  {line}");
            }
            finally { _gate.Release(); }
        }

        public async ValueTask DisposeAsync()
        {
            await _gate.WaitAsync();
            _gate.Release();
            _gate.Dispose();
        }

        public static string MakeFileSafe(string input)
        {
            var name = System.IO.Path.GetFileName(string.IsNullOrWhiteSpace(input) ? "untitled" : input);
            foreach (var c in System.IO.Path.GetInvalidFileNameChars()) name = name.Replace(c, '_');
            return name;
        }
    }
}
