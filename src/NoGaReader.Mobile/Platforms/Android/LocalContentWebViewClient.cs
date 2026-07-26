using Android.Webkit;
using Microsoft.Maui.Handlers;
using Microsoft.Maui.Platform;

namespace NoGaReader.Mobile.Platforms.Android;

/// <summary>
/// Keeps reader content offline, matching the desktop client's guarantee:
/// only local file resources (and inline data URIs) ever load inside the
/// reader WebView. Every external fetch — remote images, stylesheets,
/// fonts — is answered locally without touching the network. Full page
/// navigations are additionally filtered by the page's Navigating handler.
/// </summary>
internal sealed class LocalContentWebViewClient(WebViewHandler handler) : MauiWebViewClient(handler)
{
    public override WebResourceResponse? ShouldInterceptRequest(
        global::Android.Webkit.WebView? view,
        IWebResourceRequest? request)
    {
        var scheme = request?.Url?.Scheme;
        if (scheme is not null &&
            (scheme.Equals("file", StringComparison.OrdinalIgnoreCase) ||
             scheme.Equals("data", StringComparison.OrdinalIgnoreCase) ||
             scheme.Equals("about", StringComparison.OrdinalIgnoreCase)))
        {
            return base.ShouldInterceptRequest(view, request);
        }

        return new WebResourceResponse(
            "text/plain",
            "utf-8",
            403,
            "Blocked",
            null,
            Stream.Null);
    }
}
