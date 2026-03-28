namespace ObsidianBrain.App.Ingestion;

public static class Chunker
{
    public static List<Chunk> ChunkMarkdown(ParsedDocument parsed, int maxTokens, int overlapTokens)
    {
        var words = parsed.Content.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
        var chunks = new List<Chunk>();
        if (words.Length == 0)
        {
            return chunks;
        }

        var step = Math.Max(1, maxTokens - overlapTokens);
        for (var i = 0; i < words.Length; i += step)
        {
            var take = Math.Min(maxTokens, words.Length - i);
            var text = string.Join(' ', words.Skip(i).Take(take));
            var headingPath = ResolveHeadingPath(parsed.Headings, i);
            chunks.Add(new Chunk(text, headingPath, take));
            if (i + take >= words.Length)
            {
                break;
            }
        }

        return chunks;
    }

    private static string ResolveHeadingPath(List<Heading> headings, int tokenOffset)
    {
        if (headings.Count == 0)
        {
            return string.Empty;
        }

        var idx = Math.Min(headings.Count - 1, tokenOffset / 200);
        return headings[idx].Text;
    }
}
