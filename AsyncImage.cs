using System.IO;
using System.Net.Http;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace Yesify;

/// <summary>
/// Loads cover images off the UI thread: downloads (or reads, for local files) the bytes, decodes them at
/// the size they're shown at, and only then hands the finished picture to the Image. WPF's own lazy web
/// loading with a reduced decode size can lay the image out with the wrong dimensions first, which showed
/// up as a zoomed-in top-left corner of the cover.
/// <para>Use <c>local:AsyncImage.Url</c> on an Image, or <c>local:AsyncImage.BackgroundUrl</c> on a Border
/// (fills it like a cover), with <c>local:AsyncImage.Width</c> as the decode width in pixels.</para>
/// </summary>
public static class AsyncImage
{
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(20) };
    private static readonly Dictionary<string, ImageSource> Cache = new();
    private static readonly Dictionary<string, Task<ImageSource?>> Loading = new();

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

    /// <summary>Drops every decoded image (called when the window closes).</summary>
    public static void ClearCache() => Cache.Clear();

    private static async void OnSourceChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        var url = d is Image ? GetUrl(d) : GetBackgroundUrl(d);
        var width = GetWidth(d);
        if (string.IsNullOrEmpty(url))
        {
            Apply(d, null);
            return;
        }

        var key = $"{width}|{url}";
        if (Cache.TryGetValue(key, out var hit))
        {
            Apply(d, hit);
            return;
        }

        Apply(d, null); // don't show the previous cover while a recycled row loads the new one
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
        if (now == url && GetWidth(d) == width) Apply(d, image);
    }

    private static void Apply(DependencyObject d, ImageSource? image)
    {
        switch (d)
        {
            case Image img:
                img.Source = image;
                break;
            case Border border:
                border.Background = image == null ? null : new ImageBrush(image) { Stretch = Stretch.UniformToFill };
                break;
        }
    }

    private static async Task<ImageSource?> LoadAsync(string url, int width)
    {
        try
        {
            var bytes = url.StartsWith("http", StringComparison.OrdinalIgnoreCase)
                ? await Http.GetByteArrayAsync(url)
                : await File.ReadAllBytesAsync(url);
            return await Task.Run(() =>
            {
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
}
