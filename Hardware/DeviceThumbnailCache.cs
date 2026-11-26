using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace VisualControlsBinder.Hardware;

internal sealed class DeviceThumbnailCache
{
    private readonly Dictionary<string, BitmapSource> _thumbnails = new(StringComparer.Ordinal);

    public BitmapSource Get(string path)
    {
        if (_thumbnails.TryGetValue(path, out var thumbnail))
            return thumbnail;

        var image = new BitmapImage();
        image.BeginInit();
        image.UriSource = new Uri(HardwareTemplates.AssetPath(path), UriKind.Absolute);
        image.DecodePixelWidth = 352;
        image.CacheOption = BitmapCacheOption.OnLoad;
        image.EndInit();

        thumbnail = TrimTransparency(PhotoBackdrop.Remove(image, path));
        _thumbnails.Add(path, thumbnail);
        return thumbnail;
    }

    private static BitmapSource TrimTransparency(BitmapSource source)
    {
        var bitmap = new FormatConvertedBitmap(source, PixelFormats.Bgra32, null, 0);
        int width = bitmap.PixelWidth;
        int height = bitmap.PixelHeight;
        int stride = width * 4;
        var pixels = new byte[stride * height];
        bitmap.CopyPixels(pixels, stride, 0);
        int left = width;
        int top = height;
        int right = -1;
        int bottom = -1;
        for (int y = 0; y < height; y++)
        for (int x = 0; x < width; x++)
        {
            if (pixels[y * stride + x * 4 + 3] < 32)
                continue;
            left = Math.Min(left, x);
            top = Math.Min(top, y);
            right = Math.Max(right, x);
            bottom = y;
        }
        if (right < left)
            return source;

        const int padding = 3;
        left = Math.Max(0, left - padding);
        top = Math.Max(0, top - padding);
        right = Math.Min(width - 1, right + padding);
        bottom = Math.Min(height - 1, bottom + padding);
        var trimmed = new CroppedBitmap(source, new Int32Rect(left, top, right - left + 1, bottom - top + 1));
        trimmed.Freeze();
        return trimmed;
    }
}
