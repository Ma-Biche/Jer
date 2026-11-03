<#
.SYNOPSIS
  Launches Jer on a Windows machine, drives it with the mouse and saves screenshots.
  Used by CI to prove the app starts, renders, updates an older copy and stays alive, and to
  measure its memory and idle CPU. Uses demo mode, so it never touches real data.
#>
param(
    [string]$Exe = "dist/Jer.exe",
    [string]$OutDir = "screenshots",
    [string]$OldRelease = "https://github.com/Ma-Biche/Jer/releases/download/v1.0.0/Jer-win-x64.zip"
)

$ErrorActionPreference = "Stop"
$Exe = (Resolve-Path $Exe).Path
New-Item -ItemType Directory -Force -Path $OutDir | Out-Null
$OutDir = (Resolve-Path $OutDir).Path
$DataDir = Join-Path $env:APPDATA "Jer"
$info = (Get-Item $Exe).VersionInfo
$Version = "$($info.FileMajorPart).$($info.FileMinorPart).$($info.FileBuildPart)"
$script:Measurements = @()
Write-Host ("Testing {0} (version {1}, {2:N0} bytes)" -f $Exe, $Version, (Get-Item $Exe).Length)

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

function Get-Jer { @(Get-Process Jer -ErrorAction SilentlyContinue) }

function Stop-Jer {
    Get-Jer | Stop-Process -Force
    Start-Sleep -Milliseconds 900
}

function Start-Jer([string]$arguments, [string]$path = $Exe) {
    Stop-Jer
    Start-Process -FilePath $path -ArgumentList $arguments | Out-Null
    Start-Sleep -Seconds 5
    if ((Get-Jer).Count -eq 0) { throw "Jer exited right after start ($arguments)" }
}

function Assert-Alive([string]$step) {
    if ((Get-Jer).Count -eq 0) { throw "Jer is not running after: $step" }
}

function Assert-OnlyJer([string]$path, [string]$step) {
    $running = Get-Jer
    $paths = ($running | ForEach-Object { $_.Path }) -join ", "
    if ($running.Count -ne 1 -or $running[0].Path -ne $path) {
        throw "After '$step' expected one Jer from $path, found $($running.Count): $paths"
    }
    Write-Host "OK: $step -> one Jer running from $path (process $($running[0].Id))"
    return $running[0]
}

# Settings for each scenario. Jer's version is filled in so the "Jer was updated" notice stays away.
function Write-Settings([hashtable]$values = @{}) {
    $all = @{ FirstRunDone = $true; StartWithWindows = $false; LastVersion = $Version }
    foreach ($key in $values.Keys) { $all[$key] = $values[$key] }
    foreach ($key in @($all.Keys)) { if ($null -eq $all[$key]) { $all.Remove($key) } }
    New-Item -ItemType Directory -Force -Path $DataDir | Out-Null
    $all | ConvertTo-Json | Set-Content -Path (Join-Path $DataDir "settings.json") -Encoding UTF8
}

# Memory as Task Manager shows it, and CPU over a quiet stretch.
function Measure-Jer([string]$label, [string]$path, [int]$seconds = 30, [switch]$KeepActive) {
    $process = Get-Jer | Where-Object { $_.Path -eq $path } | Select-Object -First 1
    if (-not $process) { throw "No Jer from $path to measure" }
    Start-Sleep -Seconds 5
    $process.Refresh()
    $cpuBefore = $process.TotalProcessorTime
    $started = Get-Date
    if ($KeepActive) {
        # Someone is at the PC: a small mouse move every few seconds, away from the notch.
        for ($i = 0; $i -lt $seconds; $i += 5) {
            Move-Mouse ([int]($screen.Width / 2) + ($i % 20)) ([int]($screen.Height / 2))
            Start-Sleep -Seconds 5
        }
    } else {
        Start-Sleep -Seconds $seconds
    }
    $process.Refresh()
    $elapsedMs = ((Get-Date) - $started).TotalMilliseconds
    $cpuMs = ($process.TotalProcessorTime - $cpuBefore).TotalMilliseconds
    $privateWs = $null
    try { $privateWs = (Get-CimInstance Win32_PerfFormattedData_PerfProc_Process -Filter "IDProcess=$($process.Id)").WorkingSetPrivate } catch { }
    $row = [pscustomobject]@{
        Build              = $label
        FileMB             = [math]::Round((Get-Item $path).Length / 1MB, 2)
        TaskManagerMemMB   = if ($privateWs) { [math]::Round($privateWs / 1MB, 1) } else { $null }
        WorkingSetMB       = [math]::Round($process.WorkingSet64 / 1MB, 1)
        PrivateBytesMB     = [math]::Round($process.PrivateMemorySize64 / 1MB, 1)
        CpuPercentOfOneCore = [math]::Round($cpuMs / $elapsedMs * 100, 2)
    }
    $script:Measurements += $row
    Write-Host ($row | Format-List | Out-String)
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
Write-Settings @{ LyricsEnabled = $true }
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
Write-Settings @{ LyricsEnabled = $false }
foreach ($page in @("Music", "Pomodoro", "Water", "Shelf", "Todo")) {
    Start-Jer "--demo --pin $page"
    Save-Shot ("10-pinned-" + $page.ToLower())
}

# 6. Minimal notch.
Write-Settings @{ MinimalNotch = $true }
Start-Jer "--demo"
Save-Shot "11-minimal"

# 7. Settings window pages.
Write-Settings
foreach ($tab in @("General", "Display", "Pages", "Water", "About")) {
    Start-Jer "--demo --settings $tab"
    Save-Shot ("20-settings-" + $tab.ToLower())
}

# 8a. Mascot: nothing playing and no input for a while, then a hello when the mouse moves.
Write-Settings @{ MascotDelaySeconds = 10 }
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

# 8c. Starting the same Jer.exe again while it runs opens Settings in the running copy.
Write-Settings
Start-Jer "--demo"
$first = Assert-OnlyJer $Exe "first start"
Start-Process -FilePath $Exe | Out-Null
Start-Sleep -Seconds 3
Save-Shot "21-second-launch-opens-settings"
$still = Assert-OnlyJer $Exe "second launch of the same exe"
if ($still.Id -ne $first.Id) { throw "The second launch replaced the running Jer instead of opening its Settings" }
Stop-Jer

# 9. Updating: a newer Jer.exe replaces the copy that is running.
$oldExe = $null
try {
    $oldDir = Join-Path $env:TEMP "jer-v1"
    Remove-Item -Recurse -Force $oldDir -ErrorAction SilentlyContinue
    New-Item -ItemType Directory -Force -Path $oldDir | Out-Null
    Invoke-WebRequest $OldRelease -OutFile (Join-Path $oldDir "old.zip")
    Expand-Archive (Join-Path $oldDir "old.zip") -DestinationPath $oldDir -Force
    $oldExe = (Resolve-Path (Join-Path $oldDir "Jer.exe")).Path
} catch {
    Write-Warning "Could not download the previous release ($OldRelease); skipping the update-from-1.0 checks. $_"
}

if ($oldExe) {
    # Jer 1.0 never wrote LastVersion; the new Jer should say it was updated.
    # (Jer 1.0's demo always plays music, so it is measured in that state.)
    Write-Settings @{ LastVersion = $null; MascotEnabled = $false; LyricsEnabled = $true }
    Start-Jer "--demo" $oldExe
    Assert-OnlyJer $oldExe "start Jer 1.0" | Out-Null
    Measure-Jer "Jer 1.0.0 (old), music playing" $oldExe -KeepActive
    Start-Process -FilePath $Exe -ArgumentList "--demo --paused" | Out-Null
    Start-Sleep -Seconds 8
    Assert-OnlyJer $Exe "start Jer $Version while 1.0 runs" | Out-Null
    Save-Shot "40-updated-from-1.0"
    Stop-Jer
}

# 10. A fresh download of the same version (another folder) also takes over, and asks the old one to close.
Write-Settings @{ MascotEnabled = $false }
Start-Jer "--demo --paused"
$copyDir = Join-Path $env:TEMP "jer-copy"
New-Item -ItemType Directory -Force -Path $copyDir | Out-Null
$copyExe = Join-Path $copyDir "Jer.exe"
Copy-Item $Exe $copyExe -Force
Start-Process -FilePath $copyExe -ArgumentList "--demo --paused" | Out-Null
Start-Sleep -Seconds 6
Assert-OnlyJer $copyExe "start a second copy of Jer $Version" | Out-Null
if (-not (Select-String -Path (Join-Path $DataDir "jer.log") -Pattern "A newer Jer started; closing this one" -Quiet)) {
    throw "The running Jer was not asked to close"
}
Stop-Jer

# 11. Memory and idle CPU of this build.
Write-Settings @{ MascotEnabled = $false }
Start-Jer "--demo --paused"
Measure-Jer "Jer $Version, idle" $Exe
Write-Settings @{ MascotEnabled = $false; LyricsEnabled = $true }
Start-Jer "--demo"
Measure-Jer "Jer $Version, music playing" $Exe -KeepActive
Stop-Jer

$table = $script:Measurements | Format-Table -AutoSize | Out-String -Width 200
Write-Host "----- measurements -----"
Write-Host $table
$table | Set-Content (Join-Path $OutDir "measurements.txt")
if ($env:GITHUB_STEP_SUMMARY) {
    $md = @("| Build | Jer.exe (MB) | Memory, Task Manager (MB) | Working set (MB) | Private bytes (MB) | CPU (% of one core) |", "|---|---|---|---|---|---|")
    foreach ($row in $script:Measurements) {
        $md += "| $($row.Build) | $($row.FileMB) | $($row.TaskManagerMemMB) | $($row.WorkingSetMB) | $($row.PrivateBytesMB) | $($row.CpuPercentOfOneCore) |"
    }
    $md -join "`n" | Add-Content $env:GITHUB_STEP_SUMMARY
}

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
