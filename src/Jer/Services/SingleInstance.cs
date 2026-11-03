using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using Jer.Core;
using Jer.Interop;

namespace Jer.Services;

/// <summary>What happened when Jer tried to become the one running copy.</summary>
public enum InstanceResult
{
    /// <summary>No other Jer was running.</summary>
    First,

    /// <summary>An older (or another) copy was running and has been closed, so this one takes over.</summary>
    ReplacedOther,

    /// <summary>The same or a newer Jer is already running; it was asked to show its Settings.</summary>
    HandedOff,

    /// <summary>Another Jer is running and couldn't be closed.</summary>
    Blocked,
}

/// <summary>
/// Makes sure only one Jer runs. Starting Jer again opens the Settings of the running copy;
/// starting a newer Jer.exe (or a fresh download) closes the old one and takes its place,
/// so an update never leaves the old version running in the background.
/// </summary>
public sealed class SingleInstance : IDisposable
{
    // The same names as Jer 1.x, so old and new versions see each other.
    private const string MutexName = @"Local\Jer.SingleInstance.7f3c2a9e";
    private const string ShowSettingsEventName = @"Local\Jer.ShowSettings.7f3c2a9e";
    private const string QuitEventName = @"Local\Jer.Quit.7f3c2a9e";

    private readonly Mutex _mutex;
    private readonly EventWaitHandle _showSettings;
    private readonly EventWaitHandle _quit;
    private readonly List<RegisteredWaitHandle> _waits = new();
    private bool _owned;

    private SingleInstance(Mutex mutex, bool owned)
    {
        _mutex = mutex;
        _owned = owned;
        _showSettings = new EventWaitHandle(false, EventResetMode.AutoReset, ShowSettingsEventName);
        _quit = new EventWaitHandle(false, EventResetMode.AutoReset, QuitEventName);
    }

    /// <summary>Version of the copy that was replaced, when <see cref="InstanceResult.ReplacedOther"/>.</summary>
    public Version? ReplacedVersion { get; private set; }

    public static SingleInstance Acquire(bool quietIfRunning, out InstanceResult result)
    {
        var mutex = new Mutex(initiallyOwned: true, MutexName, out var createdNew);
        var instance = new SingleInstance(mutex, createdNew);
        if (createdNew)
        {
            result = InstanceResult.First;
            return instance;
        }

        result = instance.HandleRunningCopy(quietIfRunning);
        return instance;
    }

    /// <summary>Calls <paramref name="showSettings"/> or <paramref name="quit"/> (on a thread-pool thread) when another copy asks.</summary>
    public void Listen(Action showSettings, Action quit)
    {
        _waits.Add(ThreadPool.RegisterWaitForSingleObject(_showSettings, (_, _) => showSettings(), null, Timeout.Infinite, executeOnlyOnce: false));
        _waits.Add(ThreadPool.RegisterWaitForSingleObject(_quit, (_, _) => quit(), null, Timeout.Infinite, executeOnlyOnce: true));
    }

    public void Dispose()
    {
        foreach (var wait in _waits)
        {
            wait.Unregister(null);
        }

        _waits.Clear();
        if (_owned)
        {
            try
            {
                _mutex.ReleaseMutex();
            }
            catch (ApplicationException)
            {
                // Not owned by this thread any more; the OS releases it when the process exits.
            }

            _owned = false;
        }

        _mutex.Dispose();
        _showSettings.Dispose();
        _quit.Dispose();
    }

    private InstanceResult HandleRunningCopy(bool quietIfRunning)
    {
        if (quietIfRunning)
        {
            return InstanceResult.HandedOff; // Started at sign-in while Jer already runs: nothing to do.
        }

        var self = AppInfo.ExecutablePath;
        var others = FindOtherCopies();
        var sameExe = others.Any(o => string.Equals(o.Path, self, StringComparison.OrdinalIgnoreCase));
        var newer = others.Any(o => o.Version > AppInfo.Version);
        if (others.Count == 0 || sameExe || newer)
        {
            // Same Jer.exe (or a newer version): just bring up its Settings.
            AllowSetForegroundWindow(-1); // Lets the running copy bring its Settings window to the front.
            _showSettings.Set();
            return InstanceResult.HandedOff;
        }

        ReplacedVersion = others.Max(o => o.Version);
        Log.Info($"Replacing the running Jer {ReplacedVersion} ({string.Join(", ", others.Select(o => o.Path))})");

        // Jer 2.0 and later close themselves when asked.
        _quit.Set();
        if (WaitForMutex(TimeSpan.FromSeconds(5)))
        {
            return InstanceResult.ReplacedOther;
        }

        // Jer 1.x doesn't know that signal: end it.
        foreach (var other in others)
        {
            try
            {
                using var process = Process.GetProcessById(other.ProcessId);
                process.Kill();
                process.WaitForExit(3000);
            }
            catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or System.ComponentModel.Win32Exception)
            {
                Log.Warn($"Could not close Jer (process {other.ProcessId})", ex);
            }
        }

        return WaitForMutex(TimeSpan.FromSeconds(5)) ? InstanceResult.ReplacedOther : InstanceResult.Blocked;
    }

    private bool WaitForMutex(TimeSpan timeout)
    {
        try
        {
            _owned = _mutex.WaitOne(timeout);
        }
        catch (AbandonedMutexException)
        {
            _owned = true; // The previous owner ended without releasing it; it's ours now.
        }

        return _owned;
    }

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool AllowSetForegroundWindow(int dwProcessId);

    private sealed record RunningCopy(int ProcessId, string Path, Version Version);

    /// <summary>Other running Jer processes (any file name starting with "Jer", identified by the exe's product name).</summary>
    private static List<RunningCopy> FindOtherCopies()
    {
        var result = new List<RunningCopy>();
        int selfId;
        using (var current = Process.GetCurrentProcess())
        {
            selfId = current.Id;
        }

        foreach (var process in Process.GetProcesses())
        {
            using (process)
            {
                try
                {
                    if (process.Id == selfId || !process.ProcessName.StartsWith("Jer", StringComparison.OrdinalIgnoreCase))
                    {
                        continue;
                    }

                    var path = NativeMethods.GetProcessImagePath(process.Id);
                    if (path is null || !File.Exists(path))
                    {
                        continue;
                    }

                    var info = FileVersionInfo.GetVersionInfo(path);
                    if (!string.Equals(info.ProductName?.Trim(), "Jer", StringComparison.OrdinalIgnoreCase))
                    {
                        continue;
                    }

                    var version = new Version(Math.Max(0, info.FileMajorPart), Math.Max(0, info.FileMinorPart), Math.Max(0, info.FileBuildPart));
                    result.Add(new RunningCopy(process.Id, path, version));
                }
                catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception or IOException)
                {
                    // The process exited while we looked at it, or isn't ours to inspect.
                }
            }
        }

        return result;
    }
}
