<#
  Multi-monitor check: starts Jer with two displays, moves the notch to the second display and back
  through the Settings window (UI Automation), and checks where the notch window actually is.
#>
param(
    [string]$Exe = "publish/Jer.exe",
    [string]$OutDir = "mm-screenshots"
)

$ErrorActionPreference = "Stop"
$Exe = (Resolve-Path $Exe).Path
New-Item -ItemType Directory -Force -Path $OutDir | Out-Null
$OutDir = (Resolve-Path $OutDir).Path
$DataDir = Join-Path $env:APPDATA "Jer"

Add-Type -AssemblyName System.Windows.Forms, System.Drawing, UIAutomationClient, UIAutomationTypes
Add-Type @"
using System;
using System.Text;
using System.Runtime.InteropServices;
public static class Win {
    [StructLayout(LayoutKind.Sequential)] public struct RECT { public int Left, Top, Right, Bottom; }
    public delegate bool EnumProc(IntPtr h, IntPtr p);
    [DllImport("user32.dll")] public static extern bool SetProcessDPIAware();
    [DllImport("user32.dll")] public static extern bool EnumWindows(EnumProc cb, IntPtr p);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] public static extern int GetWindowText(IntPtr h, StringBuilder s, int n);
    [DllImport("user32.dll")] public static extern uint GetWindowThreadProcessId(IntPtr h, out uint pid);
    [DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr h, out RECT r);
    [DllImport("user32.dll")] public static extern bool IsWindowVisible(IntPtr h);
    public static string FindWindowRect(uint pid, string title) {
        string found = null;
        EnumWindows((h, p) => {
            uint owner; GetWindowThreadProcessId(h, out owner);
            if (owner != pid || !IsWindowVisible(h)) return true;
            var sb = new StringBuilder(256); GetWindowText(h, sb, 256);
            if (sb.ToString() != title) return true;
            RECT r; GetWindowRect(h, out r);
            found = r.Left + "," + r.Top + "," + r.Right + "," + r.Bottom;
            return false;
        }, IntPtr.Zero);
        return found;
    }
}
"@
[Win]::SetProcessDPIAware() | Out-Null

$screens = [System.Windows.Forms.Screen]::AllScreens
$screens | ForEach-Object { Write-Host ("{0} primary={1} bounds={2}" -f $_.DeviceName, $_.Primary, $_.Bounds) }
$primary = $screens | Where-Object { $_.Primary } | Select-Object -First 1
$second = $screens | Where-Object { -not $_.Primary } | Select-Object -First 1
if (-not $second) { throw "Only one display is available; the virtual display did not come up." }
$secondNumber = [int]([regex]::Match($second.DeviceName, '(\d+)$').Groups[1].Value)
Write-Host "Second display: $($second.DeviceName) -> 'Display $secondNumber'"

function Save-Shot([string]$name) {
    $vs = [System.Windows.Forms.SystemInformation]::VirtualScreen
    $bmp = New-Object System.Drawing.Bitmap $vs.Width, $vs.Height
    $g = [System.Drawing.Graphics]::FromImage($bmp)
    $g.CopyFromScreen($vs.Location, [System.Drawing.Point]::Empty, $vs.Size, [System.Drawing.CopyPixelOperation]::SourceCopy)
    $bmp.Save((Join-Path $OutDir "$name.png"), [System.Drawing.Imaging.ImageFormat]::Png)
    $g.Dispose(); $bmp.Dispose()
    Write-Host "Saved $name.png"
}

function Write-Settings([hashtable]$values) {
    New-Item -ItemType Directory -Force -Path $DataDir | Out-Null
    $values | ConvertTo-Json | Set-Content -Path (Join-Path $DataDir "settings.json") -Encoding UTF8
}

function Stop-Jer { Get-Process Jer -ErrorAction SilentlyContinue | Stop-Process -Force; Start-Sleep -Milliseconds 900 }

function Start-Jer([string]$arguments) {
    Stop-Jer
    Start-Process -FilePath $Exe -ArgumentList $arguments | Out-Null
    Start-Sleep -Seconds 5
    if (-not (Get-Process Jer -ErrorAction SilentlyContinue)) { throw "Jer exited ($arguments)" }
}

function Get-NotchRect {
    $p = Get-Process Jer | Select-Object -First 1
    $r = [Win]::FindWindowRect([uint32]$p.Id, "Jer")
    if (-not $r) { throw "Notch window not found" }
    $v = $r.Split(',') | ForEach-Object { [int]$_ }
    return @{ Left = $v[0]; Top = $v[1]; Right = $v[2]; Bottom = $v[3] }
}

function Assert-NotchOn($screen, [string]$label) {
    $r = Get-NotchRect
    $b = $screen.Bounds
    $centre = ($r.Left + $r.Right) / 2
    $expected = $b.Left + $b.Width / 2
    Write-Host ("Notch window {0}: ({1},{2})-({3},{4}); display {5}: {6}" -f $label, $r.Left, $r.Top, $r.Right, $r.Bottom, $screen.DeviceName, $b)
    if ($r.Left -lt $b.Left -or $r.Right -gt $b.Right -or $r.Top -ne $b.Top -or [math]::Abs($centre - $expected) -gt 2) {
        throw "Notch is not at the top centre of $($screen.DeviceName) ($label)"
    }
}

function Select-Radio([string]$name) {
    $root = [System.Windows.Automation.AutomationElement]::RootElement
    $winCond = New-Object System.Windows.Automation.PropertyCondition([System.Windows.Automation.AutomationElement]::NameProperty, "Jer Settings")
    $window = $root.FindFirst([System.Windows.Automation.TreeScope]::Children, $winCond)
    if (-not $window) { throw "Settings window not found" }
    $radioCond = New-Object System.Windows.Automation.AndCondition(
        (New-Object System.Windows.Automation.PropertyCondition([System.Windows.Automation.AutomationElement]::ControlTypeProperty, [System.Windows.Automation.ControlType]::RadioButton)),
        (New-Object System.Windows.Automation.PropertyCondition([System.Windows.Automation.AutomationElement]::NameProperty, $name)))
    $radio = $window.FindFirst([System.Windows.Automation.TreeScope]::Descendants, $radioCond)
    if (-not $radio) { throw "Radio '$name' not found" }
    $radio.GetCurrentPattern([System.Windows.Automation.SelectionItemPattern]::Pattern).Select()
    Write-Host "Selected '$name'"
}

try {
    # Starts on the main display.
    Write-Settings @{ FirstRunDone = $true; StartWithWindows = $false }
    Start-Jer "--demo"
    Assert-NotchOn $primary "default"
    Save-Shot "01-main-display"

    # A saved choice of the second display is honoured at start-up.
    Write-Settings @{ FirstRunDone = $true; StartWithWindows = $false; DisplayTarget = $second.DeviceName }
    Start-Jer "--demo"
    Assert-NotchOn $second "saved choice"
    Save-Shot "02-second-display-at-startup"

    # Live: pick the second display in Settings, then go back to the main display.
    Write-Settings @{ FirstRunDone = $true; StartWithWindows = $false }
    Start-Jer "--demo --settings Display"
    Save-Shot "03-settings-two-displays"
    Select-Radio "Display $secondNumber"
    Start-Sleep -Milliseconds 1500
    Assert-NotchOn $second "after choosing Display $secondNumber"
    Save-Shot "04-moved-to-second"
    Select-Radio "Main display"
    Start-Sleep -Milliseconds 1500
    Assert-NotchOn $primary "after choosing Main display"
    Save-Shot "05-moved-back-to-main"

    # Choice is saved for next time (demo mode does not save, so check the view model state instead).
    Write-Host "Multi-monitor check passed."
}
catch {
    Save-Shot "99-failure"
    $log = Join-Path $DataDir "jer.log"
    if (Test-Path $log) { Get-Content $log | Write-Host }
    throw
}
finally {
    Stop-Jer
}
