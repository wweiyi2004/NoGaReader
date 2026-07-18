using System.IO;
using System.Text;
using System.Windows;
using System.Windows.Documents;
using System.Windows.Media;
using DocumentFormat.OpenXml;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Wordprocessing;
using WpfParagraph = System.Windows.Documents.Paragraph;
using WpfRun = System.Windows.Documents.Run;
using OpenXmlParagraph = DocumentFormat.OpenXml.Wordprocessing.Paragraph;
using OpenXmlRun = DocumentFormat.OpenXml.Wordprocessing.Run;
using OpenXmlText = DocumentFormat.OpenXml.Wordprocessing.Text;
using OpenXmlBold = DocumentFormat.OpenXml.Wordprocessing.Bold;
using OpenXmlItalic = DocumentFormat.OpenXml.Wordprocessing.Italic;
using OpenXmlUnderline = DocumentFormat.OpenXml.Wordprocessing.Underline;
using WpfTable = System.Windows.Documents.Table;
using OpenXmlTable = DocumentFormat.OpenXml.Wordprocessing.Table;
using OpenXmlTableRow = DocumentFormat.OpenXml.Wordprocessing.TableRow;
using OpenXmlTableCell = DocumentFormat.OpenXml.Wordprocessing.TableCell;
using WpfTableRow = System.Windows.Documents.TableRow;
using WpfTableCell = System.Windows.Documents.TableCell;
using WpfFontFamily = System.Windows.Media.FontFamily;
using WpfTextAlignment = System.Windows.TextAlignment;

namespace NoGaReader.Services;

public static class OfficeDocumentService
{
    public static readonly string OpenFilter =
        "文档|*.docx;*.rtf;*.txt;*.html;*.htm|" +
        "Word 文档|*.docx|" +
        "RTF|*.rtf|" +
        "文本|*.txt|" +
        "HTML|*.html;*.htm|" +
        "所有文件|*.*";

    public static readonly string SaveFilter =
        "Word 文档 (*.docx)|*.docx|" +
        "RTF (*.rtf)|*.rtf|" +
        "文本 (*.txt)|*.txt|" +
        "HTML (*.html)|*.html";

    public static bool IsOfficeDocumentExtension(string extension)
    {
        extension = NormalizeExtension(extension);
        return extension is ".docx" or ".rtf" or ".txt" or ".html" or ".htm" or ".odt" or ".doc";
    }

    public static bool CanEditNatively(string extension)
    {
        extension = NormalizeExtension(extension);
        return extension is ".docx" or ".rtf" or ".txt" or ".html" or ".htm";
    }

    public static bool RequiresSafeSaveAs(string path)
    {
        if (!Path.GetExtension(path).Equals(".docx", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        using var word = WordprocessingDocument.Open(path, false);
        var mainPart = word.MainDocumentPart;
        var body = mainPart?.Document?.Body;
        if (mainPart is null || body is null ||
            mainPart.Parts.Any() ||
            mainPart.ExternalRelationships.Any() ||
            mainPart.HyperlinkRelationships.Any())
        {
            return true;
        }

        return body.Elements().Any(element => !IsLosslesslyEditableParagraph(element));
    }

    public static FlowDocument Load(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        if (!File.Exists(path))
        {
            throw new FileNotFoundException("文档不存在。", path);
        }

        var extension = NormalizeExtension(Path.GetExtension(path));
        return extension switch
        {
            ".docx" => LoadDocx(path),
            ".rtf" => LoadRtf(path),
            ".txt" => LoadPlainText(path),
            ".html" or ".htm" => LoadHtml(path),
            ".doc" => throw new NotSupportedException(
                "暂不支持直接编辑旧版 .doc。请先在“转换”中转为 DOCX，或另存为 DOCX。"),
            ".odt" => throw new NotSupportedException(
                "暂不支持直接编辑 ODT。请先在“转换”中转为 DOCX。"),
            _ => throw new NotSupportedException($"不支持的文档格式：{extension}")
        };
    }

    public static void Save(FlowDocument document, string path)
    {
        ArgumentNullException.ThrowIfNull(document);
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        var extension = NormalizeExtension(Path.GetExtension(path));
        var directory = Path.GetDirectoryName(path);
        if (!string.IsNullOrWhiteSpace(directory))
        {
            Directory.CreateDirectory(directory);
        }

        var tempPath = path + ".tmp-" + Guid.NewGuid().ToString("N");
        try
        {
            switch (extension)
            {
                case ".docx":
                    SaveDocx(document, tempPath);
                    break;
                case ".rtf":
                    SaveRtf(document, tempPath);
                    break;
                case ".txt":
                    SavePlainText(document, tempPath);
                    break;
                case ".html" or ".htm":
                    SaveHtml(document, tempPath);
                    break;
                default:
                    throw new NotSupportedException($"不支持另存为：{extension}");
            }

            if (File.Exists(path))
            {
                File.Replace(tempPath, path, null);
            }
            else
            {
                File.Move(tempPath, path);
            }
        }
        finally
        {
            if (File.Exists(tempPath))
            {
                try { File.Delete(tempPath); } catch { /* best effort */ }
            }
        }
    }

    public static FlowDocument CreateBlank(string title = "未命名文档")
    {
        var document = new FlowDocument
        {
            FontFamily = new WpfFontFamily("Segoe UI"),
            FontSize = 16,
            PagePadding = new Thickness(48),
            LineHeight = 28
        };
        document.Blocks.Add(new WpfParagraph(new WpfRun(title))
        {
            FontSize = 22,
            FontWeight = FontWeights.SemiBold,
            Margin = new Thickness(0, 0, 0, 12)
        });
        document.Blocks.Add(new WpfParagraph(new WpfRun(string.Empty)));
        return document;
    }

    public static string GetPlainText(FlowDocument document)
    {
        return new TextRange(document.ContentStart, document.ContentEnd).Text.TrimEnd();
    }

    public static int CountWords(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return 0;
        }

        var count = 0;
        var inWord = false;
        foreach (var ch in text)
        {
            if (char.IsWhiteSpace(ch))
            {
                inWord = false;
            }
            else if (!inWord)
            {
                inWord = true;
                count++;
            }
        }

        return count;
    }

    private static FlowDocument LoadDocx(string path)
    {
        using var word = WordprocessingDocument.Open(path, false);
        var body = word.MainDocumentPart?.Document?.Body
            ?? throw new InvalidDataException("DOCX 缺少正文。");

        var document = new FlowDocument
        {
            FontFamily = new WpfFontFamily("Segoe UI"),
            FontSize = 16,
            PagePadding = new Thickness(48),
            LineHeight = 28
        };

        foreach (var element in body.Elements())
        {
            if (element is OpenXmlParagraph paragraph)
            {
                document.Blocks.Add(ConvertParagraph(paragraph));
            }
            else if (element is OpenXmlTable table)
            {
                document.Blocks.Add(ConvertTable(table));
            }
        }

        if (!document.Blocks.Any())
        {
            document.Blocks.Add(new WpfParagraph());
        }

        return document;
    }

    private static bool IsLosslesslyEditableParagraph(OpenXmlElement element)
    {
        if (element is not OpenXmlParagraph paragraph)
        {
            return false;
        }

        foreach (var child in paragraph.ChildElements)
        {
            if (child is ParagraphProperties properties)
            {
                if (properties.ChildElements.Any(property => property is not Justification))
                {
                    return false;
                }

                continue;
            }

            if (child is not OpenXmlRun run)
            {
                return false;
            }

            foreach (var runChild in run.ChildElements)
            {
                if (runChild is RunProperties runProperties)
                {
                    if (runProperties.ChildElements.Any(property =>
                            property is not (OpenXmlBold or OpenXmlItalic or OpenXmlUnderline or FontSize)))
                    {
                        return false;
                    }

                    continue;
                }

                if (runChild is not (OpenXmlText or Break))
                {
                    return false;
                }
            }
        }

        return true;
    }

    private static WpfParagraph ConvertParagraph(OpenXmlParagraph paragraph)
    {
        var result = new WpfParagraph
        {
            Margin = new Thickness(0, 0, 0, 10)
        };

        var alignment = paragraph.ParagraphProperties?.Justification?.Val?.Value;
        if (alignment == JustificationValues.Center)
        {
            result.TextAlignment = WpfTextAlignment.Center;
        }
        else if (alignment == JustificationValues.Right)
        {
            result.TextAlignment = WpfTextAlignment.Right;
        }
        else if (alignment == JustificationValues.Both)
        {
            result.TextAlignment = WpfTextAlignment.Justify;
        }

        foreach (var run in paragraph.Elements<OpenXmlRun>())
        {
            var text = string.Concat(run.Elements<OpenXmlText>().Select(item => item.Text));
            if (text.Length == 0 && run.Elements<Break>().Any())
            {
                result.Inlines.Add(new LineBreak());
                continue;
            }

            if (text.Length == 0)
            {
                continue;
            }

            var wpfRun = new WpfRun(text);
            var props = run.RunProperties;
            if (props?.Bold is not null)
            {
                wpfRun.FontWeight = FontWeights.Bold;
            }

            if (props?.Italic is not null)
            {
                wpfRun.FontStyle = FontStyles.Italic;
            }

            if (props?.Underline is not null)
            {
                wpfRun.TextDecorations = TextDecorations.Underline;
            }

            if (props?.FontSize?.Val?.Value is { } sizeValue &&
                double.TryParse(sizeValue, out var halfPoints) &&
                halfPoints > 0)
            {
                wpfRun.FontSize = halfPoints / 2.0;
            }

            result.Inlines.Add(wpfRun);
        }

        if (!result.Inlines.Any())
        {
            result.Inlines.Add(new WpfRun(string.Empty));
        }

        return result;
    }

    private static WpfTable ConvertTable(OpenXmlTable table)
    {
        var result = new WpfTable
        {
            CellSpacing = 0,
            Margin = new Thickness(0, 0, 0, 12)
        };
        result.Columns.Add(new TableColumn());
        var rowGroup = new TableRowGroup();
        foreach (var row in table.Elements<OpenXmlTableRow>())
        {
            var wpfRow = new WpfTableRow();
            foreach (var cell in row.Elements<OpenXmlTableCell>())
            {
                var cellText = string.Join(
                    " ",
                    cell.Descendants<OpenXmlText>().Select(item => item.Text));
                var paragraph = new WpfParagraph(new WpfRun(cellText))
                {
                    Margin = new Thickness(4)
                };
                wpfRow.Cells.Add(new WpfTableCell(paragraph)
                {
                    BorderBrush = Brushes.Gray,
                    BorderThickness = new Thickness(0.5),
                    Padding = new Thickness(4)
                });
            }

            rowGroup.Rows.Add(wpfRow);
        }

        result.RowGroups.Add(rowGroup);
        return result;
    }

    private static void SaveDocx(FlowDocument document, string path)
    {
        using var word = WordprocessingDocument.Create(path, WordprocessingDocumentType.Document);
        var mainPart = word.AddMainDocumentPart();
        mainPart.Document = new Document(new Body());
        var body = mainPart.Document.Body!;

        foreach (var block in document.Blocks)
        {
            if (block is WpfParagraph paragraph)
            {
                body.AppendChild(ConvertToOpenXmlParagraph(paragraph));
            }
            else if (block is WpfTable table)
            {
                body.AppendChild(ConvertToOpenXmlTable(table));
            }
            else
            {
                var text = new TextRange(block.ContentStart, block.ContentEnd).Text.TrimEnd('\r', '\n');
                body.AppendChild(new OpenXmlParagraph(new OpenXmlRun(new OpenXmlText(text))));
            }
        }

        if (!body.Elements().Any())
        {
            body.AppendChild(new OpenXmlParagraph(new OpenXmlRun(new OpenXmlText(string.Empty))));
        }

        mainPart.Document.Save();
    }

    private static OpenXmlParagraph ConvertToOpenXmlParagraph(WpfParagraph paragraph)
    {
        var result = new OpenXmlParagraph();
        if (paragraph.TextAlignment == WpfTextAlignment.Center)
        {
            result.ParagraphProperties = new ParagraphProperties(
                new Justification { Val = JustificationValues.Center });
        }
        else if (paragraph.TextAlignment == WpfTextAlignment.Right)
        {
            result.ParagraphProperties = new ParagraphProperties(
                new Justification { Val = JustificationValues.Right });
        }
        else if (paragraph.TextAlignment == WpfTextAlignment.Justify)
        {
            result.ParagraphProperties = new ParagraphProperties(
                new Justification { Val = JustificationValues.Both });
        }

        foreach (var inline in paragraph.Inlines)
        {
            if (inline is LineBreak)
            {
                result.AppendChild(new OpenXmlRun(new Break()));
                continue;
            }

            if (inline is not WpfRun run)
            {
                var plain = new TextRange(inline.ContentStart, inline.ContentEnd).Text;
                if (plain.Length > 0)
                {
                    result.AppendChild(CreateRun(plain, bold: false, italic: false, underline: false, fontSize: null));
                }

                continue;
            }

            result.AppendChild(CreateRun(
                run.Text,
                run.FontWeight == FontWeights.Bold || run.FontWeight == FontWeights.SemiBold,
                run.FontStyle == FontStyles.Italic,
                run.TextDecorations?.Contains(TextDecorations.Underline[0]) == true,
                run.FontSize));
        }

        if (!result.Elements<OpenXmlRun>().Any())
        {
            result.AppendChild(new OpenXmlRun(new OpenXmlText(string.Empty)));
        }

        return result;
    }

    private static OpenXmlRun CreateRun(string text, bool bold, bool italic, bool underline, double? fontSize)
    {
        var run = new OpenXmlRun();
        var props = new RunProperties();
        if (bold)
        {
            props.AppendChild(new OpenXmlBold());
        }

        if (italic)
        {
            props.AppendChild(new OpenXmlItalic());
        }

        if (underline)
        {
            props.AppendChild(new OpenXmlUnderline { Val = UnderlineValues.Single });
        }

        if (fontSize is > 0)
        {
            props.AppendChild(new FontSize { Val = ((int)Math.Round(fontSize.Value * 2)).ToString() });
        }

        if (props.HasChildren)
        {
            run.AppendChild(props);
        }

        run.AppendChild(new OpenXmlText(text)
        {
            Space = SpaceProcessingModeValues.Preserve
        });
        return run;
    }

    private static OpenXmlTable ConvertToOpenXmlTable(WpfTable table)
    {
        var result = new OpenXmlTable();
        var props = new TableProperties(
            new TableBorders(
                new TopBorder { Val = BorderValues.Single, Size = 4 },
                new BottomBorder { Val = BorderValues.Single, Size = 4 },
                new LeftBorder { Val = BorderValues.Single, Size = 4 },
                new RightBorder { Val = BorderValues.Single, Size = 4 },
                new InsideHorizontalBorder { Val = BorderValues.Single, Size = 4 },
                new InsideVerticalBorder { Val = BorderValues.Single, Size = 4 }));
        result.AppendChild(props);
        foreach (var rowGroup in table.RowGroups)
        {
            foreach (var row in rowGroup.Rows)
            {
                var openXmlRow = new OpenXmlTableRow();
                foreach (var cell in row.Cells)
                {
                    var text = new TextRange(cell.ContentStart, cell.ContentEnd).Text.TrimEnd('\r', '\n');
                    openXmlRow.AppendChild(new OpenXmlTableCell(
                        new OpenXmlParagraph(new OpenXmlRun(new OpenXmlText(text)))));
                }

                result.AppendChild(openXmlRow);
            }
        }

        return result;
    }

    private static FlowDocument LoadRtf(string path)
    {
        var document = new FlowDocument();
        var range = new TextRange(document.ContentStart, document.ContentEnd);
        using var stream = File.OpenRead(path);
        range.Load(stream, DataFormats.Rtf);
        return document;
    }

    private static void SaveRtf(FlowDocument document, string path)
    {
        var range = new TextRange(document.ContentStart, document.ContentEnd);
        using var stream = File.Create(path);
        range.Save(stream, DataFormats.Rtf);
    }

    private static FlowDocument LoadPlainText(string path)
    {
        var text = File.ReadAllText(path);
        var document = new FlowDocument
        {
            FontFamily = new WpfFontFamily("Consolas, Segoe UI"),
            FontSize = 15,
            PagePadding = new Thickness(48),
            LineHeight = 24
        };
        foreach (var line in text.Replace("\r\n", "\n", StringComparison.Ordinal)
                     .Replace('\r', '\n')
                     .Split('\n'))
        {
            document.Blocks.Add(new WpfParagraph(new WpfRun(line))
            {
                Margin = new Thickness(0, 0, 0, 4)
            });
        }

        if (!document.Blocks.Any())
        {
            document.Blocks.Add(new WpfParagraph());
        }

        return document;
    }

    private static void SavePlainText(FlowDocument document, string path)
    {
        File.WriteAllText(path, GetPlainText(document), new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
    }

    private static FlowDocument LoadHtml(string path)
    {
        var html = File.ReadAllText(path);
        var document = new FlowDocument();
        var range = new TextRange(document.ContentStart, document.ContentEnd);
        using var stream = new MemoryStream(Encoding.UTF8.GetBytes(html));
        try
        {
            range.Load(stream, DataFormats.Html);
        }
        catch
        {
            return LoadPlainText(path);
        }

        return document;
    }

    private static void SaveHtml(FlowDocument document, string path)
    {
        var range = new TextRange(document.ContentStart, document.ContentEnd);
        using var stream = File.Create(path);
        range.Save(stream, DataFormats.Html);
    }

    private static string NormalizeExtension(string extension)
    {
        if (string.IsNullOrWhiteSpace(extension))
        {
            return string.Empty;
        }

        extension = extension.Trim();
        if (!extension.StartsWith(".", StringComparison.Ordinal))
        {
            extension = "." + extension;
        }

        return extension.ToLowerInvariant();
    }
}
