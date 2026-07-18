using System.Text.Json;
using Microsoft.Web.WebView2.Wpf;
using NoGaReader.Models;

namespace NoGaReader.Services;

internal sealed class ComicReaderController(WebView2 view)
{
    private readonly WebView2 _view = view;

    public async Task ApplySettingsAsync(AppSettings settings)
    {
        var value = new
        {
            display = settings.ComicDisplay switch
            {
                ComicDisplayMode.Double => "double",
                ComicDisplayMode.Continuous => "continuous",
                _ => "single"
            },
            direction = settings.ComicDirection == ComicReadingDirection.RightToLeft ? "rtl" : "ltr",
            fit = settings.ComicFit switch
            {
                ComicFitMode.Width => "width",
                ComicFitMode.Original => "original",
                _ => "height"
            },
            coverSingle = settings.ComicCoverSinglePage,
            scale = Math.Clamp(settings.ComicScale, 0.5, 3.0)
        };
        _ = await ExecuteAsync(
            $"window.__nogareaderComic ? window.__nogareaderComic.applySettings({JsonSerializer.Serialize(value)}) : false");
    }

    public async Task<bool> TurnAsync(int direction)
    {
        direction = Math.Sign(direction);
        var result = await ExecuteAsync(
            $"window.__nogareaderComic ? window.__nogareaderComic.turn({direction}) : false");
        return ParseBoolean(result);
    }

    public async Task<bool> GoToPageAsync(int pageIndex, bool smooth = true)
    {
        pageIndex = Math.Max(0, pageIndex);
        var result = await ExecuteAsync(
            $"window.__nogareaderComic ? window.__nogareaderComic.goToPage({pageIndex}, {smooth.ToString().ToLowerInvariant()}) : false");
        return ParseBoolean(result);
    }

    public async Task<double?> SetScaleAsync(double scale)
    {
        scale = Math.Clamp(scale, 0.5, 3.0);
        var result = await ExecuteAsync(
            $"window.__nogareaderComic ? window.__nogareaderComic.setScale({scale.ToString(System.Globalization.CultureInfo.InvariantCulture)}) : null");
        return ParseDouble(result);
    }

    public async Task RequestLocationAsync()
    {
        _ = await ExecuteAsync(
            "window.__nogareaderComic ? window.__nogareaderComic.postLocation() : null");
    }

    private async Task<string> ExecuteAsync(string script)
    {
        if (_view.CoreWebView2 is null)
        {
            throw new InvalidOperationException("漫画阅读视图尚未初始化。");
        }

        return await _view.CoreWebView2.ExecuteScriptAsync(script);
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

    private static double? ParseDouble(string json)
    {
        try
        {
            using var document = JsonDocument.Parse(json);
            return document.RootElement.TryGetDouble(out var value) ? value : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }
}
