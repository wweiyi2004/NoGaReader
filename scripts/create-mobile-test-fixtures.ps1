param(
    [string]$OutputDirectory = (Join-Path (Split-Path -Parent $PSScriptRoot) "artifacts\android\fixtures")
)

$ErrorActionPreference = "Stop"
Add-Type -AssemblyName System.IO.Compression

New-Item -ItemType Directory -Path $OutputDirectory -Force | Out-Null
$utf8 = [System.Text.UTF8Encoding]::new($false)

[System.IO.File]::WriteAllText(
    (Join-Path $OutputDirectory "sample.txt"),
    "NoGaReader Android TXT 测试`n`n第一章`n这是一份用于手机端排版、滚动与进度恢复测试的纯文本。`n`n第二章`n中文、English、数字 12345 应当正常显示。",
    $utf8)

[System.IO.File]::WriteAllText(
    (Join-Path $OutputDirectory "sample.fb2"),
    @'
<?xml version="1.0" encoding="utf-8"?>
<FictionBook xmlns="http://www.gribuser.ru/xml/fictionbook/2.0">
  <description>
    <title-info>
      <genre>testing</genre>
      <author><first-name>NoGaReader</first-name><last-name>QA</last-name></author>
      <book-title>Android FB2 测试书</book-title>
      <annotation><p>用于验证 FB2 元数据、章节与正文排版。</p></annotation>
    </title-info>
  </description>
  <body>
    <section>
      <title><p>第一章</p></title>
      <p>这是第一章正文。中文标点、<strong>粗体</strong>和段落应当正常显示。</p>
      <p>第二个段落用于验证行距和滚动位置。</p>
    </section>
    <section>
      <title><p>第二章</p></title>
      <p>这是第二章正文，用于验证目录跳转与章节进度。</p>
    </section>
  </body>
</FictionBook>
'@,
    $utf8)

$epubPath = Join-Path $OutputDirectory "sample.epub"
$epubStream = [System.IO.File]::Create($epubPath)
try
{
    $archive = [System.IO.Compression.ZipArchive]::new(
        $epubStream,
        [System.IO.Compression.ZipArchiveMode]::Create,
        $false)
    try
    {
        function Add-EpubTextEntry
        {
            param(
                [System.IO.Compression.ZipArchive]$Archive,
                [string]$Name,
                [string]$Content,
                [System.IO.Compression.CompressionLevel]$Compression =
                    [System.IO.Compression.CompressionLevel]::Optimal
            )

            $entry = $Archive.CreateEntry($Name, $Compression)
            $writer = [System.IO.StreamWriter]::new($entry.Open(), $utf8)
            try
            {
                $writer.Write($Content)
            }
            finally
            {
                $writer.Dispose()
            }
        }

        Add-EpubTextEntry $archive "mimetype" "application/epub+zip" `
            ([System.IO.Compression.CompressionLevel]::NoCompression)
        Add-EpubTextEntry $archive "META-INF/container.xml" @'
<?xml version="1.0"?>
<container xmlns="urn:oasis:names:tc:opendocument:xmlns:container" version="1.0">
  <rootfiles>
    <rootfile full-path="OEBPS/content.opf" media-type="application/oebps-package+xml"/>
  </rootfiles>
</container>
'@
        Add-EpubTextEntry $archive "OEBPS/content.opf" @'
<?xml version="1.0" encoding="utf-8"?>
<package xmlns="http://www.idpf.org/2007/opf" version="3.0" unique-identifier="book-id">
  <metadata xmlns:dc="http://purl.org/dc/elements/1.1/">
    <dc:identifier id="book-id">nogareader-android-fixture</dc:identifier>
    <dc:title>Android EPUB 测试书</dc:title>
    <dc:creator>NoGaReader QA</dc:creator>
    <dc:language>zh-CN</dc:language>
  </metadata>
  <manifest>
    <item id="nav" href="nav.xhtml" media-type="application/xhtml+xml" properties="nav"/>
    <item id="chapter-1" href="chapter1.xhtml" media-type="application/xhtml+xml"/>
    <item id="chapter-2" href="chapter2.xhtml" media-type="application/xhtml+xml"/>
  </manifest>
  <spine><itemref idref="chapter-1"/><itemref idref="chapter-2"/></spine>
</package>
'@
        Add-EpubTextEntry $archive "OEBPS/nav.xhtml" @'
<html xmlns="http://www.w3.org/1999/xhtml" xmlns:epub="http://www.idpf.org/2007/ops">
  <head><title>目录</title></head>
  <body><nav epub:type="toc"><ol>
    <li><a href="chapter1.xhtml">第一章</a></li>
    <li><a href="chapter2.xhtml">第二章</a></li>
  </ol></nav></body>
</html>
'@
        Add-EpubTextEntry $archive "OEBPS/chapter1.xhtml" @'
<html xmlns="http://www.w3.org/1999/xhtml">
  <head><title>第一章</title></head>
  <body><h1>第一章</h1><p>这是 Android EPUB 测试正文。</p>
  <p>中文、English、<strong>粗体</strong>和多段文本应当正确排版。</p></body>
</html>
'@
        Add-EpubTextEntry $archive "OEBPS/chapter2.xhtml" @'
<html xmlns="http://www.w3.org/1999/xhtml">
  <head><title>第二章</title></head>
  <body><h1>第二章</h1><p>目录跳转和下一章按钮应当来到这里。</p></body>
</html>
'@
    }
    finally
    {
        $archive.Dispose()
    }
}
finally
{
    $epubStream.Dispose()
}

function New-SimplePdf
{
    param([string]$Path)

    $ascii = [System.Text.Encoding]::ASCII
    $page1 = "BT /F1 28 Tf 72 720 Td (NoGaReader PDF Test) Tj 0 -48 Td /F1 16 Tf (Page 1 - Android PdfRenderer) Tj ET"
    $page2 = "BT /F1 28 Tf 72 720 Td (NoGaReader PDF Test) Tj 0 -48 Td /F1 16 Tf (Page 2 - navigation works) Tj ET"
    $objects = @(
        "<< /Type /Catalog /Pages 2 0 R >>",
        "<< /Type /Pages /Kids [3 0 R 5 0 R] /Count 2 >>",
        "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 595 842] /Resources << /Font << /F1 7 0 R >> >> /Contents 4 0 R >>",
        "<< /Length $($ascii.GetByteCount($page1)) >>`nstream`n$page1`nendstream",
        "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 595 842] /Resources << /Font << /F1 7 0 R >> >> /Contents 6 0 R >>",
        "<< /Length $($ascii.GetByteCount($page2)) >>`nstream`n$page2`nendstream",
        "<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica >>"
    )

    $stream = [System.IO.MemoryStream]::new()
    try
    {
        function Write-PdfAscii
        {
            param([System.IO.Stream]$Stream, [string]$Value)
            $bytes = $ascii.GetBytes($Value)
            $Stream.Write($bytes, 0, $bytes.Length)
        }

        Write-PdfAscii $stream "%PDF-1.4`n"
        $offsets = [System.Collections.Generic.List[long]]::new()
        for ($index = 0; $index -lt $objects.Count; $index++)
        {
            $offsets.Add($stream.Position)
            Write-PdfAscii $stream "$($index + 1) 0 obj`n$($objects[$index])`nendobj`n"
        }

        $xrefOffset = $stream.Position
        Write-PdfAscii $stream "xref`n0 $($objects.Count + 1)`n0000000000 65535 f `n"
        foreach ($offset in $offsets)
        {
            Write-PdfAscii $stream ("{0:D10} 00000 n `n" -f $offset)
        }

        Write-PdfAscii $stream "trailer`n<< /Size $($objects.Count + 1) /Root 1 0 R >>`nstartxref`n$xrefOffset`n%%EOF`n"
        [System.IO.File]::WriteAllBytes($Path, $stream.ToArray())
    }
    finally
    {
        $stream.Dispose()
    }
}

New-SimplePdf (Join-Path $OutputDirectory "sample.pdf")

Get-ChildItem -LiteralPath $OutputDirectory -File |
    Sort-Object Name |
    Select-Object FullName, Length
