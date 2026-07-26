using Android.Graphics;
using Android.Graphics.Pdf;
using Android.OS;

namespace NoGaReader.Mobile.Platforms.Android;

public sealed class AndroidPdfDocument : IDisposable
{
    private const int MaximumBitmapDimension = 2400;
    private const double MaximumRenderScale = 3d;
    private readonly ParcelFileDescriptor _descriptor;
    private readonly PdfRenderer _renderer;
    // PdfRenderer is not thread safe and renders on a worker thread here, so a
    // gate serializes renders against each other and against Dispose.
    private readonly SemaphoreSlim _renderGate = new(1, 1);
    private bool _disposed;

    public AndroidPdfDocument(string path)
    {
        _descriptor = ParcelFileDescriptor.Open(
            new Java.IO.File(path),
            ParcelFileMode.ReadOnly) ?? throw new IOException("无法打开 PDF 文件。");
        _renderer = new PdfRenderer(_descriptor);
        PageCount = _renderer.PageCount;
    }

    public int PageCount { get; }

    public async Task<ImageSource> RenderPageAsync(int pageIndex, CancellationToken cancellationToken = default)
    {
        await _renderGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            return await Task.Run(() => RenderPage(pageIndex, cancellationToken), cancellationToken)
                .ConfigureAwait(false);
        }
        finally
        {
            _renderGate.Release();
        }
    }

    private ImageSource RenderPage(int pageIndex, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        using var page = _renderer.OpenPage(Math.Clamp(pageIndex, 0, PageCount - 1));
        var scale = Math.Min(
            MaximumRenderScale,
            MaximumBitmapDimension / (double)Math.Max(page.Width, page.Height));
        var width = Math.Max(1, (int)Math.Round(page.Width * scale));
        var height = Math.Max(1, (int)Math.Round(page.Height * scale));
        using var bitmap = Bitmap.CreateBitmap(width, height, Bitmap.Config.Argb8888!);
        bitmap.EraseColor(global::Android.Graphics.Color.White);
        using var transform = new Matrix();
        transform.SetScale((float)scale, (float)scale);
        page.Render(bitmap, null, transform, PdfRenderMode.ForDisplay);

        using var stream = new MemoryStream();
        if (!bitmap.Compress(Bitmap.CompressFormat.Png!, 100, stream))
        {
            throw new IOException("无法渲染 PDF 页面。");
        }

        var bytes = stream.ToArray();
        return ImageSource.FromStream(() => new MemoryStream(bytes, writable: false));
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _renderGate.Wait();
        try
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            _renderer.Dispose();
            _descriptor.Dispose();
        }
        finally
        {
            _renderGate.Release();
        }
    }
}
