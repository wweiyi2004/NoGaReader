namespace NoGaReader.Models;

public enum ConversionJobStatus
{
    Pending,
    Running,
    Succeeded,
    Failed,
    Cancelled
}

public sealed record ConversionFormat(string Extension, string DisplayName)
{
    public string NormalizedExtension =>
        Extension.StartsWith(".", StringComparison.Ordinal)
            ? Extension.ToLowerInvariant()
            : "." + Extension.ToLowerInvariant();
}

public sealed class ConversionJob
{
    public Guid Id { get; } = Guid.NewGuid();

    public required string SourcePath { get; init; }

    public required string TargetExtension { get; init; }

    public required string OutputDirectory { get; init; }

    public string? OutputPath { get; set; }

    public ConversionJobStatus Status { get; set; } = ConversionJobStatus.Pending;

    public string StatusText { get; set; } = "准备中";

    public string? ErrorMessage { get; set; }

    public DateTimeOffset? StartedAt { get; set; }

    public DateTimeOffset? CompletedAt { get; set; }

    public string FileName => Path.GetFileName(SourcePath);

    public string TargetLabel => TargetExtension.TrimStart('.').ToUpperInvariant();
}

public sealed record ConversionResult(
    bool Succeeded,
    string? OutputPath,
    string? ErrorMessage,
    int ExitCode,
    string StandardOutput,
    string StandardError);
