// Models/ChunkRecord.cs
namespace PrivacyLens.Models;

public sealed record ChunkRecord(
    string DocumentPath,
    int Index,
    string Content,
    float[] Embedding
);

