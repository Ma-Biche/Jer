using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using Jer.Core;

namespace Jer.Interop;

/// <summary>Reads monitor names ("DELL U2720Q") and stable device paths through the Display Configuration API.</summary>
internal static class DisplayConfig
{
    public sealed record MonitorName(string FriendlyName, string DevicePath, bool IsInternal);

    private const uint QDC_ONLY_ACTIVE_PATHS = 0x00000002;
    private const int ERROR_SUCCESS = 0;
    private const int ERROR_INSUFFICIENT_BUFFER = 122;
    private const uint DISPLAYCONFIG_DEVICE_INFO_GET_SOURCE_NAME = 1;
    private const uint DISPLAYCONFIG_DEVICE_INFO_GET_TARGET_NAME = 2;

    [StructLayout(LayoutKind.Sequential)]
    private struct LUID
    {
        public uint LowPart;
        public int HighPart;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct DISPLAYCONFIG_PATH_SOURCE_INFO
    {
        public LUID adapterId;
        public uint id;
        public uint modeInfoIdx;
        public uint statusFlags;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct DISPLAYCONFIG_RATIONAL
    {
        public uint Numerator;
        public uint Denominator;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct DISPLAYCONFIG_PATH_TARGET_INFO
    {
        public LUID adapterId;
        public uint id;
        public uint modeInfoIdx;
        public uint outputTechnology;
        public uint rotation;
        public uint scaling;
        public DISPLAYCONFIG_RATIONAL refreshRate;
        public uint scanLineOrdering;
        public int targetAvailable;
        public uint statusFlags;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct DISPLAYCONFIG_PATH_INFO
    {
        public DISPLAYCONFIG_PATH_SOURCE_INFO sourceInfo;
        public DISPLAYCONFIG_PATH_TARGET_INFO targetInfo;
        public uint flags;
    }

    // Only the size matters: Jer never reads the mode union.
    [StructLayout(LayoutKind.Sequential, Size = 64)]
    private struct DISPLAYCONFIG_MODE_INFO
    {
        public uint infoType;
        public uint id;
        public LUID adapterId;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct DISPLAYCONFIG_DEVICE_INFO_HEADER
    {
        public uint type;
        public uint size;
        public LUID adapterId;
        public uint id;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct DISPLAYCONFIG_SOURCE_DEVICE_NAME
    {
        public DISPLAYCONFIG_DEVICE_INFO_HEADER header;

        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)]
        public string viewGdiDeviceName;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct DISPLAYCONFIG_TARGET_DEVICE_NAME
    {
        public DISPLAYCONFIG_DEVICE_INFO_HEADER header;
        public uint flags;
        public uint outputTechnology;
        public ushort edidManufactureId;
        public ushort edidProductCodeId;
        public uint connectorInstance;

        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 64)]
        public string monitorFriendlyDeviceName;

        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)]
        public string monitorDevicePath;
    }

    [DllImport("user32.dll")]
    private static extern int GetDisplayConfigBufferSizes(uint flags, out uint numPathArrayElements, out uint numModeInfoArrayElements);

    [DllImport("user32.dll")]
    private static extern int QueryDisplayConfig(
        uint flags,
        ref uint numPathArrayElements,
        [Out] DISPLAYCONFIG_PATH_INFO[] pathArray,
        ref uint numModeInfoArrayElements,
        [Out] DISPLAYCONFIG_MODE_INFO[] modeInfoArray,
        IntPtr currentTopologyId);

    [DllImport("user32.dll")]
    private static extern int DisplayConfigGetDeviceInfo(ref DISPLAYCONFIG_SOURCE_DEVICE_NAME requestPacket);

    [DllImport("user32.dll")]
    private static extern int DisplayConfigGetDeviceInfo(ref DISPLAYCONFIG_TARGET_DEVICE_NAME requestPacket);

    /// <summary>Maps GDI device names (\\.\DISPLAY1) to monitor names and device paths.</summary>
    public static Dictionary<string, MonitorName> GetMonitorNames()
    {
        var result = new Dictionary<string, MonitorName>(StringComparer.OrdinalIgnoreCase);
        try
        {
            for (var attempt = 0; attempt < 3; attempt++)
            {
                if (GetDisplayConfigBufferSizes(QDC_ONLY_ACTIVE_PATHS, out var pathCount, out var modeCount) != ERROR_SUCCESS)
                {
                    return result;
                }

                var paths = new DISPLAYCONFIG_PATH_INFO[pathCount];
                var modes = new DISPLAYCONFIG_MODE_INFO[modeCount];
                var rc = QueryDisplayConfig(QDC_ONLY_ACTIVE_PATHS, ref pathCount, paths, ref modeCount, modes, IntPtr.Zero);
                if (rc == ERROR_INSUFFICIENT_BUFFER)
                {
                    continue; // The topology changed between the two calls.
                }

                if (rc != ERROR_SUCCESS)
                {
                    return result;
                }

                for (var i = 0; i < pathCount; i++)
                {
                    var source = new DISPLAYCONFIG_SOURCE_DEVICE_NAME
                    {
                        header = new DISPLAYCONFIG_DEVICE_INFO_HEADER
                        {
                            type = DISPLAYCONFIG_DEVICE_INFO_GET_SOURCE_NAME,
                            size = (uint)Marshal.SizeOf<DISPLAYCONFIG_SOURCE_DEVICE_NAME>(),
                            adapterId = paths[i].sourceInfo.adapterId,
                            id = paths[i].sourceInfo.id,
                        },
                        viewGdiDeviceName = "",
                    };
                    if (DisplayConfigGetDeviceInfo(ref source) != ERROR_SUCCESS)
                    {
                        continue;
                    }

                    var target = new DISPLAYCONFIG_TARGET_DEVICE_NAME
                    {
                        header = new DISPLAYCONFIG_DEVICE_INFO_HEADER
                        {
                            type = DISPLAYCONFIG_DEVICE_INFO_GET_TARGET_NAME,
                            size = (uint)Marshal.SizeOf<DISPLAYCONFIG_TARGET_DEVICE_NAME>(),
                            adapterId = paths[i].targetInfo.adapterId,
                            id = paths[i].targetInfo.id,
                        },
                        monitorFriendlyDeviceName = "",
                        monitorDevicePath = "",
                    };
                    if (DisplayConfigGetDeviceInfo(ref target) != ERROR_SUCCESS)
                    {
                        continue;
                    }

                    // INTERNAL, LVDS, embedded DisplayPort, embedded UDI
                    var isInternal = target.outputTechnology is 0x80000000 or 6 or 11 or 13;
                    var friendly = string.IsNullOrWhiteSpace(target.monitorFriendlyDeviceName)
                        ? (isInternal ? "Built-in display" : "Display")
                        : target.monitorFriendlyDeviceName.Trim();
                    if (!result.ContainsKey(source.viewGdiDeviceName))
                    {
                        result[source.viewGdiDeviceName] = new MonitorName(friendly, target.monitorDevicePath ?? "", isInternal);
                    }
                }

                return result;
            }
        }
        catch (Exception ex)
        {
            Log.Warn("Could not read monitor names", ex);
        }

        return result;
    }
}
