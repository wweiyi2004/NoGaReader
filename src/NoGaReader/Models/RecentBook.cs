using System.Text.Json.Serialization;

namespace NoGaReader.Models;

public sealed class RecentBook
{
    public required string Path { get; set; }

    public required string Title { get; set; }

    public ReaderDocumentKind Kind { get; set; }

    public DateTimeOffset LastOpened { get; set; } = DateTimeOffset.Now;

    public int SectionIndex { get; set; }

    public int SectionCount { get; set; } = 1;

    public double SectionProgress { get; set; }

    public double ZoomFactor { get; set; } = 1.0;

    [JsonIgnore]
    public bool FileExists => File.Exists(Path) || Directory.Exists(Path);

    [JsonIgnore]
    public string LastOpenedText => LastOpened.LocalDateTime.ToString("MM-dd HH:mm");

    [JsonIgnore]
    public string ProgressText
    {
        get
        {
            if (!FileExists)
            {
                return Services.UiStrings.IsEnglish ? "File moved" : "文件已移动";
            }

            if (SectionCount <= 1)
            {
                return Kind switch
                {
                    ReaderDocumentKind.Pdf => "PDF",
                    ReaderDocumentKind.Image => Services.UiStrings.IsEnglish ? "Image" : "图片",
                    _ => Services.UiStrings.IsEnglish ? "Opened" : "已打开"
                };
            }

            var progress = Math.Clamp((SectionIndex + SectionProgress) / SectionCount, 0d, 1d);
            return Services.UiStrings.IsEnglish
                ? $"Progress {progress:P0}"
                : $"阅读进度 {progress:P0}";
        }
    }
}
