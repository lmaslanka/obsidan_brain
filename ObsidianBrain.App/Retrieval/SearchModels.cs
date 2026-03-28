namespace ObsidianBrain.App.Retrieval;

public sealed record SearchResult(
    string Path,
    string? Title,
    string? HeadingPath,
    string Snippet,
    Guid ChunkId,
    double Score
);
