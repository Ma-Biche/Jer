using System;
using System.Runtime.InteropServices;
using System.Windows.Threading;
using Jer.Core;
using Jer.Interop;

namespace Jer.Services;

public sealed class VolumeChangedEventArgs : EventArgs
{
    public VolumeChangedEventArgs(float level, bool muted, bool fromJer)
    {
        Level = level;
        IsMuted = muted;
        FromJer = fromJer;
    }

    public float Level { get; }
    public bool IsMuted { get; }

    /// <summary>True when the change came from Jer's own slider.</summary>
    public bool FromJer { get; }
}

/// <summary>Watches and sets the master volume of the default playback device (Core Audio).</summary>
public sealed class VolumeService : IDisposable
{
    private const uint CLSCTX_ALL = 23;
    private static readonly Guid IID_IAudioEndpointVolume = new("5CDF2C82-841E-4546-9722-0CF74078229A");

    private readonly Dispatcher _dispatcher;
    private readonly IMMDeviceEnumerator _enumerator;
    private readonly VolumeCallback _callback;
    private readonly DeviceCallback _deviceCallback;
    private Guid _context = Guid.NewGuid();
    private IAudioEndpointVolume? _endpoint;
    private bool _disposed;

    private VolumeService(Dispatcher dispatcher)
    {
        _dispatcher = dispatcher;
        _enumerator = (IMMDeviceEnumerator)new MMDeviceEnumeratorComObject();
        _callback = new VolumeCallback(this);
        _deviceCallback = new DeviceCallback(this);
        _enumerator.RegisterEndpointNotificationCallback(_deviceCallback);
        AttachDefaultDevice();
    }

    /// <summary>Raised on the UI thread.</summary>
    public event EventHandler<VolumeChangedEventArgs>? Changed;

    public float Level { get; private set; }
    public bool IsMuted { get; private set; }
    public bool IsAvailable => _endpoint is not null;

    public static VolumeService? TryCreate(Dispatcher dispatcher)
    {
        try
        {
            return new VolumeService(dispatcher);
        }
        catch (Exception ex)
        {
            Log.Warn("Volume control is not available", ex);
            return null;
        }
    }

    public void SetLevel(float level)
    {
        level = Math.Clamp(level, 0f, 1f);
        if (_endpoint is null)
        {
            return;
        }

        _endpoint.SetMasterVolumeLevelScalar(level, ref _context);
        if (IsMuted && level > 0)
        {
            _endpoint.SetMute(false, ref _context);
        }

        Level = level;
    }

    public void ToggleMute()
    {
        if (_endpoint is null)
        {
            return;
        }

        _endpoint.SetMute(!IsMuted, ref _context);
        IsMuted = !IsMuted;
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        DetachDevice();
        try
        {
            _enumerator.UnregisterEndpointNotificationCallback(_deviceCallback);
            Marshal.ReleaseComObject(_enumerator);
        }
        catch (Exception ex)
        {
            Log.Warn("Volume cleanup failed", ex);
        }
    }

    private void AttachDefaultDevice()
    {
        DetachDevice();
        if (_disposed)
        {
            return;
        }

        if (_enumerator.GetDefaultAudioEndpoint(EDataFlow.eRender, ERole.eMultimedia, out var device) != 0 || device is null)
        {
            return; // No speakers (for example on a server).
        }

        try
        {
            var iid = IID_IAudioEndpointVolume;
            if (device.Activate(ref iid, CLSCTX_ALL, IntPtr.Zero, out var activated) != 0 || activated is not IAudioEndpointVolume endpoint)
            {
                return;
            }

            endpoint.GetMasterVolumeLevelScalar(out var level);
            endpoint.GetMute(out var muted);
            Level = level;
            IsMuted = muted;
            endpoint.RegisterControlChangeNotify(_callback);
            _endpoint = endpoint;
        }
        finally
        {
            Marshal.ReleaseComObject(device);
        }
    }

    private void DetachDevice()
    {
        if (_endpoint is null)
        {
            return;
        }

        try
        {
            _endpoint.UnregisterControlChangeNotify(_callback);
            Marshal.ReleaseComObject(_endpoint);
        }
        catch (Exception ex)
        {
            Log.Warn("Could not detach the audio device", ex);
        }

        _endpoint = null;
    }

    // Core Audio calls these on its own threads; hop to the UI thread before touching anything.
    private void OnNotify(IntPtr data)
    {
        if (data == IntPtr.Zero)
        {
            return;
        }

        var n = Marshal.PtrToStructure<AUDIO_VOLUME_NOTIFICATION_DATA>(data);
        var fromJer = n.guidEventContext == _context;
        _dispatcher.BeginInvoke(() =>
        {
            Level = n.fMasterVolume;
            IsMuted = n.bMuted != 0;
            Changed?.Invoke(this, new VolumeChangedEventArgs(Level, IsMuted, fromJer));
        });
    }

    private void OnDefaultDeviceChanged() => _dispatcher.BeginInvoke(() =>
    {
        try
        {
            AttachDefaultDevice();
        }
        catch (Exception ex)
        {
            Log.Warn("Could not switch audio device", ex);
        }
    });

    private sealed class VolumeCallback : IAudioEndpointVolumeCallback
    {
        private readonly VolumeService _owner;

        public VolumeCallback(VolumeService owner) => _owner = owner;

        public int OnNotify(IntPtr pNotify)
        {
            try
            {
                _owner.OnNotify(pNotify);
            }
            catch
            {
                // Never throw back into Core Audio.
            }

            return 0;
        }
    }

    private sealed class DeviceCallback : IMMNotificationClient
    {
        private readonly VolumeService _owner;

        public DeviceCallback(VolumeService owner) => _owner = owner;

        public int OnDeviceStateChanged(string pwstrDeviceId, uint dwNewState) => 0;

        public int OnDeviceAdded(string pwstrDeviceId) => 0;

        public int OnDeviceRemoved(string pwstrDeviceId) => 0;

        public int OnDefaultDeviceChanged(EDataFlow flow, ERole role, string? pwstrDefaultDeviceId)
        {
            if (flow == EDataFlow.eRender && role == ERole.eMultimedia)
            {
                _owner.OnDefaultDeviceChanged();
            }

            return 0;
        }

        public int OnPropertyValueChanged(string pwstrDeviceId, PROPERTYKEY key) => 0;
    }
}
