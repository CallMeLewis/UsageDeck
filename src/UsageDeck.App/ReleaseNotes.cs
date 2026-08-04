namespace UsageDeck.App;

internal sealed record ReleaseNotesSection(string Heading, IReadOnlyList<string> Items);

internal sealed record ReleaseNotesDocument(
    string Version,
    IReadOnlyList<string> Introduction,
    IReadOnlyList<ReleaseNotesSection> Sections,
    IReadOnlyList<string> ClosingNotes);

internal sealed record ReleaseNotesLoadResult(
    ReleaseNotesDocument? Document,
    string UnavailableMessage)
{
    public bool IsAvailable => this.Document is not null;
}

internal static class ReleaseNotesReader
{
    private const string UnavailableMessage = "Release notes are not included with this build.";

    public static ReleaseNotesLoadResult Load(string appBaseDirectory, string version)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(appBaseDirectory);
        ArgumentException.ThrowIfNullOrWhiteSpace(version);

        string path = Path.Combine(appBaseDirectory, "ReleaseNotes", $"v{version}.md");
        try
        {
            if (!File.Exists(path))
            {
                return new ReleaseNotesLoadResult(null, UnavailableMessage);
            }

            ReleaseNotesDocument document = Parse(version, File.ReadAllText(path));
            return HasContent(document)
                ? new ReleaseNotesLoadResult(document, string.Empty)
                : new ReleaseNotesLoadResult(null, UnavailableMessage);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return new ReleaseNotesLoadResult(
                null,
                "Release notes could not be read from this installation.");
        }
    }

    internal static ReleaseNotesDocument Parse(string version, string markdown)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(version);
        ArgumentNullException.ThrowIfNull(markdown);

        List<string> introduction = [];
        List<ReleaseNotesSection> sections = [];
        List<string> closingNotes = [];
        string? currentHeading = null;
        List<string> currentItems = [];
        List<string> paragraphLines = [];
        bool hasSeenSection = false;

        void FlushParagraph()
        {
            if (paragraphLines.Count == 0)
            {
                return;
            }

            string paragraph = string.Join(' ', paragraphLines);
            (hasSeenSection ? closingNotes : introduction).Add(paragraph);
            paragraphLines.Clear();
        }

        void FlushSection()
        {
            if (currentHeading is null)
            {
                return;
            }

            sections.Add(new ReleaseNotesSection(currentHeading, currentItems.ToArray()));
            currentHeading = null;
            currentItems = [];
        }

        foreach (string sourceLine in markdown.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n'))
        {
            string line = sourceLine.Trim();
            if (line.Length == 0)
            {
                FlushParagraph();
                continue;
            }

            if (line.StartsWith("## ", StringComparison.Ordinal))
            {
                FlushParagraph();
                FlushSection();
                hasSeenSection = true;
                currentHeading = line[3..].Trim();
                continue;
            }

            if (line.StartsWith("# ", StringComparison.Ordinal))
            {
                FlushParagraph();
                continue;
            }

            if (line.StartsWith("- ", StringComparison.Ordinal) && currentHeading is not null)
            {
                FlushParagraph();
                currentItems.Add(line[2..].Trim());
                continue;
            }

            paragraphLines.Add(line);
        }

        FlushParagraph();
        FlushSection();

        return new ReleaseNotesDocument(version, introduction, sections, closingNotes);
    }

    private static bool HasContent(ReleaseNotesDocument document) =>
        document.Introduction.Count > 0
        || document.Sections.Any(section => section.Items.Count > 0)
        || document.ClosingNotes.Count > 0;
}
