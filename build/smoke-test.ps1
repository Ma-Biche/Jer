<#
.SYNOPSIS
  Launches Jer on a Windows machine, drives it with the mouse and saves screenshots.
  Used by CI to prove the app starts, renders and stays alive. Uses demo mode, so it never touches real data.
#>
param(
    [string]$Exe = "publish/win-x64/Jer.exe",
    [string]$OutDir = "screenshots"
)

$ErrorActionPreference = "Stop"
$Exe = (Resolve-Path $Exe).Path
New-Item -ItemType Directory -Force -Path $OutDir | Out-Null
$OutDir = (Resolve-Path $OutDir).Path
$DataDir = Join-Path $env:APPDATA "Jer"

Add-Type -AssemblyName System.Windows.Forms, System.Drawing
Add-Type @"
using System;
using System.Runtime.InteropServices;
public static class JerInput {
    [DllImport("user32.dll")] public static extern bool SetCursorPos(int x, int y);
    [DllImport("user32.dll")] public static extern void mouse_event(uint flags, int dx, int dy, int data, UIntPtr extra);
    [DllImport("user32.dll")] public static extern bool SetProcessDPIAware();
    [DllImport("user32.dll")] public static extern void keybd_event(byte vk, byte scan, uint flags, UIntPtr extra);
    public static void TapCapsLock() {
        keybd_event(0x14, 0x45, 0x1, UIntPtr.Zero);
        keybd_event(0x14, 0x45, 0x3, UIntPtr.Zero);
    }
}
"@
[JerInput]::SetProcessDPIAware() | Out-Null

$screen = [System.Windows.Forms.Screen]::PrimaryScreen.Bounds
Write-Host "Screen: $($screen.Width) x $($screen.Height)"

function Save-Shot([string]$name) {
    $bmp = New-Object System.Drawing.Bitmap $screen.Width, $screen.Height
    $g = [System.Drawing.Graphics]::FromImage($bmp)
    # With desktop composition the screen copy includes layered (transparent) windows such as the notch.
    $g.CopyFromScreen($screen.Location, [System.Drawing.Point]::Empty, $screen.Size, [System.Drawing.CopyPixelOperation]::SourceCopy)
    $bmp.Save((Join-Path $OutDir "$name.png"), [System.Drawing.Imaging.ImageFormat]::Png)
    $g.Dispose()
    $bmp.Dispose()
    Write-Host "Saved $name.png"
}

function Move-Mouse([int]$x, [int]$y) {
    [JerInput]::SetCursorPos($x, $y) | Out-Null
    [JerInput]::mouse_event(0x0001, 1, 0, 0, [UIntPtr]::Zero)
    Start-Sleep -Milliseconds 60
    [JerInput]::mouse_event(0x0001, -1, 0, 0, [UIntPtr]::Zero)
}

function Scroll-Wheel([int]$delta) {
    [JerInput]::mouse_event(0x0800, 0, 0, $delta, [UIntPtr]::Zero)
}

function Stop-Jer {
    Get-Process Jer -ErrorAction SilentlyContinue | Stop-Process -Force
    Start-Sleep -Milliseconds 900
}

function Start-Jer([string]$arguments) {
    Stop-Jer
    Start-Process -FilePath $Exe -ArgumentList $arguments | Out-Null
    Start-Sleep -Seconds 5
    if (-not (Get-Process Jer -ErrorAction SilentlyContinue)) { throw "Jer exited right after start ($arguments)" }
}

function Assert-Alive([string]$step) {
    if (-not (Get-Process Jer -ErrorAction SilentlyContinue)) { throw "Jer is not running after: $step" }
}

function Write-Settings([hashtable]$values) {
    New-Item -ItemType Directory -Force -Path $DataDir | Out-Null
    $values | ConvertTo-Json | Set-Content -Path (Join-Path $DataDir "settings.json") -Encoding UTF8
}

function Write-Diagnostics {
    Write-Host "----- diagnostics -----"
    $log = Join-Path $DataDir "jer.log"
    if (Test-Path $log) { Write-Host "jer.log:"; Get-Content $log | Write-Host } else { Write-Host "(no jer.log)" }
    foreach ($provider in @(".NET Runtime", "Application Error")) {
        try {
            Get-WinEvent -FilterHashtable @{ LogName = "Application"; ProviderName = $provider; StartTime = $script:StartedAt } -MaxEvents 4 -ErrorAction Stop |
                ForEach-Object { Write-Host "[$provider] $($_.TimeCreated)"; Write-Host $_.Message }
        } catch { Write-Host "(no $provider events)" }
    }
    try { Save-Shot "99-failure" } catch { }
}

$script:StartedAt = Get-Date
try {
# Show the desktop so screenshots have a clean background.
(New-Object -ComObject Shell.Application).MinimizeAll()
Start-Sleep -Seconds 1
Move-Mouse ([int]($screen.Width / 2)) ([int]($screen.Height / 2))

# 1. First run shows the welcome alert.
Remove-Item -Recurse -Force $DataDir -ErrorAction SilentlyContinue
Start-Jer "--demo"
Save-Shot "01-first-run-alert"

# 2. Closed notch with music playing and the lyrics pill.
Write-Settings @{ FirstRunDone = $true; LyricsEnabled = $true; StartWithWindows = $false }
Start-Jer "--demo"
Save-Shot "02-closed-music"

# 3. Hover to open, then scroll through the pages.
Move-Mouse ([int]($screen.Width / 2)) 8
Start-Sleep -Milliseconds 1500
Save-Shot "03-open-music-lyrics"
$pages = @("04-open-pomodoro", "05-open-water", "06-open-shelf", "07-open-todo")
foreach ($page in $pages) {
    Scroll-Wheel -120
    Start-Sleep -Milliseconds 900
    Save-Shot $page
}
Assert-Alive "scrolling pages"

# 4. Move away: the notch closes again.
Move-Mouse ([int]($screen.Width / 2)) ([int]($screen.Height / 2))
Start-Sleep -Milliseconds 1500
Save-Shot "08-closed-again"

# 5. Pinned pages (independent of mouse simulation).
Write-Settings @{ FirstRunDone = $true; LyricsEnabled = $false; StartWithWindows = $false }
foreach ($page in @("Music", "Pomodoro", "Water", "Shelf", "Todo")) {
    Start-Jer "--demo --pin $page"
    Save-Shot ("10-pinned-" + $page.ToLower())
}

# 6. Minimal notch.
Write-Settings @{ FirstRunDone = $true; MinimalNotch = $true; StartWithWindows = $false }
Start-Jer "--demo"
Save-Shot "11-minimal"

# 7. Settings window pages.
Write-Settings @{ FirstRunDone = $true; StartWithWindows = $false }
foreach ($tab in @("General", "Display", "Pages", "Water", "About")) {
    Start-Jer "--demo --settings $tab"
    Save-Shot ("20-settings-" + $tab.ToLower())
}

# 8a. Mascot: nothing playing and no input for a while, then a hello when the mouse moves.
Write-Settings @{ FirstRunDone = $true; StartWithWindows = $false; MascotDelaySeconds = 10 }
Start-Jer "--demo --paused"
Start-Sleep -Seconds 9
Save-Shot "30-mascot"
Move-Mouse ([int]($screen.Width / 2) + 40) ([int]($screen.Height / 2))
Start-Sleep -Milliseconds 1300
Save-Shot "31-mascot-hello"
Assert-Alive "mascot"

# 8b. Caps Lock overlay.
[JerInput]::TapCapsLock()
Start-Sleep -Milliseconds 700
Save-Shot "32-hud-capslock"
[JerInput]::TapCapsLock()
Start-Sleep -Milliseconds 300

# 8c. Starting Jer again while it runs opens Settings in the running copy.
Start-Jer "--demo"
Start-Process -FilePath $Exe | Out-Null
Start-Sleep -Seconds 3
Save-Shot "21-second-launch-opens-settings"
Assert-Alive "second launch"
Stop-Jer

# The log must not contain errors.
$log = Join-Path $DataDir "jer.log"
if (Test-Path $log) {
    Write-Host "----- jer.log -----"
    Get-Content $log | Write-Host
    $errors = Select-String -Path $log -Pattern "\[ERROR\]"
    if ($errors) { throw "Jer logged errors during the smoke test." }
}

Write-Host "Smoke test passed."
}
catch {
    Write-Diagnostics
    throw
}
