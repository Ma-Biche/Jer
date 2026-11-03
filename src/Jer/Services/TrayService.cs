using System;
using System.ComponentModel;
using System.Drawing;
using System.Windows.Forms;
using Jer.Core;
using Jer.Interop;
using Jer.Models;
using Microsoft.Win32;
using Application = System.Windows.Application;

namespace Jer.Services;

/// <summary>The Jer icon next to the clock, with quick switches and the display picker.</summary>
public sealed class TrayService : IDisposable
{
    private readonly AppSettings _settings;
    private readonly MonitorService _monitors;
    private readonly Action _openSettings;
    private readonly Action _openAbout;
    private readonly Action _exit;
    private readonly NotifyIcon _icon;
    private readonly ContextMenuStrip _menu;
    private readonly ToolStripMenuItem _startup;
    private readonly ToolStripMenuItem _mascot;
    private readonly ToolStripMenuItem _minimal;
    private readonly ToolStripMenuItem _fullscreen;
    private readonly ToolStripMenuItem _display;
    private Icon? _currentIcon;

    public TrayService(AppSettings settings, MonitorService monitors, Action openSettings, Action openAbout, Action exit)
    {
        _settings = settings;
        _monitors = monitors;
        _openSettings = openSettings;
        _openAbout = openAbout;
        _exit = exit;

        _menu = new ContextMenuStrip
        {
            Renderer = new DarkMenuRenderer(),
            ShowImageMargin = false,
            ShowCheckMargin = true,
            Font = new Font("Segoe UI", 9.5f),
        };

        var title = new ToolStripMenuItem("Jer") { Enabled = false };
        var settingsItem = new ToolStripMenuItem("Settings…", null, (_, _) => _openSettings());
        _display = new ToolStripMenuItem("Show notch on");
        _display.DropDown = new ToolStripDropDownMenu { Renderer = new DarkMenuRenderer(), ShowImageMargin = false, ShowCheckMargin = true };
        _display.DropDownOpening += (_, _) => BuildDisplayMenu();
        BuildDisplayMenu();

        _startup = new ToolStripMenuItem("Start with Windows", null, (_, _) => ToggleStartup());
        _mascot = new ToolStripMenuItem("Jer mascot", null, (_, _) => _settings.MascotEnabled = !_settings.MascotEnabled);
        _minimal = new ToolStripMenuItem("Minimal notch", null, (_, _) => _settings.MinimalNotch = !_settings.MinimalNotch);
        _fullscreen = new ToolStripMenuItem("Hide in full screen", null, (_, _) => _settings.HideInFullscreen = !_settings.HideInFullscreen);

        _menu.Items.AddRange(new ToolStripItem[]
        {
            title,
            settingsItem,
            new ToolStripSeparator(),
            _display,
            new ToolStripSeparator(),
            _startup,
            _mascot,
            _minimal,
            _fullscreen,
            new ToolStripSeparator(),
            new ToolStripMenuItem("About Jer", null, (_, _) => _openAbout()),
            new ToolStripMenuItem("Exit", null, (_, _) => _exit()),
        });
        _menu.Opening += (_, _) => RefreshChecks();

        _icon = new NotifyIcon
        {
            Text = "Jer",
            ContextMenuStrip = _menu,
            Visible = false,
        };
        _icon.MouseClick += (_, e) =>
        {
            if (e.Button == MouseButtons.Left)
            {
                _openSettings();
            }
        };

        UpdateIcon();
        _icon.Visible = true;
        SystemEvents.UserPreferenceChanged += OnUserPreferenceChanged;
        _settings.PropertyChanged += OnSettingsChanged;
    }

    public void Dispose()
    {
        SystemEvents.UserPreferenceChanged -= OnUserPreferenceChanged;
        _settings.PropertyChanged -= OnSettingsChanged;
        _icon.Visible = false;
        _icon.Dispose();
        _menu.Dispose();
        _currentIcon?.Dispose();
    }

    private void OnSettingsChanged(object? sender, PropertyChangedEventArgs e) => RefreshChecks();

    private void ToggleStartup()
    {
        var enable = !StartupService.IsEnabled();
        StartupService.SetEnabled(enable);
        _settings.StartWithWindows = StartupService.IsEnabled();
        RefreshChecks();
    }

    private void RefreshChecks()
    {
        _startup.Checked = StartupService.IsEnabled();
        _mascot.Checked = _settings.MascotEnabled;
        _minimal.Checked = _settings.MinimalNotch;
        _fullscreen.Checked = _settings.HideInFullscreen;
    }

    private void BuildDisplayMenu()
    {
        _display.DropDownItems.Clear();
        var usePrimary = string.Equals(_settings.DisplayTarget, AppSettings.PrimaryDisplay, StringComparison.OrdinalIgnoreCase);
        var main = new ToolStripMenuItem("Main display", null, (_, _) =>
        {
            _settings.DisplayTarget = AppSettings.PrimaryDisplay;
            _settings.DisplayTargetName = null;
        })
        {
            Checked = usePrimary,
        };
        _display.DropDownItems.Add(main);
        _display.DropDownItems.Add(new ToolStripSeparator());

        foreach (var display in _monitors.Displays)
        {
            var d = display;
            var label = $"Display {d.Number} · {d.FriendlyName}" + (d.IsPrimary ? " (main)" : "");
            var item = new ToolStripMenuItem(label, null, (_, _) =>
            {
                _settings.DisplayTarget = d.Key;
                _settings.DisplayTargetName = d.FriendlyName;
            })
            {
                Checked = !usePrimary && string.Equals(_settings.DisplayTarget, d.Key, StringComparison.OrdinalIgnoreCase),
            };
            _display.DropDownItems.Add(item);
        }
    }

    private void OnUserPreferenceChanged(object sender, UserPreferenceChangedEventArgs e) =>
        Application.Current?.Dispatcher.BeginInvoke(UpdateIcon);

    /// <summary>White icon on a dark taskbar, black icon on a light one.</summary>
    private void UpdateIcon()
    {
        try
        {
            var light = false;
            using (var key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize"))
            {
                light = key?.GetValue("SystemUsesLightTheme") is int value && value == 1;
            }

            var resource = Application.GetResourceStream(new Uri($"pack://application:,,,/Assets/{(light ? "TrayBlack" : "TrayWhite")}.ico"));
            if (resource is null)
            {
                return;
            }

            var dpi = NativeMethods.GetDpiForSystem();
            var size = (int)Math.Round(16 * (dpi == 0 ? 96 : dpi) / 96.0);
            using var stream = resource.Stream;
            var next = new Icon(stream, size, size);
            _icon.Icon = next;
            _currentIcon?.Dispose();
            _currentIcon = next;
        }
        catch (Exception ex)
        {
            Log.Warn("Could not update the tray icon", ex);
        }
    }

    private sealed class DarkMenuRenderer : ToolStripProfessionalRenderer
    {
        public DarkMenuRenderer()
            : base(new DarkColors())
        {
            RoundedEdges = false;
        }

        protected override void OnRenderItemText(ToolStripItemTextRenderEventArgs e)
        {
            e.TextColor = e.Item.Enabled ? Color.FromArgb(240, 240, 244) : Color.FromArgb(150, 150, 158);
            base.OnRenderItemText(e);
        }

        protected override void OnRenderArrow(ToolStripArrowRenderEventArgs e)
        {
            e.ArrowColor = Color.FromArgb(220, 220, 226);
            base.OnRenderArrow(e);
        }

        protected override void OnRenderItemCheck(ToolStripItemImageRenderEventArgs e)
        {
            var r = e.ImageRectangle;
            using var pen = new Pen(Color.FromArgb(139, 123, 255), 2f)
            {
                StartCap = System.Drawing.Drawing2D.LineCap.Round,
                EndCap = System.Drawing.Drawing2D.LineCap.Round,
            };
            e.Graphics.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
            var x = r.Left + r.Width * 0.2f;
            var y = r.Top + r.Height * 0.5f;
            e.Graphics.DrawLines(pen, new[]
            {
                new PointF(x, y),
                new PointF(r.Left + r.Width * 0.42f, r.Top + r.Height * 0.72f),
                new PointF(r.Left + r.Width * 0.82f, r.Top + r.Height * 0.28f),
            });
        }

        protected override void OnRenderSeparator(ToolStripSeparatorRenderEventArgs e)
        {
            var y = e.Item.Height / 2;
            using var pen = new Pen(Color.FromArgb(64, 64, 72));
            e.Graphics.DrawLine(pen, 8, y, e.Item.Width - 8, y);
        }
    }

    private sealed class DarkColors : ProfessionalColorTable
    {
        private static readonly Color Background = Color.FromArgb(32, 32, 36);
        private static readonly Color Hover = Color.FromArgb(52, 52, 58);
        private static readonly Color Border = Color.FromArgb(60, 60, 68);

        public override Color ToolStripDropDownBackground => Background;
        public override Color ImageMarginGradientBegin => Background;
        public override Color ImageMarginGradientMiddle => Background;
        public override Color ImageMarginGradientEnd => Background;
        public override Color MenuBorder => Border;
        public override Color MenuItemBorder => Hover;
        public override Color MenuItemSelected => Hover;
        public override Color MenuItemSelectedGradientBegin => Hover;
        public override Color MenuItemSelectedGradientEnd => Hover;
        public override Color MenuItemPressedGradientBegin => Hover;
        public override Color MenuItemPressedGradientEnd => Hover;
        public override Color CheckBackground => Background;
        public override Color CheckSelectedBackground => Hover;
        public override Color CheckPressedBackground => Hover;
        public override Color SeparatorDark => Border;
        public override Color SeparatorLight => Background;
    }
}
