// Models/ImportProgress.cs
namespace PrivacyLens.Models;

public sealed record ImportProgress(
    int Current,                // 1-based index of current file
    int Total,                  // total files
    string File,                // file name (not path)
    string Stage,               // "Start", "Extract", "Chunk", "429 wait", "Embed+Save", "Done", "Error"
    string? Info = null,        // details like "60s (chunk retry 2/6)"
    long? StageElapsedMs = null // optional timing for a completed stage
);



