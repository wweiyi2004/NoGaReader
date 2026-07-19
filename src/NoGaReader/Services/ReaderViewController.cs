using System.Text.Json;
using Microsoft.Web.WebView2.Wpf;
using NoGaReader.Models;

namespace NoGaReader.Services;

internal sealed record WebReaderLocation(
    double Progress,
    int Page,
    int PageCount,
    bool IsPaged,
    TextAnchor? Anchor);

internal sealed record PageTurnResult(bool Moved, string? Boundary, WebReaderLocation? Location);

internal sealed class ReaderViewController(WebView2 view)
{
    private readonly WebView2 _view = view;

    public async Task ApplyAsync(
        AppSettings settings,
        double restoreProgress,
        string annotationsJson = "[]",
        TextAnchor? restoreAnchor = null)
    {
        EnsureValidJsonArray(annotationsJson);
        _ = await ExecuteAsync(ReaderRuntime.BuildBootstrapScript(
            settings,
            restoreProgress,
            annotationsJson,
            JsonSerializer.Serialize(restoreAnchor)));
    }

    public async Task<WebReaderLocation?> GetLocationAsync()
    {
        var result = await ExecuteAsync(
            "window.__nogareader ? window.__nogareader.preciseLocation() : null");
        return ParseLocation(result);
    }

    public async Task<PageTurnResult> TurnPageAsync(int direction)
    {
        direction = Math.Sign(direction);
        var result = await ExecuteAsync(
            $"window.__nogareader ? window.__nogareader.turnPage({direction}) : null");
        try
        {
            using var document = JsonDocument.Parse(result);
            var root = UnwrapJsonString(document.RootElement);
            if (root.ValueKind != JsonValueKind.Object)
            {
                return new PageTurnResult(false, null, null);
            }

            var moved = root.TryGetProperty("moved", out var movedElement) &&
                        movedElement.ValueKind == JsonValueKind.True;
            var boundary = root.TryGetProperty("boundary", out var boundaryElement)
                ? boundaryElement.GetString()
                : null;
            return new PageTurnResult(moved, boundary, ReadLocation(root));
        }
        catch (JsonException)
        {
            return new PageTurnResult(false, null, null);
        }
    }

    public async Task<int> ApplyAnnotationsAsync(string annotationsJson)
    {
        EnsureValidJsonArray(annotationsJson);
        var result = await ExecuteAsync(
            $"window.__nogareader ? window.__nogareader.applyAnnotations({annotationsJson}) : 0");
        return ParseInteger(result);
    }

    public async Task<bool> GoToAnnotationAsync(string annotationId)
    {
        var result = await ExecuteAsync(
            $"window.__nogareader ? window.__nogareader.goToAnnotation({JsonSerializer.Serialize(annotationId)}) : false");
        return ParseBoolean(result);
    }

    public async Task<bool> RevealTextAsync(string query, int occurrenceIndex = 0)
    {
        occurrenceIndex = Math.Clamp(occurrenceIndex, 0, 10_000);
        var result = await ExecuteAsync(
            $"window.__nogareader ? window.__nogareader.revealText({JsonSerializer.Serialize(query)}, {occurrenceIndex}) : false");
        return ParseBoolean(result);
    }

    public async Task<bool> GoToFragmentAsync(string fragment)
    {
        var result = await ExecuteAsync(
            "(() => { " +
            $"const id = {JsonSerializer.Serialize(fragment)}; " +
            "const target = document.getElementById(id) || document.querySelector(`[name=\"${CSS.escape(id)}\"]`); " +
            "if (!target) return false; target.scrollIntoView({ block: 'start', inline: 'start' }); " +
            "if (window.__nogareader) window.setTimeout(window.__nogareader.postLocation, 80); return true; })()");
        return ParseBoolean(result);
    }

    public async Task ClearSelectionAsync()
    {
        _ = await ExecuteAsync(
            "window.__nogareader ? window.__nogareader.clearSelection() : null");
    }

    public async Task RestoreProgressAsync(double progress)
    {
        progress = Math.Clamp(progress, 0, 1);
        _ = await ExecuteAsync(
            $"window.__nogareader ? window.__nogareader.restore({progress.ToString(System.Globalization.CultureInfo.InvariantCulture)}) : null");
    }

    public async Task ApplyThemeColorsAsync(
        string background,
        string foreground,
        string muted,
        string link,
        string rule,
        bool dark)
    {
        var bg = JsonSerializer.Serialize(background);
        var fg = JsonSerializer.Serialize(foreground);
        var mu = JsonSerializer.Serialize(muted);
        var li = JsonSerializer.Serialize(link);
        var ru = JsonSerializer.Serialize(rule);
        var darkJs = dark ? "true" : "false";
        var script =
            "(function(){" +
            "var bg=" + bg + ",fg=" + fg + ",muted=" + mu + ",link=" + li + ",rule=" + ru + ",dark=" + darkJs + ";" +
            "var root=document.documentElement, body=document.body;" +
            "if(!root||!body) return 'no-dom';" +
            "root.style.colorScheme = dark ? 'dark' : 'light';" +
            "var force=document.getElementById('nogareader-theme-force');" +
            "if(!force){ force=document.createElement('style'); force.id='nogareader-theme-force';" +
            " (document.head||root).appendChild(force); }" +
            "force.textContent=" +
            "'html,body,body.nogar-reader{background:'+bg+' !important;background-color:'+bg+' !important;color:'+fg+' !important;}' +" +
            "':root{--nr-background:'+bg+' !important;--nr-foreground:'+fg+' !important;--nr-muted:'+muted+' !important;--nr-link:'+link+' !important;--nr-rule:'+rule+' !important;color-scheme:'+(dark?'dark':'light')+';}' +" +
            "'body.nogar-reader a{color:'+link+' !important;}body.nogar-reader blockquote{color:'+muted+' !important;border-color:'+rule+' !important;}body.nogar-reader hr{border-color:'+rule+' !important;}';" +
            "var style=document.getElementById('nogareader-runtime-style');" +
            "if(style){ var css=style.textContent||'';" +
            " function setVar(name,val){ var re=new RegExp(name+':\\\\s*[^;]+;','g');" +
            "  if(re.test(css)) css=css.replace(re, name+': '+val+';');" +
            "  else if(css.indexOf(':root')>=0) css=css.replace('{', '{'+name+': '+val+';'); }" +
            " setVar('--nr-background',bg); setVar('--nr-foreground',fg); setVar('--nr-muted',muted); setVar('--nr-link',link); setVar('--nr-rule',rule);" +
            " style.textContent=css; }" +
            "return 'ok';" +
            "})()";
        _ = await ExecuteAsync(script);
    }

    private async Task<string> ExecuteAsync(string script)
    {
        if (_view.CoreWebView2 is null)
        {
            throw new InvalidOperationException("阅读视图尚未初始化。");
        }

        return await _view.CoreWebView2.ExecuteScriptAsync(script);
    }

    private static WebReaderLocation? ParseLocation(string json)
    {
        try
        {
            using var document = JsonDocument.Parse(json);
            var root = UnwrapJsonString(document.RootElement);
            return ReadLocation(root);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static WebReaderLocation? ReadLocation(JsonElement root)
    {
        if (root.ValueKind != JsonValueKind.Object ||
            !root.TryGetProperty("progress", out var progressElement) ||
            !progressElement.TryGetDouble(out var progress))
        {
            return null;
        }

        var page = root.TryGetProperty("page", out var pageElement) && pageElement.TryGetInt32(out var pageValue)
            ? Math.Max(0, pageValue)
            : 0;
        var pageCount = root.TryGetProperty("pageCount", out var countElement) && countElement.TryGetInt32(out var countValue)
            ? Math.Max(0, countValue)
            : 0;
        var paged = root.TryGetProperty("paged", out var pagedElement) && pagedElement.ValueKind == JsonValueKind.True;
        var anchor = root.TryGetProperty("anchor", out var anchorElement)
            ? ParseAnchor(anchorElement)
            : null;
        return new WebReaderLocation(Math.Clamp(progress, 0, 1), page, pageCount, paged, anchor);
    }

    internal static TextAnchor? ParseAnchor(JsonElement root)
    {
        if (root.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        static string? StringProperty(JsonElement element, string name, int maximumLength)
        {
            if (!element.TryGetProperty(name, out var property) || property.ValueKind != JsonValueKind.String)
            {
                return null;
            }

            var value = property.GetString();
            return value is not null && value.Length <= maximumLength ? value : null;
        }

        var startPath = StringProperty(root, "startPath", 1024);
        var endPath = StringProperty(root, "endPath", 1024);
        var exactText = StringProperty(root, "exactText", 4096);
        if (startPath is null || endPath is null || string.IsNullOrWhiteSpace(exactText))
        {
            return null;
        }

        var startOffset = root.TryGetProperty("startOffset", out var startElement) &&
                          startElement.TryGetInt32(out var start)
            ? Math.Clamp(start, 0, 1_000_000)
            : 0;
        var endOffset = root.TryGetProperty("endOffset", out var endElement) &&
                        endElement.TryGetInt32(out var end)
            ? Math.Clamp(end, 0, 1_000_000)
            : 0;
        var progress = root.TryGetProperty("progress", out var progressElement) &&
                       progressElement.TryGetDouble(out var progressValue)
            ? Math.Clamp(progressValue, 0, 1)
            : 0;

        return new TextAnchor
        {
            StartPath = startPath,
            StartOffset = startOffset,
            EndPath = endPath,
            EndOffset = endOffset,
            ExactText = exactText,
            Prefix = StringProperty(root, "prefix", 256),
            Suffix = StringProperty(root, "suffix", 256),
            Progress = progress
        };
    }

    private static JsonElement UnwrapJsonString(JsonElement root)
    {
        if (root.ValueKind != JsonValueKind.String || root.GetString() is not { } nested)
        {
            return root;
        }

        using var nestedDocument = JsonDocument.Parse(nested);
        return nestedDocument.RootElement.Clone();
    }

    private static bool ParseBoolean(string json)
    {
        try
        {
            using var document = JsonDocument.Parse(json);
            return document.RootElement.ValueKind == JsonValueKind.True;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    private static int ParseInteger(string json)
    {
        try
        {
            using var document = JsonDocument.Parse(json);
            return document.RootElement.TryGetInt32(out var value) ? value : 0;
        }
        catch (JsonException)
        {
            return 0;
        }
    }

    private static void EnsureValidJsonArray(string json)
    {
        using var document = JsonDocument.Parse(json);
        if (document.RootElement.ValueKind != JsonValueKind.Array)
        {
            throw new ArgumentException("批注数据必须是 JSON 数组。", nameof(json));
        }
    }
}
