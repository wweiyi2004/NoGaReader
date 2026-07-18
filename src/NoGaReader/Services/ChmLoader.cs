using System.Diagnostics;
using System.Text;
using System.Text.RegularExpressions;
using NoGaReader.Models;

namespace NoGaReader.Services;

internal sealed class ChmLoader
{
    private const string CacheCategory = "chm";
    private const string CacheVersion = "chm-v1";

    public ReaderSession Load(string sourcePath, CancellationToken cancellationToken)
    {
        var cacheDirectory = AppPaths.GetDocumentCacheDirectory(sourcePath, CacheCategory);
        var completeMarker = Path.Combine(cacheDirectory, ".complete");
        var identity = CreateIdentity(sourcePath);
        var marker = $"{CacheVersion}|{identity}";
        var contentRoot = Path.Combine(cacheDirectory, "content");

        var ready = Directory.Exists(contentRoot) &&
                    File.Exists(completeMarker) &&
                    string.Equals(File.ReadAllText(completeMarker).Trim(), marker, StringComparison.Ordinal);

        if (!ready)
        {
            Reset(cacheDirectory);
            Directory.CreateDirectory(contentRoot);
            cancellationToken.ThrowIfCancellationRequested();
            DecompileChm(sourcePath, contentRoot, cancellationToken);
            File.WriteAllText(completeMarker, marker, Encoding.UTF8);
        }

        var htmlFiles = Directory.EnumerateFiles(contentRoot, "*.*", SearchOption.AllDirectories)
            .Where(path =>
            {
                var ext = Path.GetExtension(path);
                return ext.Equals(".htm", StringComparison.OrdinalIgnoreCase) ||
                       ext.Equals(".html", StringComparison.OrdinalIgnoreCase) ||
                       ext.Equals(".xhtml", StringComparison.OrdinalIgnoreCase);
            })
            .OrderBy(path => path, StringComparer.OrdinalIgnoreCase)
            .ToList();

        if (htmlFiles.Count == 0)
        {
            throw new InvalidDataException("CHM 解包后没有可显示的 HTML 页面。");
        }

        var sections = htmlFiles
            .Select((path, index) =>
            {
                var relative = Path.GetRelativePath(contentRoot, path);
                var title = Path.GetFileNameWithoutExtension(path);
                if (string.IsNullOrWhiteSpace(title))
                {
                    title = $"页面 {index + 1}";
                }

                return new ReaderSection(title, path);
            })
            .ToList();

        var toc = BuildTocFromHhc(contentRoot, sections) ??
                  sections.Select(section => new TocNode
                  {
                      Title = section.Title,
                      FullPath = section.FullPath
                  }).ToList();

        return new ReaderSession
        {
            SourcePath = sourcePath,
            Title = Path.GetFileNameWithoutExtension(sourcePath),
            RootDirectory = contentRoot,
            Kind = ReaderDocumentKind.Html,
            Sections = sections,
            TableOfContents = toc,
            IsReflowable = true,
            SupportsInPageSearch = false,
            EnableScriptExecution = false
        };
    }

    private static void DecompileChm(string sourcePath, string outputDirectory, CancellationToken cancellationToken)
    {
        var hh = ResolveHhPath();
        if (hh is null)
        {
            throw new InvalidOperationException(
                "无法解包 CHM：未找到 Windows 帮助编译器 hh.exe。");
        }

        using var process = new Process
        {
            StartInfo = new ProcessStartInfo
            {
                FileName = hh,
                ArgumentList = { "-decompile", outputDirectory, sourcePath },
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            }
        };
        process.Start();
        process.WaitForExit(120_000);
        cancellationToken.ThrowIfCancellationRequested();
        if (!Directory.EnumerateFiles(outputDirectory, "*.*", SearchOption.AllDirectories).Any())
        {
            var error = process.StandardError.ReadToEnd();
            throw new InvalidDataException(
                string.IsNullOrWhiteSpace(error)
                    ? "CHM 解包失败，未生成内容。"
                    : $"CHM 解包失败：{error.Trim()}");
        }
    }

    private static string? ResolveHhPath()
    {
        var system = Environment.GetFolderPath(Environment.SpecialFolder.System);
        var candidate = Path.Combine(system, "hh.exe");
        if (File.Exists(candidate))
        {
            return candidate;
        }

        candidate = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "hh.exe");
        return File.Exists(candidate) ? candidate : null;
    }

    private static IReadOnlyList<TocNode>? BuildTocFromHhc(
        string contentRoot,
        IReadOnlyList<ReaderSection> sections)
    {
        var hhc = Directory.EnumerateFiles(contentRoot, "*.hhc", SearchOption.AllDirectories).FirstOrDefault();
        if (hhc is null || !File.Exists(hhc))
        {
            return null;
        }

        string text;
        try
        {
            text = File.ReadAllText(hhc, Encoding.Default);
        }
        catch
        {
            return null;
        }

        var nodes = new List<TocNode>();
        var matches = Regex.Matches(
            text,
            @"<param\s+name\s*=\s*""Name""\s+value\s*=\s*""(?<name>[^""]*)""\s*>\s*<param\s+name\s*=\s*""Local""\s+value\s*=\s*""(?<local>[^""]*)""\s*>",
            RegexOptions.IgnoreCase | RegexOptions.Singleline);
        var byFile = sections.ToDictionary(
            section => Path.GetFullPath(section.FullPath),
            section => section,
            StringComparer.OrdinalIgnoreCase);

        foreach (Match match in matches)
        {
            var name = match.Groups["name"].Value.Trim();
            var local = match.Groups["local"].Value.Trim().Replace('/', Path.DirectorySeparatorChar);
            if (string.IsNullOrWhiteSpace(local))
            {
                continue;
            }

            var fragment = (string?)null;
            var hashIndex = local.IndexOf('#');
            if (hashIndex >= 0)
            {
                fragment = local[(hashIndex + 1)..];
                local = local[..hashIndex];
            }

            var full = Path.GetFullPath(Path.Combine(Path.GetDirectoryName(hhc)!, local));
            if (!byFile.ContainsKey(full))
            {
                // try content root relative
                full = Path.GetFullPath(Path.Combine(contentRoot, local));
            }

            if (!byFile.ContainsKey(full))
            {
                continue;
            }

            nodes.Add(new TocNode
            {
                Title = string.IsNullOrWhiteSpace(name) ? Path.GetFileNameWithoutExtension(local) : name,
                FullPath = full,
                Fragment = fragment
            });
        }

        return nodes.Count == 0 ? null : nodes;
    }

    private static string CreateIdentity(string sourcePath)
    {
        var file = new FileInfo(sourcePath);
        return $"{Path.GetFullPath(sourcePath)}|{file.Length}|{file.LastWriteTimeUtc.Ticks}";
    }

    private static void Reset(string directory)
    {
        if (Directory.Exists(directory))
        {
            try { Directory.Delete(directory, true); } catch { /* best effort */ }
        }

        Directory.CreateDirectory(directory);
    }
}
