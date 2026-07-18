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
                return "文件已移动";
            }

            if (SectionCount <= 1)
            {
                return Kind switch
                {
                    ReaderDocumentKind.Pdf => "PDF",
                    ReaderDocumentKind.Image => "图片",
                    _ => "已打开"
                };
            }

            var progress = Math.Clamp((SectionIndex + SectionProgress) / SectionCount, 0d, 1d);
            return $"阅读进度 {progress:P0}";
        }
    }
}
