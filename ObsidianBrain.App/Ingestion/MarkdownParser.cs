using System.Text.RegularExpressions;

namespace ObsidianBrain.App.Ingestion;

public static class MarkdownParser
{
    private static readonly Regex HeadingRegex = new("^(#{1,3})\\s+(.*)$", RegexOptions.Compiled);

    public static ParsedDocument Parse(string content)
    {
        var lines = content.Split('\n');
        var frontmatter = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var headings = new List<Heading>();

        var index = 0;
        if (lines.Length > 0 && lines[0].Trim() == "---")
        {
            index = 1;
            for (; index < lines.Length; index++)
            {
                var line = lines[index].Trim();
                if (line == "---")
                {
                    index++;
                    break;
                }

                var colon = line.IndexOf(':');
                if (colon > 0)
                {
                    var key = line[..colon].Trim();
                    var value = line[(colon + 1)..].Trim();
                    frontmatter[key] = value;
                }
            }
        }

        string? title = null;

        for (var i = index; i < lines.Length; i++)
        {
            var raw = lines[i];
            var match = HeadingRegex.Match(raw.TrimEnd('\r'));
            if (!match.Success)
            {
                continue;
            }

            var level = match.Groups[1].Value.Length;
            var text = match.Groups[2].Value.Trim();
            headings.Add(new Heading(level, text, i + 1));
            if (title is null && level == 1)
            {
                title = text;
            }
        }

        title ??= frontmatter.TryGetValue("title", out var fmTitle) ? fmTitle : null;

        return new ParsedDocument
        {
            Content = content,
            Title = title,
            Frontmatter = frontmatter,
            Headings = headings
        };
    }
}
