using System;
using System.Globalization;
using System.Windows.Threading;
using Jer.Core;
using Jer.Interop;
using Windows.Networking.Connectivity;
using Windows.System.Power;

namespace Jer.Services;

public sealed record NetworkState(bool Connected, string Name, bool IsWifi);

public sealed record PowerState(bool HasBattery, bool PluggedIn, int Percent, bool Charging);

/// <summary>
/// Watches the things the notch reacts to: idle time, full-screen apps, Caps Lock, keyboard language,
/// charger and network. Windows pushes power and network changes; the rest are cheap polls.
/// Jer never reads which keys you type.
/// </summary>
public sealed class SystemWatcher : IDisposable
{
    private readonly Dispatcher _dispatcher;
    private readonly Func<IntPtr> _notchMonitor;
    private readonly Func<IntPtr> _notchWindow;
    private readonly DispatcherTimer _fast = new(DispatcherPriority.Background) { Interval = TimeSpan.FromMilliseconds(250) };
    private readonly DispatcherTimer _slow = new(DispatcherPriority.Background) { Interval = TimeSpan.FromSeconds(1) };
    private readonly Debouncer _networkDebounce = new(TimeSpan.FromSeconds(1.5));

    private bool _capsLock;
    private IntPtr _layoutWindow;
    private IntPtr _layout;
    private NetworkState? _network;
    private PowerState? _power;
    private bool _watchKeys;

    public SystemWatcher(Dispatcher dispatcher, Func<IntPtr> notchMonitor, Func<IntPtr> notchWindow)
    {
        _dispatcher = dispatcher;
        _notchMonitor = notchMonitor;
        _notchWindow = notchWindow;
        _fast.Tick += (_, _) => PollKeys();
        _slow.Tick += (_, _) => PollSlow();
    }

    public event Action<bool>? CapsLockChanged;
    public event Action<CultureInfo>? KeyboardLayoutChanged;
    public event Action<bool>? FullscreenChanged;
    public event Action<NetworkState>? NetworkChanged;
    public event Action<PowerState, PowerState>? PowerChanged;
    public event Action? Tick;

    public TimeSpan IdleTime { get; private set; }
    public bool IsFullscreen { get; private set; }
    public PowerState? Power => _power;

    public void Start()
    {
        _capsLock = IsCapsLockOn();
        _slow.Start();

        try
        {
            _power = ReadPower();
            PowerManager.BatteryStatusChanged += OnPowerEvent;
            PowerManager.PowerSupplyStatusChanged += OnPowerEvent;
            PowerManager.RemainingChargePercentChanged += OnPowerEvent;
        }
        catch (Exception ex)
        {
            Log.Warn("Power notifications are not available", ex);
        }

        try
        {
            _network = ReadNetwork();
            NetworkInformation.NetworkStatusChanged += OnNetworkStatusChanged;
        }
        catch (Exception ex)
        {
            Log.Warn("Network notifications are not available", ex);
        }
    }

    /// <summary>Caps Lock and language polling only runs while one of those overlays is on.</summary>
    public void SetKeyWatching(bool enabled)
    {
        _watchKeys = enabled;
        if (enabled)
        {
            _capsLock = IsCapsLockOn();
            ReadLayout(out _layoutWindow, out _layout);
            _fast.Start();
        }
        else
        {
            _fast.Stop();
        }
    }

    public void Dispose()
    {
        _fast.Stop();
        _slow.Stop();
        try
        {
            PowerManager.BatteryStatusChanged -= OnPowerEvent;
            PowerManager.PowerSupplyStatusChanged -= OnPowerEvent;
            PowerManager.RemainingChargePercentChanged -= OnPowerEvent;
            NetworkInformation.NetworkStatusChanged -= OnNetworkStatusChanged;
        }
        catch
        {
            // Shutting down.
        }
    }

    private static bool IsCapsLockOn() => (NativeMethods.GetKeyState(NativeMethods.VK_CAPITAL) & 1) != 0;

    private void PollKeys()
    {
        if (!_watchKeys)
        {
            return;
        }

        var caps = IsCapsLockOn();
        if (caps != _capsLock)
        {
            _capsLock = caps;
            CapsLockChanged?.Invoke(caps);
        }

        ReadLayout(out var window, out var layout);
        if (layout != IntPtr.Zero && layout != _layout)
        {
            // Only a change inside the same window is a real switch, not just moving to another app.
            var sameWindow = window == _layoutWindow;
            _layout = layout;
            _layoutWindow = window;
            if (sameWindow)
            {
                var langId = (int)(layout.ToInt64() & 0xFFFF);
                try
                {
                    KeyboardLayoutChanged?.Invoke(CultureInfo.GetCultureInfo(langId));
                }
                catch (CultureNotFoundException)
                {
                }
            }
        }
        else
        {
            _layoutWindow = window;
        }
    }

    private static void ReadLayout(out IntPtr window, out IntPtr layout)
    {
        window = NativeMethods.GetForegroundWindow();
        var thread = NativeMethods.GetWindowThreadProcessId(window, out _);
        layout = thread == 0 ? IntPtr.Zero : NativeMethods.GetKeyboardLayout(thread);
    }

    private void PollSlow()
    {
        IdleTime = TimeSpan.FromMilliseconds(NativeMethods.GetIdleMilliseconds());
        var fullscreen = DetectFullscreen();
        if (fullscreen != IsFullscreen)
        {
            IsFullscreen = fullscreen;
            FullscreenChanged?.Invoke(fullscreen);
        }

        Tick?.Invoke();
    }

    /// <summary>
    /// True when a game, video or slideshow covers the display the notch is on.
    /// Exclusive full-screen (D3D) and presentation mode hide the notch on every display.
    /// </summary>
    private bool DetectFullscreen()
    {
        try
        {
            if (NativeMethods.SHQueryUserNotificationState(out var state) == 0 &&
                state is NativeMethods.QUNS_RUNNING_D3D_FULL_SCREEN or NativeMethods.QUNS_PRESENTATION_MODE)
            {
                return true;
            }

            var fg = NativeMethods.GetForegroundWindow();
            if (fg == IntPtr.Zero || fg == _notchWindow() || fg == NativeMethods.GetShellWindow())
            {
                return false;
            }

            if (!NativeMethods.IsWindowVisible(fg) || NativeMethods.IsIconic(fg))
            {
                return false;
            }

            var cls = NativeMethods.GetClassName(fg);
            if (cls is "Progman" or "WorkerW" or "Shell_TrayWnd" or "Shell_SecondaryTrayWnd" or "XamlExplorerHostIslandWindow")
            {
                return false;
            }

            if (NativeMethods.DwmGetWindowAttribute(fg, NativeMethods.DWMWA_CLOAKED, out var cloaked, sizeof(int)) == 0 && cloaked != 0)
            {
                return false;
            }

            var monitor = _notchMonitor();
            if (monitor == IntPtr.Zero || NativeMethods.MonitorFromWindow(fg, NativeMethods.MONITOR_DEFAULTTONEAREST) != monitor)
            {
                return false;
            }

            var info = new NativeMethods.MONITORINFOEX { cbSize = System.Runtime.InteropServices.Marshal.SizeOf<NativeMethods.MONITORINFOEX>(), szDevice = "" };
            if (!NativeMethods.GetMonitorInfo(monitor, ref info) || !NativeMethods.GetWindowRect(fg, out var rect))
            {
                return false;
            }

            var m = info.rcMonitor;
            var covers = rect.Left <= m.Left && rect.Top <= m.Top && rect.Right >= m.Right && rect.Bottom >= m.Bottom;
            if (!covers)
            {
                return false;
            }

            // A maximized window with a normal title bar (auto-hidden taskbar) is not "full screen".
            var hasCaption = (NativeMethods.GetStyle(fg) & NativeMethods.WS_CAPTION) == NativeMethods.WS_CAPTION;
            return !hasCaption;
        }
        catch (Exception ex)
        {
            Log.Warn("Full-screen check failed", ex);
            return false;
        }
    }

    private void OnPowerEvent(object? sender, object e) => _dispatcher.BeginInvoke(() =>
    {
        try
        {
            var next = ReadPower();
            var previous = _power ?? next;
            _power = next;
            if (previous != next)
            {
                PowerChanged?.Invoke(previous, next);
            }
        }
        catch (Exception ex)
        {
            Log.Warn("Could not read power state", ex);
        }
    });

    private static PowerState ReadPower()
    {
        var battery = PowerManager.BatteryStatus;
        var hasBattery = battery != BatteryStatus.NotPresent;
        var plugged = PowerManager.PowerSupplyStatus != PowerSupplyStatus.NotPresent;
        var percent = hasBattery ? MathEx.Clamp(PowerManager.RemainingChargePercent, 0, 100) : 100;
        return new PowerState(hasBattery, plugged, percent, battery == BatteryStatus.Charging);
    }

    private void OnNetworkStatusChanged(object sender) =>
        _dispatcher.BeginInvoke(() => _networkDebounce.Run(() =>
        {
            try
            {
                var next = ReadNetwork();
                if (_network is null || _network.Connected != next.Connected || (next.Connected && _network.Name != next.Name))
                {
                    _network = next;
                    NetworkChanged?.Invoke(next);
                }
            }
            catch (Exception ex)
            {
                Log.Warn("Could not read network state", ex);
            }
        }));

    private static NetworkState ReadNetwork()
    {
        var profile = NetworkInformation.GetInternetConnectionProfile();
        if (profile is null)
        {
            return new NetworkState(false, "", false);
        }

        var connected = profile.GetNetworkConnectivityLevel() == NetworkConnectivityLevel.InternetAccess;
        var isWifi = profile.IsWlanConnectionProfile;
        string name;
        try
        {
            name = isWifi ? profile.WlanConnectionProfileDetails?.GetConnectedSsid() ?? profile.ProfileName : profile.ProfileName;
        }
        catch
        {
            name = profile.ProfileName ?? "";
        }

        return new NetworkState(connected, name ?? "", isWifi);
    }
}
