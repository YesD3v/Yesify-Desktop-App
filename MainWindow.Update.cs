using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Shapes;
using Yesify.ViewModels;

namespace Yesify;

/// <summary>
/// Update screen (see <see cref="MainViewModel.CheckForUpdatesAsync"/>): like the idle screen, the app fades
/// away and the offer sits over the artwork backdrop: the new version, what's new, and Install / Not now.
/// The title bar stays, so the window can still be moved, minimized or closed. Installing turns the buttons
/// into a progress bar; "Not now" brings the app back with an Update button over Settings.
/// </summary>
public partial class MainWindow
{
    private Storyboard? _updatePulse;
    private bool _updateShown;
    private readonly Dictionary<UIElement, bool> _updateHitTest = [];

    private void InitUpdate()
    {
        Vm.PropertyChanged += OnUpdatePropertyChanged;
        AddHandler(PreviewKeyDownEvent, new KeyEventHandler(Update_KeyDown), true);
        // Found while the window was closed (Yesify in the tray): it's waiting here when the window opens.
        if (Vm.IsUpdateScreenOpen) Loaded += (_, _) => ShowUpdateScreen();
    }

    private void UnhookUpdate() => Vm.PropertyChanged -= OnUpdatePropertyChanged;

    private void OnUpdatePropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        switch (e.PropertyName)
        {
            case nameof(MainViewModel.IsUpdateScreenOpen):
                if (Vm.IsUpdateScreenOpen) ShowUpdateScreen(); else HideUpdateScreen();
                break;
            case nameof(MainViewModel.AvailableUpdate):
                RenderUpdateNotes();
                break;
            case nameof(MainViewModel.UpdateFraction):
            case nameof(MainViewModel.IsUpdating):
                UpdateProgressBar();
                break;
        }
    }

    private void Update_KeyDown(object sender, KeyEventArgs e)
    {
        if (!_updateShown || e.Key != Key.Escape) return;
        e.Handled = true;
        Vm.DismissUpdateCommand.Execute(null);
    }

    /// <summary>For the test harness: lay the screen out at once, without the fade.</summary>
    internal void ShowUpdateScreenNow()
    {
        ShowUpdateScreen();
        foreach (var el in new UIElement[] { UpdateScreen, UpdateContent })
            el.BeginAnimation(OpacityProperty, null);
        UpdateScreen.Opacity = 1;
        UpdateContent.Opacity = 1;
        ((TranslateTransform)UpdateContent.RenderTransform).BeginAnimation(TranslateTransform.YProperty, null);
        foreach (var child in AppLayers()) { child.BeginAnimation(OpacityProperty, null); child.Opacity = 0; }
    }

    private IEnumerable<UIElement> AppLayers() => Root.Children.OfType<UIElement>().Where(c => c != TitleBar);

    private void ShowUpdateScreen()
    {
        if (_updateShown) return;
        _updateShown = true;
        RenderUpdateNotes();
        UpdateProgressBar();
        UpdateNotesScroller.ScrollToTop();
        var ease = new CubicEase { EasingMode = EasingMode.EaseOut };

        // The app steps back (all but the title bar)...
        foreach (var layer in AppLayers())
        {
            if (!_updateHitTest.ContainsKey(layer)) _updateHitTest[layer] = layer.IsHitTestVisible;
            layer.IsHitTestVisible = false;
            layer.BeginAnimation(OpacityProperty, new DoubleAnimation(0, TimeSpan.FromMilliseconds(520)) { EasingFunction = new CubicEase { EasingMode = EasingMode.EaseInOut } });
        }

        // ...the shade comes over the backdrop, and the offer rises into place.
        UpdateScreen.Visibility = Visibility.Visible;
        UpdateScreen.IsHitTestVisible = true;
        UpdateScreen.BeginAnimation(OpacityProperty, new DoubleAnimation(1, TimeSpan.FromMilliseconds(600)) { EasingFunction = ease });
        var rise = TimeSpan.FromMilliseconds(220);
        // Start from below and hidden right away: during the delay a property shows its own (resting) value,
        // which drew the offer in place for a moment before it snapped down to rise (a little bounce).
        var slide = (TranslateTransform)UpdateContent.RenderTransform;
        UpdateContent.BeginAnimation(OpacityProperty, null);
        slide.BeginAnimation(TranslateTransform.YProperty, null);
        UpdateContent.Opacity = 0;
        slide.Y = 26;
        UpdateContent.BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(620)) { BeginTime = rise, EasingFunction = ease });
        ((TranslateTransform)UpdateContent.RenderTransform).BeginAnimation(TranslateTransform.YProperty,
            new DoubleAnimation(26, 0, TimeSpan.FromMilliseconds(900)) { BeginTime = rise, EasingFunction = new QuinticEase { EasingMode = EasingMode.EaseOut } });

        StartUpdatePulse();
        Keyboard.Focus(this);
    }

    private void HideUpdateScreen()
    {
        if (!_updateShown) return;
        _updateShown = false;
        var ease = new CubicEase { EasingMode = EasingMode.EaseInOut };

        UpdateScreen.IsHitTestVisible = false;
        var fade = new DoubleAnimation(0, TimeSpan.FromMilliseconds(360)) { EasingFunction = ease };
        fade.Completed += (_, _) =>
        {
            if (_updateShown) return; // opened again meanwhile
            UpdateScreen.Visibility = Visibility.Collapsed;
            _updatePulse?.Stop(this);
        };
        UpdateScreen.BeginAnimation(OpacityProperty, fade);
        ((TranslateTransform)UpdateContent.RenderTransform).BeginAnimation(TranslateTransform.YProperty,
            new DoubleAnimation(12, TimeSpan.FromMilliseconds(360)) { EasingFunction = ease });

        foreach (var layer in AppLayers())
        {
            var back = new DoubleAnimation(1, TimeSpan.FromMilliseconds(480)) { BeginTime = TimeSpan.FromMilliseconds(120), EasingFunction = ease };
            back.Completed += (_, _) => { if (!_updateShown) layer.BeginAnimation(OpacityProperty, null); };
            layer.BeginAnimation(OpacityProperty, back);
            layer.IsHitTestVisible = !_updateHitTest.TryGetValue(layer, out var was) || was;
        }
        _updateHitTest.Clear();
    }

    /// <summary>A ring breathing out of the badge, only while the screen shows.</summary>
    private void StartUpdatePulse()
    {
        if (_updatePulse == null)
        {
            var duration = new Duration(TimeSpan.FromSeconds(2.6));
            var ease = new CubicEase { EasingMode = EasingMode.EaseOut };
            _updatePulse = new Storyboard { RepeatBehavior = RepeatBehavior.Forever };
            void Add(DoubleAnimation a, string path)
            {
                Storyboard.SetTarget(a, UpdatePulse);
                Storyboard.SetTargetProperty(a, new PropertyPath(path));
                _updatePulse.Children.Add(a);
            }
            Add(new DoubleAnimation(1, 1.5, duration) { EasingFunction = ease }, "(UIElement.RenderTransform).(ScaleTransform.ScaleX)");
            Add(new DoubleAnimation(1, 1.5, duration) { EasingFunction = ease }, "(UIElement.RenderTransform).(ScaleTransform.ScaleY)");
            Add(new DoubleAnimation(0.6, 0, duration), "Opacity");
            Timeline.SetDesiredFrameRate(_updatePulse, 60);
        }
        _updatePulse.Begin(this, isControllable: true);
    }

    // ---------------------------------------------------------------- progress

    private void UpdateProgressBar()
    {
        var fraction = Vm.UpdateFraction;
        var track = Math.Max(UpdateTrack.ActualWidth, UpdateContent.Width);
        var slide = (TranslateTransform)UpdateBar.RenderTransform;
        UpdatePercent.Text = Vm.IsUpdating && fraction is { } p ? $"{p * 100:0}%" : "";

        if (!Vm.IsUpdating)
        {
            slide.BeginAnimation(TranslateTransform.XProperty, null);
            UpdateBar.BeginAnimation(WidthProperty, null);
            UpdateBar.Width = 0;
            return;
        }
        if (fraction is { } f)
        {
            // Filling: the bar eases toward each new value.
            slide.BeginAnimation(TranslateTransform.XProperty, null);
            slide.X = 0;
            UpdateBar.BeginAnimation(WidthProperty, new DoubleAnimation(Math.Max(6, track * Math.Clamp(f, 0, 1)), TimeSpan.FromMilliseconds(220)));
        }
        else
        {
            // No way to tell how far along (building, swapping): a short glint sweeps across instead.
            var w = track * 0.28;
            UpdateBar.BeginAnimation(WidthProperty, null);
            UpdateBar.Width = w;
            if (slide.HasAnimatedProperties) return;
            slide.BeginAnimation(TranslateTransform.XProperty, new DoubleAnimation(-w, track, TimeSpan.FromSeconds(1.5))
            {
                RepeatBehavior = RepeatBehavior.Forever,
                EasingFunction = new SineEase { EasingMode = EasingMode.EaseInOut },
            });
        }
    }

    // ---------------------------------------------------------------- what's new

    /// <summary>The notes are Markdown: headings, bullets and **bold** are drawn; the rest is plain text.</summary>
    private void RenderUpdateNotes()
    {
        UpdateNotes.Children.Clear();
        var notes = Vm.AvailableUpdate?.Notes ?? "";
        var first = true;
        foreach (var raw in notes.Replace("\r", "").Split('\n'))
        {
            var line = raw.Trim();
            if (line.Length == 0) continue;

            if (line.StartsWith('#'))
            {
                var heading = new TextBlock
                {
                    FontSize = 13.5,
                    FontWeight = FontWeights.Bold,
                    Foreground = Brushes.White,
                    TextWrapping = TextWrapping.Wrap,
                    Margin = new Thickness(0, first ? 6 : 16, 0, 4),
                };
                AddInlines(heading, line.TrimStart('#', ' '));
                UpdateNotes.Children.Add(heading);
            }
            else if (line.StartsWith("- ") || line.StartsWith("* "))
            {
                var row = new Grid { Margin = new Thickness(0, 4, 0, 0) };
                row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(16) });
                row.ColumnDefinitions.Add(new ColumnDefinition());
                row.Children.Add(new Ellipse
                {
                    Width = 4.5,
                    Height = 4.5,
                    Fill = new SolidColorBrush(Color.FromArgb(0xB0, 0xFF, 0xFF, 0xFF)),
                    VerticalAlignment = VerticalAlignment.Top,
                    HorizontalAlignment = HorizontalAlignment.Left,
                    Margin = new Thickness(2, 7.5, 0, 0),
                });
                var text = NoteText();
                AddInlines(text, line[2..]);
                Grid.SetColumn(text, 1);
                row.Children.Add(text);
                UpdateNotes.Children.Add(row);
            }
            else
            {
                var text = NoteText();
                text.Margin = new Thickness(0, first ? 6 : 8, 0, 0);
                AddInlines(text, line);
                UpdateNotes.Children.Add(text);
            }
            first = false;
        }
        if (UpdateNotes.Children.Count == 0)
        {
            var none = NoteText();
            none.Margin = new Thickness(0, 6, 0, 0);
            none.Text = "Fixes and improvements.";
            UpdateNotes.Children.Add(none);
        }
    }

    private static TextBlock NoteText() => new()
    {
        FontSize = 13,
        FontWeight = FontWeights.Normal,
        LineHeight = 19,
        Foreground = new SolidColorBrush(Color.FromArgb(0xC8, 0xFF, 0xFF, 0xFF)),
        TextWrapping = TextWrapping.Wrap,
    };

    /// <summary>**bold** as bold, `code` and [links](url) as their text.</summary>
    private static void AddInlines(TextBlock block, string text)
    {
        text = System.Text.RegularExpressions.Regex.Replace(text, @"\[([^\]]+)\]\([^)]*\)", "$1").Replace("`", "");
        var bold = false;
        foreach (var part in text.Split("**"))
        {
            if (part.Length > 0)
            {
                var run = new Run(part.Replace("*", ""));
                if (bold) { run.FontWeight = FontWeights.SemiBold; run.Foreground = Brushes.White; }
                block.Inlines.Add(run);
            }
            bold = !bold;
        }
    }
}
