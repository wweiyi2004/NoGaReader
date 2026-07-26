using Android.Graphics;

namespace NoGaReader.Mobile.Platforms.Android;

/// <summary>
/// Decodes large page images with a bounded dimension so oversized comic pages
/// cannot exhaust memory: an unbounded 4000×6000 page costs ~96 MB as a raw
/// bitmap, while the sampled decode stays around 23 MB worst case. Small
/// images keep the plain file source (which also preserves GIF animation).
/// </summary>
internal static class DownsampledImageLoader
{
    private const int MaximumDimension = 2400;

    public static ImageSource Load(string path)
    {
        try
        {
            var bounds = new BitmapFactory.Options { InJustDecodeBounds = true };
            BitmapFactory.DecodeFile(path, bounds);
            if (bounds.OutWidth <= 0 || bounds.OutHeight <= 0)
            {
                return ImageSource.FromFile(path);
            }

            var sampleSize = 1;
            while (Math.Max(bounds.OutWidth, bounds.OutHeight) / sampleSize > MaximumDimension)
            {
                sampleSize *= 2;
            }

            if (sampleSize == 1)
            {
                return ImageSource.FromFile(path);
            }

            var options = new BitmapFactory.Options { InSampleSize = sampleSize };
            using var bitmap = BitmapFactory.DecodeFile(path, options);
            if (bitmap is null)
            {
                return ImageSource.FromFile(path);
            }

            using var buffer = new MemoryStream();
            // JPEG drops transparency, which page-sized artwork does not rely
            // on; it keeps the re-encoded page an order of magnitude smaller.
            if (!bitmap.Compress(Bitmap.CompressFormat.Jpeg!, 88, buffer))
            {
                return ImageSource.FromFile(path);
            }

            var bytes = buffer.ToArray();
            return ImageSource.FromStream(() => new MemoryStream(bytes, writable: false));
        }
        catch (Exception)
        {
            return ImageSource.FromFile(path);
        }
    }
}
