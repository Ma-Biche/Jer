using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Globalization;
using System.Linq;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Shapes;
using System.Windows.Threading;
using Jer.Core;
using Jer.Interop;
using Jer.Models;
using Jer.Services;
using Jer.ViewModels;

namespace Jer.Views;

/// <summary>
/// The notch. A fixed-size, transparent, always-on-top window at the top centre of the chosen display.
/// Only the black shape is drawn; every transparent pixel lets clicks through to the apps below.
/// </summary>
public partial class NotchWindow : Window
{
    private const double BaseWidth = 860;
    private const double BaseHeight = 260;
    private const double CollapsedHeight = 32;
    private const double MinimalHeight = 6;
    private const double MinimalWidth = 180;
    private const double ClockWidth = 196;
    private const double MusicWidth = 264;
    private const double TimerWidth = 236;
    private const double ExpandedWidth = 560;
    private const double ExpandedLyricsWidth = 830;
    private const double ExpandedHeight = 206;
    private const double HudWidth = 330;
    private const double HudHeight = 42;
    private const double AlertWidth = 460;

    private readonly NotchViewModel _vm;
    private readonly AppSettings _settings;
    private readonly MonitorService _monitors;
    private readonly Action _openSettings;
    private readonly RectangleGeometry _bodyClip = new();
    private readonly Dictionary<UIElement, int> _fadeGeneration = new();
    private readonly Random _random = new();

    private readonly DispatcherTimer _hoverTimer = new() { Interval = TimeSpan.FromMilliseconds(110) };
    private readonly DispatcherTimer _leaveTimer = new() { Interval = TimeSpan.FromMilliseconds(320) };
    private readonly DispatcherTimer _dragLeaveTimer = new() { Interval = TimeSpan.FromMilliseconds(160) };
    private readonly DispatcherTimer _blinkTimer = new();
    private readonly DispatcherTimer _lookTimer = new();
    private readonly Debouncer _displayDebounce = new(TimeSpan.FromMilliseconds(600));

    private IntPtr _hwnd;
    private DisplayInfo? _display;
    private Mode _mode = Mode.Collapsed;
    private bool _expandRequested;
    private bool _pinned;
    private bool _isDraggingOut;
    private bool _dragOver;
    private bool _hidden;
    private IntPtr _previousForeground;
    private Storyboard? _equalizer;
    private bool _equalizerRunning;
    private Storyboard? _sleepy;
    private MascotMood _shownMood = MascotMood.Hidden;
    private double _wheelAccumulator;
    private DateTime _lastWheelSwitch = DateTime.MinValue;
    private Point? _shelfPressPoint;
    private ShelfItemViewModel? _shelfPressed;
    private bool _shelfDragStarted;
    private string _lyricShown = "";
    private EventHandler? _scrollStep;

    public NotchWindow(NotchViewModel vm, AppSettings settings, MonitorService monitors, Action openSettings)
    {
        _vm = vm;
        _settings = settings;
        _monitors = monitors;
        _openSettings = openSettings;
        InitializeComponent();
        DataContext = vm;

        NotchBody.Clip = _bodyClip;
        NotchBody.SizeChanged += (_, _) => UpdateBodyClip();

        _hoverTimer.Tick += (_, _) =>
        {
            _hoverTimer.Stop();
            if (IsCursorOverNotch())
            {
                Expand();
            }
        };
        _leaveTimer.Tick += (_, _) =>
        {
            _leaveTimer.Stop();
            if (!ShouldStayOpen())
            {
                Collapse();
            }
        };
        _dragLeaveTimer.Tick += (_, _) =>
        {
            _dragLeaveTimer.Stop();
            _dragOver = false;
            ShowDropOverlay(false);
            _leaveTimer.Stop();
            _leaveTimer.Start();
        };
        _blinkTimer.Tick += (_, _) => Blink();
        _lookTimer.Tick += (_, _) => LookAround();

        NotchHost.MouseEnter += (_, _) => OnNotchMouseEnter();
        NotchHost.MouseLeave += (_, _) => OnNotchMouseLeave();
        NotchBody.DragEnter += OnBodyDragEnter;
        NotchBody.DragOver += OnBodyDragOver;
        NotchBody.DragLeave += OnBodyDragLeave;
        NotchBody.Drop += OnBodyDrop;
        ExpandedView.PreviewMouseWheel += OnExpandedMouseWheel;
        ExpandedView.MouseLeftButtonDown += (_, _) => ActivateForKeyboard(null);
        PreviewKeyDown += OnPreviewKeyDown;
        Deactivated += (_, _) =>
        {
            if (_expandRequested && !ShouldStayOpen())
            {
                Collapse();
            }
        };

        ProgressSlider.AddHandler(Thumb.DragStartedEvent, new DragStartedEventHandler((_, _) => _vm.Music.IsSeeking = true));
        ProgressSlider.AddHandler(Thumb.DragCompletedEvent, new DragCompletedEventHandler((_, _) =>
        {
            _vm.Music.IsSeeking = false;
            _vm.Music.SeekToFraction(ProgressSlider.Value);
        }));
        ProgressSlider.PreviewMouseLeftButtonUp += (_, _) =>
        {
            if (!_vm.Music.IsSeeking)
            {
                _vm.Music.SeekToFraction(ProgressSlider.Value);
            }
        };

        VolumeHost.MouseEnter += (_, _) => AnimateVolumeSlider(open: true);
        VolumeHost.MouseLeave += (_, _) => AnimateVolumeSlider(open: false);

        _vm.VisualStateChanged += (_, _) => UpdateMode();
        _vm.PageChanged += (_, direction) => AnimatePageChange(direction);
        _vm.Music.CurrentLineChanged += (_, index) => OnLyricLineChanged(index);
        _settings.PropertyChanged += OnSettingsChanged;
        _monitors.DisplaysChanged += (_, _) => ApplyPlacement();

        SourceInitialized += OnSourceInitialized;
        DpiChanged += (_, _) => Dispatcher.BeginInvoke(ApplyPlacement, DispatcherPriority.Background);
        Loaded += (_, _) =>
        {
            // WPF applies its own initial size after SourceInitialized; place the notch again once it has.
            ApplyPlacement();
            BuildEqualizer();
            UpdateMode(animate: false);
        };
        ContentRendered += (_, _) => ApplyPlacement();
    }

    private enum Mode
    {
        Collapsed,
        Mascot,
        Expanded,
        Hud,
        Alert,
    }

    public IntPtr Handle => _hwnd;

    /// <summary>The monitor the notch is on (for full-screen detection).</summary>
    public IntPtr CurrentMonitor => _display?.Handle ?? IntPtr.Zero;

    /// <summary>Keeps the notch open on a page (used for screenshots: --pin).</summary>
    public void Pin(NotchPage page)
    {
        _pinned = true;
        _vm.SelectPage(page);
        Expand(force: true);
    }

    /// <summary>Picks the display from settings and puts the notch at its top centre.</summary>
    public void ApplyPlacement()
    {
        if (_hwnd == IntPtr.Zero)
        {
            return;
        }

        if (_monitors.Displays.Count == 0)
        {
            _monitors.Refresh();
        }

        var display = _monitors.Resolve(_settings.DisplayTarget, out _);
        _display = display;

        var scale = _settings.NotchScale;
        if (_settings.AutoScaleWideDisplays)
        {
            var dipWidth = display.Bounds.Width / display.Scale;
            scale *= dipWidth >= 3400 ? 1.2 : dipWidth >= 2400 ? 1.1 : 1.0;
        }

        RootScale.ScaleX = RootScale.ScaleY = scale;

        var widthDip = Math.Min(BaseWidth * scale, display.Bounds.Width / display.Scale);
        var heightDip = BaseHeight * scale;
        var width = (int)Math.Ceiling(widthDip * display.Scale);
        var height = (int)Math.Ceiling(heightDip * display.Scale);
        var x = display.Bounds.Left + (display.Bounds.Width - width) / 2;
        var y = display.Bounds.Top;

        NativeMethods.SetWindowPos(_hwnd, NativeMethods.HWND_TOPMOST, x, y, width, height,
            NativeMethods.SWP_NOACTIVATE | NativeMethods.SWP_NOOWNERZORDER);
    }

    /// <summary>Re-reads the displays (after a monitor was plugged in or the layout changed).</summary>
    public void RefreshDisplays() => _displayDebounce.Run(() =>
    {
        _monitors.Refresh();
        ApplyPlacement();
    });

    // ---------------------------------------------------------------- window setup

    private void OnSourceInitialized(object? sender, EventArgs e)
    {
        _hwnd = new WindowInteropHelper(this).Handle;
        var ex = NativeMethods.GetExStyle(_hwnd);
        ex |= NativeMethods.WS_EX_TOOLWINDOW | NativeMethods.WS_EX_NOACTIVATE;
        ex &= ~NativeMethods.WS_EX_APPWINDOW;
        NativeMethods.SetExStyle(_hwnd, ex);
        HwndSource.FromHwnd(_hwnd)?.AddHook(WndProc);
        ApplyPlacement();
    }

    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        switch (msg)
        {
            case NativeMethods.WM_MOUSEACTIVATE:
                // Clicking the notch never steals focus from the app you're using.
                handled = true;
                return new IntPtr(NativeMethods.MA_NOACTIVATE);
            case NativeMethods.WM_DISPLAYCHANGE:
                RefreshDisplays();
                break;
            case NativeMethods.WM_SETTINGCHANGE:
                var area = lParam != IntPtr.Zero ? Marshal.PtrToStringUni(lParam) : null;
                if (area == "intl")
                {
                    _vm.RefreshClock();
                }
                else if (wParam.ToInt64() == 0x002F /* SPI_SETWORKAREA */)
                {
                    RefreshDisplays();
                }

                break;
        }

        return IntPtr.Zero;
    }

    private void BringToTop()
    {
        if (_hwnd != IntPtr.Zero)
        {
            NativeMethods.SetWindowPos(_hwnd, NativeMethods.HWND_TOPMOST, 0, 0, 0, 0,
                NativeMethods.SWP_NOMOVE | NativeMethods.SWP_NOSIZE | NativeMethods.SWP_NOACTIVATE | NativeMethods.SWP_NOOWNERZORDER);
        }
    }

    private void OnSettingsChanged(object? sender, PropertyChangedEventArgs e)
    {
        switch (e.PropertyName)
        {
            case nameof(AppSettings.DisplayTarget):
            case nameof(AppSettings.NotchScale):
            case nameof(AppSettings.AutoScaleWideDisplays):
                ApplyPlacement();
                break;
            case nameof(AppSettings.MinimalNotch):
            case nameof(AppSettings.LyricsEnabled):
                UpdateMode();
                break;
        }
    }

    // ---------------------------------------------------------------- state machine

    private void UpdateMode(bool animate = true)
    {
        if (!IsLoaded)
        {
            return;
        }

        if (_vm.HiddenForFullscreen != _hidden)
        {
            SetHidden(_vm.HiddenForFullscreen);
        }

        Mode next;
        if (_expandRequested)
        {
            next = Mode.Expanded;
        }
        else if (_vm.CurrentAlert is not null)
        {
            next = Mode.Alert;
        }
        else if (_vm.CurrentHud is not null)
        {
            next = Mode.Hud;
        }
        else if (_vm.Mood != MascotMood.Hidden)
        {
            next = Mode.Mascot;
        }
        else
        {
            next = Mode.Collapsed;
        }

        var previous = _mode;
        _mode = next;

        if (next == Mode.Hud && _vm.CurrentHud is { } hud)
        {
            ShowHudContent(hud);
        }

        if (next == Mode.Alert && _vm.CurrentAlert is { } alert && !ReferenceEquals(AlertView.DataContext, alert))
        {
            AlertView.DataContext = alert;
        }

        if (previous != next || !animate)
        {
            Fade(CollapsedView, next == Mode.Collapsed && !_settings.MinimalNotch, 150, next == Mode.Collapsed ? 90 : 0);
            Fade(MascotView, next == Mode.Mascot, 200, next == Mode.Mascot ? 120 : 0);
            Fade(ExpandedView, next == Mode.Expanded, next == Mode.Expanded ? 220 : 110, next == Mode.Expanded ? 70 : 0);
            Fade(HudView, next == Mode.Hud, 160, next == Mode.Hud ? 80 : 0);
            Fade(AlertView, next == Mode.Alert, 180, next == Mode.Alert ? 90 : 0);
        }
        else if (next == Mode.Collapsed)
        {
            Fade(CollapsedView, !_settings.MinimalNotch, 150);
        }

        if (next is Mode.Hud or Mode.Alert or Mode.Expanded)
        {
            BringToTop();
        }

        UpdateCollapsedContent();
        UpdateMascot();
        UpdateEqualizer();

        var target = TargetSize(next);
        var bouncy = next is Mode.Expanded or Mode.Alert || (next == Mode.Hud && previous != Mode.Hud);
        AnimateBody(target.Width, target.Height, animate && !_hidden, bouncy);

        var minimalLine = _settings.MinimalNotch && next == Mode.Collapsed;
        LeftShoulder.Visibility = RightShoulder.Visibility = minimalLine ? Visibility.Hidden : Visibility.Visible;

        UpdateLyricPill();
    }

    private Size TargetSize(Mode mode)
    {
        switch (mode)
        {
            case Mode.Expanded:
                var lyricsSplit = _vm.SelectedPage == NotchPage.Music && _vm.Music.LyricsEnabled && _vm.Music.Media.HasSession;
                var width = lyricsSplit ? ExpandedLyricsWidth : ExpandedWidth;
                ExpandedView.Width = width;
                return new Size(width, ExpandedHeight);
            case Mode.Hud:
                return new Size(HudWidth, HudHeight);
            case Mode.Alert:
                AlertView.Measure(new Size(AlertWidth, double.PositiveInfinity));
                return new Size(AlertWidth, Math.Max(76, Math.Ceiling(AlertView.DesiredSize.Height)));
            case Mode.Mascot:
                return new Size(ClockWidth, CollapsedHeight);
            default:
                if (_settings.MinimalNotch)
                {
                    return new Size(MinimalWidth, MinimalHeight);
                }

                if (_vm.IsMusicActive)
                {
                    return new Size(MusicWidth, CollapsedHeight);
                }

                return new Size(_vm.Pomodoro.IsRunning ? TimerWidth : ClockWidth, CollapsedHeight);
        }
    }

    private void UpdateCollapsedContent()
    {
        var music = _vm.IsMusicActive;
        MiniArt.Visibility = music ? Visibility.Visible : Visibility.Collapsed;
        EqBars.Visibility = music ? Visibility.Visible : Visibility.Collapsed;
        MiniRing.Visibility = !music && _vm.Pomodoro.IsRunning ? Visibility.Visible : Visibility.Collapsed;
    }

    private void SetHidden(bool hidden)
    {
        _hidden = hidden;
        if (hidden)
        {
            var fade = new DoubleAnimation(0, TimeSpan.FromMilliseconds(160));
            fade.Completed += (_, _) =>
            {
                if (_hidden)
                {
                    Hide();
                }
            };
            Root.BeginAnimation(OpacityProperty, fade);
        }
        else
        {
            if (!IsVisible)
            {
                Show();
            }

            ApplyPlacement();
            Root.BeginAnimation(OpacityProperty, new DoubleAnimation(1, TimeSpan.FromMilliseconds(220)));
        }
    }

    private void AnimateBody(double width, double height, bool animate, bool bouncy)
    {
        if (!animate)
        {
            NotchBody.BeginAnimation(WidthProperty, null);
            NotchBody.BeginAnimation(HeightProperty, null);
            NotchBody.Width = width;
            NotchBody.Height = height;
            return;
        }

        var duration = TimeSpan.FromMilliseconds(bouncy ? 440 : 300);
        IEasingFunction ease = bouncy
            ? new BackEase { Amplitude = 0.22, EasingMode = EasingMode.EaseOut }
            : new QuarticEase { EasingMode = EasingMode.EaseOut };
        NotchBody.BeginAnimation(WidthProperty, new DoubleAnimation(width, duration) { EasingFunction = ease });
        NotchBody.BeginAnimation(HeightProperty, new DoubleAnimation(height, duration) { EasingFunction = ease });
    }

    private void UpdateBodyClip()
    {
        var w = NotchBody.ActualWidth;
        var h = NotchBody.ActualHeight;
        var r = Math.Max(0, Math.Min(h / 2, 24));
        _bodyClip.Rect = new Rect(0, -r, w, h + r); // Square top corners, round bottom ones.
        _bodyClip.RadiusX = r;
        _bodyClip.RadiusY = r;
    }

    private void Fade(UIElement element, bool show, double milliseconds, double delay = 0)
    {
        var generation = _fadeGeneration.TryGetValue(element, out var g) ? g + 1 : 1;
        _fadeGeneration[element] = generation;
        if (show)
        {
            element.Visibility = Visibility.Visible;
        }
        else if (element.Visibility != Visibility.Visible)
        {
            return;
        }

        var animation = new DoubleAnimation(show ? 1 : 0, TimeSpan.FromMilliseconds(milliseconds))
        {
            BeginTime = TimeSpan.FromMilliseconds(delay),
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut },
        };
        if (!show)
        {
            animation.Completed += (_, _) =>
            {
                if (_fadeGeneration[element] == generation)
                {
                    element.Visibility = Visibility.Collapsed;
                }
            };
        }

        element.BeginAnimation(OpacityProperty, animation);
    }

    // ---------------------------------------------------------------- hover, expand, collapse

    private void OnNotchMouseEnter()
    {
        _leaveTimer.Stop();
        if (_expandRequested || _mode == Mode.Alert || _hidden)
        {
            return;
        }

        _hoverTimer.Stop();
        _hoverTimer.Start();
    }

    private void OnNotchMouseLeave()
    {
        _hoverTimer.Stop();
        if (_expandRequested)
        {
            _leaveTimer.Stop();
            _leaveTimer.Start();
        }
    }

    private void Expand(bool force = false)
    {
        if (_expandRequested || _hidden)
        {
            return;
        }

        if (!force && _vm.CurrentAlert is not null)
        {
            return; // An alert is waiting for an answer.
        }

        _expandRequested = true;
        _vm.IsExpanded = true;
        UpdateMode();
    }

    private void Collapse()
    {
        if (!_expandRequested || _pinned)
        {
            return;
        }

        _expandRequested = false;
        _vm.IsExpanded = false;
        _vm.Shelf.ClearSelection();
        ResetPageBar();
        RestoreForeground();
        UpdateMode();
    }

    private bool ShouldStayOpen() =>
        _pinned || _isDraggingOut || _dragOver || IsCursorOverNotch() ||
        (IsActive && (TodoTitleBox.IsKeyboardFocused || TodoTimeBox.IsKeyboardFocused));

    /// <summary>Hit-tests the cursor against the notch in screen pixels (works during drag and drop too).</summary>
    private bool IsCursorOverNotch()
    {
        if (PresentationSource.FromVisual(NotchHost) is null || !NativeMethods.GetCursorPos(out var cursor))
        {
            return NotchHost.IsMouseOver;
        }

        var topLeft = NotchHost.PointToScreen(new Point(0, 0));
        var bottomRight = NotchHost.PointToScreen(new Point(NotchHost.ActualWidth, NotchHost.ActualHeight));
        return cursor.X >= topLeft.X - 1 && cursor.X <= bottomRight.X + 1 &&
               cursor.Y >= topLeft.Y - 1 && cursor.Y <= bottomRight.Y + 2;
    }

    private void SettingsButton_Click(object sender, RoutedEventArgs e)
    {
        _pinned = false;
        Collapse();
        _openSettings();
    }

    private void PageBar_MouseEnter(object sender, MouseEventArgs e)
    {
        Fade(PageIcons, true, 140);
        PageIcons.IsHitTestVisible = true;
        PageDots.BeginAnimation(OpacityProperty, new DoubleAnimation(0, TimeSpan.FromMilliseconds(120)));
    }

    private void PageBar_MouseLeave(object sender, MouseEventArgs e) => ResetPageBar();

    private void ResetPageBar()
    {
        PageIcons.IsHitTestVisible = false;
        PageIcons.BeginAnimation(OpacityProperty, new DoubleAnimation(0, TimeSpan.FromMilliseconds(120)));
        PageDots.BeginAnimation(OpacityProperty, new DoubleAnimation(1, TimeSpan.FromMilliseconds(160)));
    }

    private void AnimatePageChange(int direction)
    {
        var duration = TimeSpan.FromMilliseconds(240);
        var ease = new CubicEase { EasingMode = EasingMode.EaseOut };
        PageShift.BeginAnimation(TranslateTransform.XProperty, new DoubleAnimation(26 * direction, 0, duration) { EasingFunction = ease });
        PagesHost.BeginAnimation(OpacityProperty, new DoubleAnimation(0.2, 1, duration) { EasingFunction = ease });
        if (_mode == Mode.Expanded)
        {
            UpdateMode();
        }
    }

    private void OnExpandedMouseWheel(object sender, MouseWheelEventArgs e)
    {
        var scroller = FindScrollable(e.OriginalSource as DependencyObject);
        if (scroller is not null)
        {
            if (ReferenceEquals(scroller, ShelfScroller))
            {
                ShelfScroller.ScrollToHorizontalOffset(ShelfScroller.HorizontalOffset - e.Delta * 0.6);
                e.Handled = true;
            }

            return; // Lists scroll themselves.
        }

        e.Handled = true;
        _wheelAccumulator += e.Delta;
        if (Math.Abs(_wheelAccumulator) < 100 || DateTime.Now - _lastWheelSwitch < TimeSpan.FromMilliseconds(260))
        {
            return;
        }

        _vm.NextPage(_wheelAccumulator > 0 ? -1 : 1);
        _wheelAccumulator = 0;
        _lastWheelSwitch = DateTime.Now;
    }

    private ScrollViewer? FindScrollable(DependencyObject? node)
    {
        while (node is not null && !ReferenceEquals(node, ExpandedView))
        {
            if (node is ScrollViewer sv && (sv.ScrollableHeight > 0 || sv.ScrollableWidth > 0))
            {
                return sv;
            }

            node = node is Visual or System.Windows.Media.Media3D.Visual3D ? VisualTreeHelper.GetParent(node) : LogicalTreeHelper.GetParent(node);
        }

        return null;
    }

    private void OnPreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape)
        {
            if (!_vm.Todo.IsEditing || !(TodoTitleBox.IsKeyboardFocused || TodoTimeBox.IsKeyboardFocused))
            {
                _pinned = false;
                Collapse();
                e.Handled = true;
            }

            return;
        }

        if (!_expandRequested || Keyboard.FocusedElement is TextBox)
        {
            return;
        }

        if (e.Key == Key.Left)
        {
            _vm.NextPage(-1);
            e.Handled = true;
        }
        else if (e.Key == Key.Right)
        {
            _vm.NextPage(1);
            e.Handled = true;
        }
    }

    // ---------------------------------------------------------------- typing (to-do)

    private void TextInput_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e) =>
        ActivateForKeyboard(sender as IInputElement);

    /// <summary>The notch never takes focus on its own; it only does when you click a text box.</summary>
    private void ActivateForKeyboard(IInputElement? target)
    {
        if (!IsActive)
        {
            var foreground = NativeMethods.GetForegroundWindow();
            if (foreground != _hwnd)
            {
                _previousForeground = foreground;
            }

            NativeMethods.SetForegroundWindow(_hwnd);
        }

        if (target is not null)
        {
            Dispatcher.BeginInvoke(() => Keyboard.Focus(target), DispatcherPriority.Input);
        }
    }

    private void RestoreForeground()
    {
        if (TodoTitleBox.IsKeyboardFocused || TodoTimeBox.IsKeyboardFocused)
        {
            Keyboard.ClearFocus();
        }

        if (_previousForeground != IntPtr.Zero && NativeMethods.GetForegroundWindow() == _hwnd)
        {
            NativeMethods.SetForegroundWindow(_previousForeground);
        }

        _previousForeground = IntPtr.Zero;
    }

    // ---------------------------------------------------------------- shelf: drop in

    private static bool HasDroppableData(IDataObject data) =>
        data.GetDataPresent(DataFormats.FileDrop) || data.GetDataPresent(DataFormats.UnicodeText) || data.GetDataPresent(DataFormats.Text);

    private void OnBodyDragEnter(object sender, DragEventArgs e)
    {
        e.Handled = true;
        if (_isDraggingOut || !_vm.HasPage(NotchPage.Shelf) || !HasDroppableData(e.Data))
        {
            e.Effects = DragDropEffects.None;
            return;
        }

        _dragOver = true;
        _dragLeaveTimer.Stop();
        _leaveTimer.Stop();
        if (_vm.SelectedPage != NotchPage.Shelf)
        {
            _vm.SelectPage(NotchPage.Shelf);
        }

        Expand(force: true);
        ShowDropOverlay(true);
        e.Effects = DragDropEffects.Copy;
    }

    private void OnBodyDragOver(object sender, DragEventArgs e)
    {
        e.Handled = true;
        if (_isDraggingOut || !_dragOver)
        {
            e.Effects = DragDropEffects.None;
            return;
        }

        _dragLeaveTimer.Stop();
        e.Effects = DragDropEffects.Copy;
    }

    private void OnBodyDragLeave(object sender, DragEventArgs e)
    {
        e.Handled = true;
        _dragLeaveTimer.Stop();
        _dragLeaveTimer.Start();
    }

    private void OnBodyDrop(object sender, DragEventArgs e)
    {
        e.Handled = true;
        _dragLeaveTimer.Stop();
        _dragOver = false;
        ShowDropOverlay(false);
        if (_isDraggingOut)
        {
            return; // Brought back to the notch: it simply stays on the shelf.
        }

        try
        {
            if (e.Data.GetData(DataFormats.FileDrop) is string[] files && files.Length > 0)
            {
                _vm.Shelf.AddPaths(files);
            }
            else if (e.Data.GetData(DataFormats.UnicodeText) is string text && text.Length > 0)
            {
                _vm.Shelf.AddText(text);
            }
            else if (e.Data.GetData(DataFormats.Text) is string ansi && ansi.Length > 0)
            {
                _vm.Shelf.AddText(ansi);
            }
        }
        catch (Exception ex)
        {
            Log.Warn("Drop failed", ex);
        }

        _leaveTimer.Stop();
        _leaveTimer.Start();
    }

    private void ShowDropOverlay(bool show) => Fade(DropOverlay, show, 120);

    // ---------------------------------------------------------------- shelf: click, select, drag out

    private void ShelfTile_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (IsInsideButton(e.OriginalSource as DependencyObject, sender as DependencyObject) ||
            (sender as FrameworkElement)?.DataContext is not ShelfItemViewModel item)
        {
            return;
        }

        var ctrl = NativeMethods.IsKeyDown(NativeMethods.VK_CONTROL);
        var shift = NativeMethods.IsKeyDown(NativeMethods.VK_SHIFT);
        if (ctrl || shift)
        {
            _vm.Shelf.Select(item, toggle: ctrl, range: shift);
            _shelfPressed = null;
            e.Handled = true;
            return;
        }

        _shelfPressed = item;
        _shelfPressPoint = e.GetPosition(this);
        _shelfDragStarted = false;
    }

    private void ShelfTile_PreviewMouseMove(object sender, MouseEventArgs e)
    {
        if (_shelfPressed is null || _shelfPressPoint is null || e.LeftButton != MouseButtonState.Pressed)
        {
            return;
        }

        var delta = e.GetPosition(this) - _shelfPressPoint.Value;
        if (Math.Abs(delta.X) < SystemParameters.MinimumHorizontalDragDistance &&
            Math.Abs(delta.Y) < SystemParameters.MinimumVerticalDragDistance)
        {
            return;
        }

        var item = _shelfPressed;
        _shelfPressed = null;
        _shelfDragStarted = true;
        StartShelfDrag((DependencyObject)sender, item);
    }

    private void ShelfTile_PreviewMouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (_shelfPressed is { } item && !_shelfDragStarted && !IsInsideButton(e.OriginalSource as DependencyObject, sender as DependencyObject))
        {
            _vm.Shelf.ClearSelection();
            _vm.Shelf.Open(item);
        }

        _shelfPressed = null;
        _shelfPressPoint = null;
    }

    private void StartShelfDrag(DependencyObject source, ShelfItemViewModel item)
    {
        var items = _vm.Shelf.DragSet(item).Where(i => i.Exists).ToList();
        if (items.Count == 0)
        {
            return;
        }

        var data = new DataObject();
        var files = items.Where(i => !i.IsText).Select(i => i.Model.Path!).ToArray();
        if (files.Length > 0)
        {
            data.SetData(DataFormats.FileDrop, files);
        }
        else
        {
            data.SetText(string.Join(Environment.NewLine, items.Select(i => i.Model.Text ?? "")));
        }

        _isDraggingOut = true;
        try
        {
            // Copy, never move: dragging out of the shelf leaves your original files where they are.
            DragDrop.DoDragDrop(source, data, DragDropEffects.Copy | DragDropEffects.Link);
        }
        catch (Exception ex)
        {
            Log.Warn("Drag out failed", ex);
        }
        finally
        {
            _isDraggingOut = false;
        }

        _leaveTimer.Stop();
        _leaveTimer.Start();
    }

    /// <summary>True when the click landed on a button inside the tile (such as the remove button).</summary>
    private static bool IsInsideButton(DependencyObject? node, DependencyObject? boundary)
    {
        while (node is not null && !ReferenceEquals(node, boundary))
        {
            if (node is ButtonBase)
            {
                return true;
            }

            node = node is Visual ? VisualTreeHelper.GetParent(node) : LogicalTreeHelper.GetParent(node);
        }

        return false;
    }

    // ---------------------------------------------------------------- music bits

    private void AnimateVolumeSlider(bool open)
    {
        var duration = TimeSpan.FromMilliseconds(180);
        var ease = new CubicEase { EasingMode = EasingMode.EaseOut };
        VolumeSlider.BeginAnimation(WidthProperty, new DoubleAnimation(open ? 70 : 0, duration) { EasingFunction = ease });
        VolumeSlider.BeginAnimation(OpacityProperty, new DoubleAnimation(open ? 1 : 0, duration));
    }

    private void BuildEqualizer()
    {
        _equalizer = new Storyboard { RepeatBehavior = RepeatBehavior.Forever };
        Timeline.SetDesiredFrameRate(_equalizer, 24); // Gentle on the CPU.
        var durations = new[] { 0.46, 0.58, 0.38, 0.52 };
        for (var i = 0; i < EqBars.Children.Count; i++)
        {
            var bar = (Rectangle)EqBars.Children[i];
            bar.RenderTransformOrigin = new Point(0.5, 0.5);
            bar.RenderTransform = new ScaleTransform(1, 0.35);
            var animation = new DoubleAnimation(0.25, 1.0, TimeSpan.FromSeconds(durations[i]))
            {
                AutoReverse = true,
                RepeatBehavior = RepeatBehavior.Forever,
                BeginTime = TimeSpan.FromSeconds(i * 0.07),
                EasingFunction = new SineEase { EasingMode = EasingMode.EaseInOut },
            };
            Storyboard.SetTarget(animation, bar);
            Storyboard.SetTargetProperty(animation, new PropertyPath("RenderTransform.ScaleY"));
            _equalizer.Children.Add(animation);
        }
    }

    private void UpdateEqualizer()
    {
        if (_equalizer is null)
        {
            return;
        }

        var run = _mode == Mode.Collapsed && !_settings.MinimalNotch && _vm.IsMusicActive && !_hidden;
        if (run && !_equalizerRunning)
        {
            _equalizer.Begin(this, true);
            _equalizerRunning = true;
        }
        else if (!run && _equalizerRunning)
        {
            _equalizer.Stop(this);
            _equalizerRunning = false;
        }
    }

    private void UpdateLyricPill()
    {
        var show = _mode is Mode.Collapsed or Mode.Hud && _vm.Music.ShowLyricPill && !_hidden;
        if (show)
        {
            SetLyricText(_vm.Music.CurrentLineText, animate: LyricPill.Visibility == Visibility.Visible);
        }

        Fade(LyricPill, show, 180);
    }

    private void OnLyricLineChanged(int index)
    {
        UpdateLyricPill();
        if (_mode == Mode.Expanded && index >= 0 && LyricsPanel.IsVisible)
        {
            Dispatcher.BeginInvoke(() => ScrollLyricIntoView(index), DispatcherPriority.Loaded);
        }
    }

    private void SetLyricText(string text, bool animate)
    {
        if (text == _lyricShown)
        {
            return;
        }

        _lyricShown = text;
        var typeface = new Typeface(LyricText.FontFamily, LyricText.FontStyle, FontWeights.SemiBold, LyricText.FontStretch);
        var measured = new FormattedText(text, CultureInfo.CurrentUICulture, FlowDirection.LeftToRight, typeface, LyricText.FontSize,
            Brushes.White, VisualTreeHelper.GetDpi(this).PixelsPerDip).WidthIncludingTrailingWhitespace;
        var width = Math.Clamp(measured + 28 + 19 + 4, 120, 640);

        if (!animate)
        {
            LyricText.Text = text;
            LyricPill.BeginAnimation(WidthProperty, null);
            LyricPill.Width = width;
            return;
        }

        var fadeOut = new DoubleAnimation(0, TimeSpan.FromMilliseconds(90));
        fadeOut.Completed += (_, _) =>
        {
            LyricText.Text = _lyricShown;
            LyricText.BeginAnimation(OpacityProperty, new DoubleAnimation(1, TimeSpan.FromMilliseconds(160)));
        };
        LyricText.BeginAnimation(OpacityProperty, fadeOut);
        LyricPill.BeginAnimation(WidthProperty, new DoubleAnimation(width, TimeSpan.FromMilliseconds(260))
        {
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut },
        });
    }

    private void ScrollLyricIntoView(int index)
    {
        if (LyricsList.ItemContainerGenerator.ContainerFromIndex(index) is not FrameworkElement line || !line.IsVisible)
        {
            return;
        }

        var top = line.TransformToAncestor(LyricsScroller).Transform(new Point(0, 0)).Y;
        var target = LyricsScroller.VerticalOffset + top - (LyricsScroller.ViewportHeight - line.ActualHeight) / 2;
        SmoothScroll(LyricsScroller, Math.Clamp(target, 0, LyricsScroller.ScrollableHeight));
    }

    private void SmoothScroll(ScrollViewer viewer, double target)
    {
        if (_scrollStep is not null)
        {
            CompositionTarget.Rendering -= _scrollStep;
        }

        var start = viewer.VerticalOffset;
        var began = DateTime.Now;
        _scrollStep = (_, _) =>
        {
            var t = Math.Min(1, (DateTime.Now - began).TotalMilliseconds / 280);
            var eased = 1 - Math.Pow(1 - t, 3);
            viewer.ScrollToVerticalOffset(start + (target - start) * eased);
            if (t >= 1 && _scrollStep is not null)
            {
                CompositionTarget.Rendering -= _scrollStep;
                _scrollStep = null;
            }
        };
        CompositionTarget.Rendering += _scrollStep;
    }

    // ---------------------------------------------------------------- overlay

    private void ShowHudContent(HudInfo hud)
    {
        HudView.DataContext = hud;
        var hasLevel = hud.Level.HasValue;
        HudBar.Visibility = HudLevel.Visibility = hasLevel ? Visibility.Visible : Visibility.Collapsed;
        HudDetail.Visibility = hasLevel || string.IsNullOrEmpty(hud.Detail) ? Visibility.Collapsed : Visibility.Visible;
        if (hasLevel)
        {
            HudLevel.Text = _vm.HudLevelText;
            HudBarFill.BeginAnimation(WidthProperty, new DoubleAnimation(104 * Math.Clamp(hud.Level!.Value, 0, 1), TimeSpan.FromMilliseconds(140)));
        }
    }

    // ---------------------------------------------------------------- mascot

    private void UpdateMascot()
    {
        var mood = _mode == Mode.Mascot ? _vm.Mood : MascotMood.Hidden;
        if (mood == _shownMood)
        {
            return;
        }

        _shownMood = mood;
        _blinkTimer.Stop();
        _lookTimer.Stop();
        _sleepy?.Stop(this);
        OpenEyes.Visibility = mood == MascotMood.Waving ? Visibility.Collapsed : Visibility.Visible;
        HappyEyes.Visibility = mood == MascotMood.Waving ? Visibility.Visible : Visibility.Collapsed;
        SleepyZ.Opacity = 0;

        switch (mood)
        {
            case MascotMood.Awake:
                SetEyeOpenness(1, 200);
                ScheduleBlink();
                _lookTimer.Interval = TimeSpan.FromSeconds(3.5);
                _lookTimer.Start();
                break;
            case MascotMood.Dozing:
                SetEyeOpenness(0.16, 900);
                EyesShift.BeginAnimation(TranslateTransform.XProperty, new DoubleAnimation(0, TimeSpan.FromMilliseconds(400)));
                StartSleepyZ();
                break;
            case MascotMood.Waving:
                var bounce = new DoubleAnimationUsingKeyFrames { Duration = TimeSpan.FromMilliseconds(700) };
                bounce.KeyFrames.Add(new EasingDoubleKeyFrame(-3, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(160)), new CubicEase()));
                bounce.KeyFrames.Add(new EasingDoubleKeyFrame(0, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(340)), new BounceEase { Bounces = 1 }));
                bounce.KeyFrames.Add(new EasingDoubleKeyFrame(-2, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(500)), new CubicEase()));
                bounce.KeyFrames.Add(new EasingDoubleKeyFrame(0, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(700)), new CubicEase()));
                EyesShift.BeginAnimation(TranslateTransform.YProperty, bounce);
                break;
        }
    }

    private void SetEyeOpenness(double scaleY, double milliseconds)
    {
        var animation = new DoubleAnimation(scaleY, TimeSpan.FromMilliseconds(milliseconds)) { EasingFunction = new CubicEase { EasingMode = EasingMode.EaseInOut } };
        LeftEyeScale.BeginAnimation(ScaleTransform.ScaleYProperty, animation);
        RightEyeScale.BeginAnimation(ScaleTransform.ScaleYProperty, animation);
    }

    private void ScheduleBlink()
    {
        _blinkTimer.Interval = TimeSpan.FromSeconds(2.4 + _random.NextDouble() * 3.6);
        _blinkTimer.Start();
    }

    private void Blink()
    {
        _blinkTimer.Stop();
        if (_shownMood != MascotMood.Awake)
        {
            return;
        }

        var blink = new DoubleAnimationUsingKeyFrames { Duration = TimeSpan.FromMilliseconds(220) };
        blink.KeyFrames.Add(new LinearDoubleKeyFrame(0.1, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(80))));
        blink.KeyFrames.Add(new LinearDoubleKeyFrame(1, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(220))));
        LeftEyeScale.BeginAnimation(ScaleTransform.ScaleYProperty, blink);
        RightEyeScale.BeginAnimation(ScaleTransform.ScaleYProperty, blink);
        ScheduleBlink();
    }

    private void LookAround()
    {
        if (_shownMood != MascotMood.Awake)
        {
            _lookTimer.Stop();
            return;
        }

        var options = new[] { -3.0, 0, 0, 3.0 };
        var x = options[_random.Next(options.Length)];
        EyesShift.BeginAnimation(TranslateTransform.XProperty, new DoubleAnimation(x, TimeSpan.FromMilliseconds(260))
        {
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseInOut },
        });
        _lookTimer.Interval = TimeSpan.FromSeconds(2.5 + _random.NextDouble() * 4);
    }

    private void StartSleepyZ()
    {
        _sleepy = new Storyboard { RepeatBehavior = RepeatBehavior.Forever };
        Timeline.SetDesiredFrameRate(_sleepy, 20);
        var rise = new DoubleAnimation(4, -6, TimeSpan.FromSeconds(2.2));
        Storyboard.SetTarget(rise, SleepyZ);
        Storyboard.SetTargetProperty(rise, new PropertyPath("RenderTransform.Y"));
        var fade = new DoubleAnimationUsingKeyFrames { Duration = TimeSpan.FromSeconds(2.2) };
        fade.KeyFrames.Add(new LinearDoubleKeyFrame(0, KeyTime.FromTimeSpan(TimeSpan.Zero)));
        fade.KeyFrames.Add(new LinearDoubleKeyFrame(0.9, KeyTime.FromTimeSpan(TimeSpan.FromSeconds(0.6))));
        fade.KeyFrames.Add(new LinearDoubleKeyFrame(0, KeyTime.FromTimeSpan(TimeSpan.FromSeconds(2.2))));
        Storyboard.SetTarget(fade, SleepyZ);
        Storyboard.SetTargetProperty(fade, new PropertyPath(OpacityProperty));
        _sleepy.Children.Add(rise);
        _sleepy.Children.Add(fade);
        _sleepy.Begin(this, true);
    }
}
