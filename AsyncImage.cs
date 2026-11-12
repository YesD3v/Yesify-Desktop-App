using System.IO;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Imaging;
using Yesify.Services;

namespace Yesify;

/// <summary>
/// Loads cover images off the UI thread: downloads (or reads, for local files) the bytes, decodes them at
/// the size they're shown at, and only then hands the finished picture to the Image. WPF's own lazy web
/// loading with a reduced decode size can lay the image out with the wrong dimensions first, which showed
/// up as a zoomed-in top-left corner of the cover.
/// <para>Downloaded covers are kept on disk, so a cover is only ever downloaded once: lists and Home fill in
/// straight away the next time, and without a connection.</para>
/// <para>Use <c>local:AsyncImage.Url</c> on an Image, or <c>local:AsyncImage.BackgroundUrl</c> on a Border
/// (fills it like a cover), with <c>local:AsyncImage.Width</c> as the decode width in pixels.</para>
/// </summary>
public static class AsyncImage
{
    /// <summary>The cover files are trimmed back to this once they pass <see cref="DiskLimitBytes"/>.</summary>
    private const long DiskTargetBytes = 120L * 1024 * 1024;
    private const long DiskLimitBytes = 160L * 1024 * 1024;

    private static readonly string DiskDir = Path.Combine(Storage.LocalDir, "covers");
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(20) };
    private static readonly Dictionary<string, ImageSource> Cache = new();
    private static readonly Dictionary<string, Task<ImageSource?>> Loading = new();
    private static readonly Duration FadeIn = new(TimeSpan.FromMilliseconds(180));
    private static bool _diskReady;

    public static readonly DependencyProperty UrlProperty = DependencyProperty.RegisterAttached(
        "Url", typeof(string), typeof(AsyncImage), new PropertyMetadata(null, OnSourceChanged));

    public static readonly DependencyProperty BackgroundUrlProperty = DependencyProperty.RegisterAttached(
        "BackgroundUrl", typeof(string), typeof(AsyncImage), new PropertyMetadata(null, OnSourceChanged));

    public static readonly DependencyProperty WidthProperty = DependencyProperty.RegisterAttached(
        "Width", typeof(int), typeof(AsyncImage), new PropertyMetadata(300, OnSourceChanged));

    public static string? GetUrl(DependencyObject d) => (string?)d.GetValue(UrlProperty);
    public static void SetUrl(DependencyObject d, string? value) => d.SetValue(UrlProperty, value);
    public static string? GetBackgroundUrl(DependencyObject d) => (string?)d.GetValue(BackgroundUrlProperty);
    public static void SetBackgroundUrl(DependencyObject d, string? value) => d.SetValue(BackgroundUrlProperty, value);
    public static int GetWidth(DependencyObject d) => (int)d.GetValue(WidthProperty);
    public static void SetWidth(DependencyObject d, int value) => d.SetValue(WidthProperty, value);

    /// <summary>Drops every decoded image (called when the window closes). The files on disk stay.</summary>
    public static void ClearCache() => Cache.Clear();

    private static async void OnSourceChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        var url = d is Image ? GetUrl(d) : GetBackgroundUrl(d);
        var width = GetWidth(d);
        if (string.IsNullOrEmpty(url))
        {
            Apply(d, null, fade: false);
            return;
        }

        var key = $"{width}|{url}";
        if (Cache.TryGetValue(key, out var hit))
        {
            Apply(d, hit, fade: false);
            return;
        }

        Apply(d, null, fade: false); // don't show the previous cover while a recycled row loads the new one
        if (!Loading.TryGetValue(key, out var task))
        {
            task = LoadAsync(url, width);
            Loading[key] = task;
        }
        var image = await task;
        Loading.Remove(key);
        if (image != null)
        {
            if (Cache.Count > 800) Cache.Clear();
            Cache[key] = image;
        }
        // Still wanted? (rows get recycled while scrolling)
        var now = d is Image ? GetUrl(d) : GetBackgroundUrl(d);
        if (now == url && GetWidth(d) == width) Apply(d, image, fade: true);
    }

    /// <param name="fade">Ease a cover in when it arrives late, instead of popping (glass mode only).</param>
    private static void Apply(DependencyObject d, ImageSource? image, bool fade)
    {
        fade &= image != null && Frost.IsGlass;
        switch (d)
        {
            case Image img:
                img.Source = image;
                img.BeginAnimation(UIElement.OpacityProperty, fade ? new DoubleAnimation(0, 1, FadeIn) : null);
                break;
            case Border border:
                if (image == null)
                {
                    border.Background = null;
                    break;
                }
                var brush = new ImageBrush(image) { Stretch = Stretch.UniformToFill };
                border.Background = brush;
                if (fade) brush.BeginAnimation(Brush.OpacityProperty, new DoubleAnimation(0, 1, FadeIn));
                break;
        }
    }

    private static async Task<ImageSource?> LoadAsync(string url, int width)
    {
        try
        {
            return await Task.Run(async () =>
            {
                var bytes = url.StartsWith("http", StringComparison.OrdinalIgnoreCase)
                    ? await DownloadAsync(url)
                    : await File.ReadAllBytesAsync(url);
                var img = new BitmapImage();
                img.BeginInit();
                img.StreamSource = new MemoryStream(bytes);
                img.DecodePixelWidth = width;
                img.CacheOption = BitmapCacheOption.OnLoad;
                img.EndInit();
                img.Freeze();
                return (ImageSource)img;
            });
        }
        catch
        {
            return null;
        }
    }

    /// <summary>The image's bytes: from disk if it was downloaded before, otherwise from the web (and saved).</summary>
    private static async Task<byte[]> DownloadAsync(string url)
    {
        var file = Path.Combine(DiskDir, Convert.ToHexString(SHA1.HashData(Encoding.UTF8.GetBytes(url))) + ".img");
        try
        {
            if (File.Exists(file))
            {
                var saved = await File.ReadAllBytesAsync(file);
                if (saved.Length > 0) return saved;
            }
        }
        catch { /* unreadable: download it again */ }

        var bytes = await Http.GetByteArrayAsync(url);
        try
        {
            PrepareDisk();
            var tmp = $"{file}.{Environment.CurrentManagedThreadId}.tmp";
            await File.WriteAllBytesAsync(tmp, bytes);
            File.Move(tmp, file, overwrite: true);
        }
        catch { /* the cache is a convenience */ }
        return bytes;
    }

    /// <summary>Once per run, before the first save: make the folder and delete the oldest covers if it has grown too big.</summary>
    private static void PrepareDisk()
    {
        if (_diskReady) return;
        lock (Loading)
        {
            if (_diskReady) return;
            _diskReady = true;
        }
        Directory.CreateDirectory(DiskDir);
        try
        {
            var files = new DirectoryInfo(DiskDir).GetFiles();
            var total = files.Sum(f => f.Length);
            if (total <= DiskLimitBytes) return;
            foreach (var f in files.OrderBy(f => f.LastWriteTimeUtc))
            {
                if (total <= DiskTargetBytes) break;
                try
                {
                    total -= f.Length;
                    f.Delete();
                }
                catch { /* in use */ }
            }
        }
        catch { /* trimming is best effort */ }
    }
}
