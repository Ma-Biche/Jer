using System;
using Jer.Core;
using Microsoft.Win32;

namespace Jer.Services;

/// <summary>"Start with Windows", via the per-user Run key (no admin rights needed).</summary>
public static class StartupService
{
    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string ApprovedKey = @"Software\Microsoft\Windows\CurrentVersion\Explorer\StartupApproved\Run";
    private const string ValueName = "Jer";

    private static string Command => $"\"{Environment.ProcessPath}\" --startup";

    /// <summary>True when Jer is registered and not disabled in Task Manager's Startup tab.</summary>
    public static bool IsEnabled()
    {
        try
        {
            using var run = Registry.CurrentUser.OpenSubKey(RunKey);
            if (run?.GetValue(ValueName) is not string)
            {
                return false;
            }

            using var approved = Registry.CurrentUser.OpenSubKey(ApprovedKey);
            // Task Manager stores 12 bytes; an odd first byte means "disabled".
            return approved?.GetValue(ValueName) is not byte[] { Length: > 0 } state || (state[0] & 1) == 0;
        }
        catch (Exception ex)
        {
            Log.Warn("Could not read the startup setting", ex);
            return false;
        }
    }

    public static void SetEnabled(bool enabled)
    {
        try
        {
            using var run = Registry.CurrentUser.CreateSubKey(RunKey);
            if (enabled)
            {
                run.SetValue(ValueName, Command);
                using var approved = Registry.CurrentUser.OpenSubKey(ApprovedKey, writable: true);
                approved?.DeleteValue(ValueName, throwOnMissingValue: false);
            }
            else
            {
                run.DeleteValue(ValueName, throwOnMissingValue: false);
            }
        }
        catch (Exception ex)
        {
            Log.Warn("Could not change the startup setting", ex);
        }
    }

    /// <summary>Keeps the registered path current if the user moved Jer.exe.</summary>
    public static void RefreshPathIfEnabled()
    {
        try
        {
            using var run = Registry.CurrentUser.OpenSubKey(RunKey, writable: true);
            if (run?.GetValue(ValueName) is string current && !string.Equals(current, Command, StringComparison.OrdinalIgnoreCase))
            {
                run.SetValue(ValueName, Command);
            }
        }
        catch (Exception ex)
        {
            Log.Warn("Could not refresh the startup path", ex);
        }
    }
}
