namespace NoGaReader.Models;

public sealed record TocNode
{
    public required string Title { get; init; }

    public required string FullPath { get; init; }

    /// <summary>
    /// Gets the href fragment without the leading '#', or <see langword="null"/> when absent.
    /// </summary>
    public string? Fragment { get; init; }

    public IReadOnlyList<TocNode> Children { get; init; } = [];

    public IEnumerable<TocNode> Flatten()
    {
        yield return this;
        foreach (var child in Children)
        {
            foreach (var descendant in child.Flatten())
            {
                yield return descendant;
            }
        }
    }
}
