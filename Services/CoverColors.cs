using System.IO;
using System.Net.Http;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace Yesify.Services;

/// <summary>
/// Two background colors from an album cover (top-left and bottom-right halves), favoring vivid pixels
/// and kept dark enough for white text.
/// </summary>
public static class CoverColors
{
    public static readonly (Color A, Color B) Fallback = (Color.FromRgb(0x26, 0x26, 0x2C), Color.FromRgb(0x14, 0x14, 0x18));

    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(10) };
    private static readonly Dictionary<string, (Color, Color)> Cache = new();
    private static readonly Dictionary<string, Backdrop> BackdropCache = new();

    /// <summary>For the glass background: a tiny decode of the artwork (stretched, it reads as a blur) plus vivid colors.</summary>
    public sealed record Backdrop(ImageSource? Image, Color A, Color B, Color C);

    public static async Task<Backdrop> GetBackdropAsync(string? url)
    {
        if (string.IsNullOrEmpty(url)) return new Backdrop(null, Fallback.A, Fallback.B, Fallback.B);
        lock (BackdropCache)
            if (BackdropCache.TryGetValue(url, out var hit)) return hit;

        try
        {
            var bytes = await Http.GetByteArrayAsync(url);
            var result = await Task.Run(() =>
            {
                var (pixels, w, h) = Decode(bytes, 48);
                var (a, b, all) = Average(pixels, w, h);
                return new Backdrop(DecodeImage(bytes, 64), Tune(a, vivid: true), Tune(b, vivid: true), Tune(all, vivid: true));
            });
            lock (BackdropCache)
            {
                if (BackdropCache.Count > 40) BackdropCache.Clear();
                BackdropCache[url] = result;
            }
            return result;
        }
        catch
        {
            return new Backdrop(null, Fallback.A, Fallback.B, Fallback.B);
        }
    }

    public static async Task<(Color A, Color B)> GetAsync(string? url)
    {
        if (string.IsNullOrEmpty(url)) return Fallback;
        lock (Cache)
            if (Cache.TryGetValue(url, out var hit)) return hit;

        try
        {
            var bytes = await Http.GetByteArrayAsync(url);
            var colors = await Task.Run(() => Extract(bytes));
            lock (Cache)
            {
                if (Cache.Count > 200) Cache.Clear();
                Cache[url] = colors;
            }
            return colors;
        }
        catch
        {
            return Fallback;
        }
    }

    private static (Color, Color) Extract(byte[] bytes)
    {
        var (px, w, h) = Decode(bytes, 24);
        var (a, b, _) = Average(px, w, h);
        return (Tune(a, vivid: false), Tune(b, vivid: false));
    }

    private static BitmapImage DecodeImage(byte[] bytes, int width)
    {
        var img = new BitmapImage();
        img.BeginInit();
        img.StreamSource = new MemoryStream(bytes);
        img.DecodePixelWidth = width;
        img.CacheOption = BitmapCacheOption.OnLoad;
        img.EndInit();
        img.Freeze(); // usable from the UI thread
        return img;
    }

    private static (byte[] Pixels, int W, int H) Decode(byte[] bytes, int width)
    {
        var bgra = new FormatConvertedBitmap(DecodeImage(bytes, width), PixelFormats.Bgra32, null, 0);
        int w = bgra.PixelWidth, h = bgra.PixelHeight;
        var px = new byte[w * h * 4];
        bgra.CopyPixels(px, w * 4, 0);
        return (px, w, h);
    }

    /// <summary>Weighted average color of the top-left half, the bottom-right half, and the whole image.</summary>
    private static (double[] A, double[] B, double[] All) Average(byte[] px, int w, int h)
    {
        double[] a = new double[4], b = new double[4], all = new double[4]; // r, g, b, weight
        for (var y = 0; y < h; y++)
        for (var x = 0; x < w; x++)
        {
            var i = (y * w + x) * 4;
            double bl = px[i], g = px[i + 1], r = px[i + 2];
            var max = Math.Max(r, Math.Max(g, bl));
            var min = Math.Min(r, Math.Min(g, bl));
            var sat = max == 0 ? 0 : (max - min) / max;
            var weight = 0.15 + sat * sat; // vivid pixels count more than greys
            var acc = x * h + y * w < w * h ? a : b; // split along the diagonal
            acc[0] += r * weight; acc[1] += g * weight; acc[2] += bl * weight; acc[3] += weight;
            all[0] += r * weight; all[1] += g * weight; all[2] += bl * weight; all[3] += weight;
        }
        return (a, b, all);
    }

    // Average color, nudged more saturated and clamped to a lightness that keeps white text readable.
    private static Color Tune(double[] acc, bool vivid)
    {
        if (acc[3] <= 0) return Fallback.A;
        double r = acc[0] / acc[3] / 255, g = acc[1] / acc[3] / 255, b = acc[2] / acc[3] / 255;
        RgbToHsl(r, g, b, out var hue, out var s, out var l);
        s = Math.Min(1, s * (vivid ? 1.45 : 1.25));
        l = vivid ? Math.Clamp(l, 0.26, 0.48) : Math.Clamp(l, 0.16, 0.38);
        HslToRgb(hue, s, l, out r, out g, out b);
        return Color.FromRgb((byte)(r * 255), (byte)(g * 255), (byte)(b * 255));
    }

    private static void RgbToHsl(double r, double g, double b, out double h, out double s, out double l)
    {
        var max = Math.Max(r, Math.Max(g, b));
        var min = Math.Min(r, Math.Min(g, b));
        l = (max + min) / 2;
        if (max == min) { h = s = 0; return; }
        var d = max - min;
        s = l > 0.5 ? d / (2 - max - min) : d / (max + min);
        h = max == r ? (g - b) / d + (g < b ? 6 : 0) : max == g ? (b - r) / d + 2 : (r - g) / d + 4;
        h /= 6;
    }

    private static void HslToRgb(double h, double s, double l, out double r, out double g, out double b)
    {
        if (s == 0) { r = g = b = l; return; }
        var q = l < 0.5 ? l * (1 + s) : l + s - l * s;
        var p = 2 * l - q;
        r = Hue(p, q, h + 1.0 / 3);
        g = Hue(p, q, h);
        b = Hue(p, q, h - 1.0 / 3);
    }

    private static double Hue(double p, double q, double t)
    {
        if (t < 0) t += 1;
        if (t > 1) t -= 1;
        if (t < 1.0 / 6) return p + (q - p) * 6 * t;
        if (t < 1.0 / 2) return q;
        if (t < 2.0 / 3) return p + (q - p) * (2.0 / 3 - t) * 6;
        return p;
    }
}
