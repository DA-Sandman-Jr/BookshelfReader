namespace BookshelfReader.Core.Models;

public sealed class VisionReadResult
{
    public string? Model { get; init; }

    public List<VisionBookEntry> Books { get; init; } = new();
    public List<string> Notes { get; init; } = new();
}

public sealed record VisionBookEntry(string Title, string Author, double Confidence);
