using System.IO;
using System.Text;
using System.Windows;
using System.Windows.Documents;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using DocumentFormat.OpenXml;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Wordprocessing;
using A = DocumentFormat.OpenXml.Drawing;
using DW = DocumentFormat.OpenXml.Drawing.Wordprocessing;
using PIC = DocumentFormat.OpenXml.Drawing.Pictures;
using WpfParagraph = System.Windows.Documents.Paragraph;
using WpfRun = System.Windows.Documents.Run;
using WpfImage = System.Windows.Controls.Image;
using WpfInlineUIContainer = System.Windows.Documents.InlineUIContainer;
using OpenXmlParagraph = DocumentFormat.OpenXml.Wordprocessing.Paragraph;
using OpenXmlRun = DocumentFormat.OpenXml.Wordprocessing.Run;
using OpenXmlText = DocumentFormat.OpenXml.Wordprocessing.Text;
using OpenXmlDrawing = DocumentFormat.OpenXml.Wordprocessing.Drawing;
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
        "文档|*.docx;*.doc;*.odt;*.rtf;*.txt|" +
        "Word 文档|*.docx;*.doc|" +
        "OpenDocument|*.odt|" +
        "RTF|*.rtf|" +
        "文本|*.txt|" +
        "所有文件|*.*";

    public static readonly string SaveFilter =
        "Word 文档 (*.docx)|*.docx|" +
        "RTF (*.rtf)|*.rtf|" +
        "文本 (*.txt)|*.txt";

    public static bool IsOfficeDocumentExtension(string extension)
    {
        extension = NormalizeExtension(extension);
        return extension is ".docx" or ".rtf" or ".txt" or ".odt" or ".doc";
    }

    public static bool CanEditNatively(string extension)
    {
        extension = NormalizeExtension(extension);
        return extension is ".docx" or ".rtf" or ".txt";
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
            ".html" or ".htm" => throw new NotSupportedException(
                "HTML 请使用阅读器安全打开；当前文档编辑器不支持无损 HTML 编辑。"),
            ".doc" => throw new NotSupportedException(
                "暂不支持直接编辑旧版 .doc。请使用 Microsoft Word 或 LibreOffice 将其另存为 DOCX，再打开编辑。"),
            ".odt" => throw new NotSupportedException(
                "暂不支持直接编辑 ODT。请先在主窗口侧栏「转换」中转为 DOCX，再打开编辑。"),
            _ => throw new NotSupportedException($"不支持的文档格式：{extension}")
        };
    }

    public static void Save(FlowDocument document, string path)
    {
        ArgumentNullException.ThrowIfNull(document);
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        var extension = NormalizeExtension(Path.GetExtension(path));
        if (extension == ".txt" && ContainsEmbeddedUiElement(document))
        {
            throw new NotSupportedException(
                "TXT 无法保存图片或其他嵌入对象。请使用“另存为”保存成 DOCX 或 RTF；原文件未被覆盖。");
        }

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
        var mainPart = word.MainDocumentPart
            ?? throw new InvalidDataException("DOCX 缺少主文档。");
        var body = mainPart.Document?.Body
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
                document.Blocks.Add(ConvertParagraph(paragraph, mainPart));
            }
            else if (element is OpenXmlTable table)
            {
                document.Blocks.Add(ConvertTable(table, mainPart));
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

    private static WpfParagraph ConvertParagraph(
        OpenXmlParagraph paragraph,
        MainDocumentPart mainPart)
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
            foreach (var child in run.ChildElements)
            {
                if (child is OpenXmlText text)
                {
                    result.Inlines.Add(ConvertRunText(run, text.Text ?? string.Empty));
                    continue;
                }

                if (child is Break)
                {
                    result.Inlines.Add(new LineBreak());
                    continue;
                }

                if (child is OpenXmlDrawing drawing &&
                    TryConvertImage(drawing, mainPart) is { } image)
                {
                    result.Inlines.Add(image);
                }
            }
        }

        if (!result.Inlines.Any())
        {
            result.Inlines.Add(new WpfRun(string.Empty));
        }

        return result;
    }

    private static WpfRun ConvertRunText(OpenXmlRun run, string text)
    {
        var result = new WpfRun(text);
        var props = run.RunProperties;
        if (props?.Bold is not null)
        {
            result.FontWeight = FontWeights.Bold;
        }

        if (props?.Italic is not null)
        {
            result.FontStyle = FontStyles.Italic;
        }

        if (props?.Underline is not null)
        {
            result.TextDecorations = TextDecorations.Underline;
        }

        if (props?.FontSize?.Val?.Value is { } sizeValue &&
            double.TryParse(sizeValue, out var halfPoints) &&
            halfPoints > 0)
        {
            result.FontSize = halfPoints / 2.0;
        }

        return result;
    }

    private static WpfInlineUIContainer? TryConvertImage(
        OpenXmlDrawing drawing,
        MainDocumentPart mainPart)
    {
        var relationshipId = drawing.Descendants<A.Blip>()
            .Select(blip => blip.Embed?.Value)
            .FirstOrDefault(value => !string.IsNullOrWhiteSpace(value));
        if (string.IsNullOrWhiteSpace(relationshipId))
        {
            return null;
        }

        ImagePart? imagePart;
        try
        {
            imagePart = mainPart.GetPartById(relationshipId) as ImagePart;
        }
        catch (Exception exception) when (exception is ArgumentException or KeyNotFoundException)
        {
            return null;
        }

        if (imagePart is null)
        {
            return null;
        }

        BitmapSource bitmap;
        try
        {
            using var partStream = imagePart.GetStream(FileMode.Open, FileAccess.Read);
            using var imageStream = new MemoryStream();
            partStream.CopyTo(imageStream);
            imageStream.Position = 0;
            var decoder = BitmapDecoder.Create(
                imageStream,
                BitmapCreateOptions.PreservePixelFormat,
                BitmapCacheOption.OnLoad);
            bitmap = decoder.Frames.FirstOrDefault()
                ?? throw new InvalidDataException("DOCX 图片没有可解码的帧。");
            if (bitmap.CanFreeze)
            {
                bitmap.Freeze();
            }
        }
        catch (Exception exception) when (
            exception is IOException or InvalidDataException or NotSupportedException or FormatException)
        {
            return null;
        }

        var image = new WpfImage
        {
            Source = bitmap,
            Stretch = Stretch.Uniform,
            MaxWidth = 640,
            MaxHeight = 480
        };

        var extent = drawing.Descendants<DW.Extent>().FirstOrDefault();
        if (extent?.Cx?.Value is > 0 && extent.Cy?.Value is > 0)
        {
            var width = extent.Cx.Value / 9525d;
            var height = extent.Cy.Value / 9525d;
            var scale = Math.Min(1d, Math.Min(640d / width, 480d / height));
            image.Width = width * scale;
            image.Height = height * scale;
        }

        return new WpfInlineUIContainer(image);
    }

    private static WpfTable ConvertTable(
        OpenXmlTable table,
        MainDocumentPart mainPart)
    {
        var result = new WpfTable
        {
            CellSpacing = 0,
            Margin = new Thickness(0, 0, 0, 12)
        };
        var columnCount = table.Elements<OpenXmlTableRow>()
            .Select(row => row.Elements<OpenXmlTableCell>().Count())
            .DefaultIfEmpty(1)
            .Max();
        for (var index = 0; index < Math.Max(1, columnCount); index++)
        {
            result.Columns.Add(new TableColumn());
        }

        var rowGroup = new TableRowGroup();
        foreach (var row in table.Elements<OpenXmlTableRow>())
        {
            var wpfRow = new WpfTableRow();
            foreach (var cell in row.Elements<OpenXmlTableCell>())
            {
                var wpfCell = new WpfTableCell
                {
                    BorderBrush = Brushes.Gray,
                    BorderThickness = new Thickness(0.5),
                    Padding = new Thickness(4)
                };
                foreach (var element in cell.Elements())
                {
                    if (element is OpenXmlParagraph paragraph)
                    {
                        wpfCell.Blocks.Add(ConvertParagraph(paragraph, mainPart));
                    }
                    else if (element is OpenXmlTable nestedTable)
                    {
                        wpfCell.Blocks.Add(ConvertTable(nestedTable, mainPart));
                    }
                }

                if (!wpfCell.Blocks.Any())
                {
                    wpfCell.Blocks.Add(new WpfParagraph());
                }

                wpfRow.Cells.Add(wpfCell);
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
        uint nextDrawingId = 1;

        foreach (var block in document.Blocks)
        {
            if (block is WpfParagraph paragraph)
            {
                body.AppendChild(ConvertToOpenXmlParagraph(paragraph, mainPart, ref nextDrawingId));
            }
            else if (block is WpfTable table)
            {
                body.AppendChild(ConvertToOpenXmlTable(table, mainPart, ref nextDrawingId));
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

    private static OpenXmlParagraph ConvertToOpenXmlParagraph(
        WpfParagraph paragraph,
        MainDocumentPart mainPart,
        ref uint nextDrawingId)
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

            if (inline is WpfInlineUIContainer container)
            {
                if (container.Child is not WpfImage image)
                {
                    throw new NotSupportedException("DOCX 暂不支持保存此嵌入对象；文档未被覆盖。");
                }

                result.AppendChild(CreateImageRun(image, mainPart, ref nextDrawingId));
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

    private static OpenXmlRun CreateImageRun(
        WpfImage image,
        MainDocumentPart mainPart,
        ref uint nextDrawingId)
    {
        if (image.Source is not BitmapSource bitmap)
        {
            throw new NotSupportedException("DOCX 只能保存位图类型的嵌入图片；文档未被覆盖。");
        }

        using var imageStream = new MemoryStream();
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        encoder.Save(imageStream);
        imageStream.Position = 0;

        var imagePart = mainPart.AddImagePart(ImagePartType.Png);
        imagePart.FeedData(imageStream);
        var relationshipId = mainPart.GetIdOfPart(imagePart);
        var (widthEmus, heightEmus) = GetImageExtent(image, bitmap);
        var drawingId = nextDrawingId++;
        var pictureName = $"Picture {drawingId}";

        var nonVisualProperties = new PIC.NonVisualPictureProperties(
            new PIC.NonVisualDrawingProperties
            {
                Id = drawingId,
                Name = pictureName
            },
            new PIC.NonVisualPictureDrawingProperties());
        var blipFill = new PIC.BlipFill(
            new A.Blip
            {
                Embed = relationshipId,
                CompressionState = A.BlipCompressionValues.Print
            },
            new A.Stretch(new A.FillRectangle()));
        var shapeProperties = new PIC.ShapeProperties(
            new A.Transform2D(
                new A.Offset { X = 0L, Y = 0L },
                new A.Extents { Cx = widthEmus, Cy = heightEmus }),
            new A.PresetGeometry(new A.AdjustValueList())
            {
                Preset = A.ShapeTypeValues.Rectangle
            });
        var picture = new PIC.Picture(nonVisualProperties, blipFill, shapeProperties);
        var graphicData = new A.GraphicData(picture)
        {
            Uri = "http://schemas.openxmlformats.org/drawingml/2006/picture"
        };
        var inline = new DW.Inline(
            new DW.Extent { Cx = widthEmus, Cy = heightEmus },
            new DW.EffectExtent
            {
                LeftEdge = 0L,
                TopEdge = 0L,
                RightEdge = 0L,
                BottomEdge = 0L
            },
            new DW.DocProperties { Id = drawingId, Name = pictureName },
            new DW.NonVisualGraphicFrameDrawingProperties(
                new A.GraphicFrameLocks { NoChangeAspect = true }),
            new A.Graphic(graphicData))
        {
            DistanceFromTop = 0U,
            DistanceFromBottom = 0U,
            DistanceFromLeft = 0U,
            DistanceFromRight = 0U
        };

        return new OpenXmlRun(new OpenXmlDrawing(inline));
    }

    private static (long WidthEmus, long HeightEmus) GetImageExtent(
        WpfImage image,
        BitmapSource bitmap)
    {
        var naturalWidth = bitmap.DpiX > 0
            ? bitmap.PixelWidth * 96d / bitmap.DpiX
            : bitmap.PixelWidth;
        var naturalHeight = bitmap.DpiY > 0
            ? bitmap.PixelHeight * 96d / bitmap.DpiY
            : bitmap.PixelHeight;
        var width = double.IsFinite(image.Width) && image.Width > 0
            ? image.Width
            : image.ActualWidth > 0 ? image.ActualWidth : naturalWidth;
        var height = double.IsFinite(image.Height) && image.Height > 0
            ? image.Height
            : image.ActualHeight > 0 ? image.ActualHeight : naturalHeight;

        if (width <= 0 || height <= 0)
        {
            throw new InvalidDataException("嵌入图片没有有效尺寸；文档未被覆盖。");
        }

        var maxWidth = double.IsFinite(image.MaxWidth) && image.MaxWidth > 0
            ? image.MaxWidth
            : double.PositiveInfinity;
        var maxHeight = double.IsFinite(image.MaxHeight) && image.MaxHeight > 0
            ? image.MaxHeight
            : double.PositiveInfinity;
        var scale = Math.Min(1d, Math.Min(maxWidth / width, maxHeight / height));
        width *= scale;
        height *= scale;

        const double emusPerDip = 9525d;
        return (
            Math.Max(1L, (long)Math.Round(width * emusPerDip)),
            Math.Max(1L, (long)Math.Round(height * emusPerDip)));
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

    private static OpenXmlTable ConvertToOpenXmlTable(
        WpfTable table,
        MainDocumentPart mainPart,
        ref uint nextDrawingId)
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
                    var openXmlCell = new OpenXmlTableCell();
                    foreach (var block in cell.Blocks)
                    {
                        if (block is WpfParagraph paragraph)
                        {
                            openXmlCell.AppendChild(ConvertToOpenXmlParagraph(
                                paragraph,
                                mainPart,
                                ref nextDrawingId));
                        }
                        else if (block is WpfTable nestedTable)
                        {
                            openXmlCell.AppendChild(ConvertToOpenXmlTable(
                                nestedTable,
                                mainPart,
                                ref nextDrawingId));
                        }
                        else
                        {
                            var text = new TextRange(block.ContentStart, block.ContentEnd)
                                .Text
                                .TrimEnd('\r', '\n');
                            openXmlCell.AppendChild(new OpenXmlParagraph(
                                new OpenXmlRun(new OpenXmlText(text))));
                        }
                    }

                    if (!openXmlCell.ChildElements.Any() || openXmlCell.LastChild is OpenXmlTable)
                    {
                        openXmlCell.AppendChild(new OpenXmlParagraph(
                            new OpenXmlRun(new OpenXmlText(string.Empty))));
                    }

                    openXmlRow.AppendChild(openXmlCell);
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

    private static bool ContainsEmbeddedUiElement(FlowDocument document)
    {
        var pointer = document.ContentStart;
        while (pointer is not null && pointer.CompareTo(document.ContentEnd) < 0)
        {
            if (pointer.GetPointerContext(LogicalDirection.Forward) == TextPointerContext.ElementStart &&
                pointer.GetAdjacentElement(LogicalDirection.Forward) is WpfInlineUIContainer)
            {
                return true;
            }

            pointer = pointer.GetNextContextPosition(LogicalDirection.Forward);
        }

        return false;
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
