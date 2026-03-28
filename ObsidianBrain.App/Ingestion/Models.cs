namespace ObsidianBrain.App.Ingestion;

public sealed record Heading(int Level, string Text, int Line);
public sealed record Chunk(string Text, string HeadingPath, int TokenCount);

public sealed class ParsedDocument
{
    public required string Content { get; init; }
    public required string? Title { get; init; }
    public required Dictionary<string, string> Frontmatter { get; init; }
    public required List<Heading> Headings { get; init; }
}

public sealed class IngestionFileStat
{
    public required string Path { get; set; }
    public string? FileHash { get; set; }
    public required string Action { get; set; }
    public long BytesRead { get; set; }
    public int ChunksCreated { get; set; }
    public int ChunksUpdated { get; set; }
    public int ChunksDeleted { get; set; }
    public int TokensEmbedded { get; set; }
    public int DurationMs { get; set; }
    public string? ErrorMessage { get; set; }
}
