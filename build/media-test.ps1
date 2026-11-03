<#
.SYNOPSIS
  Plays "music" through the Windows media controls with build/fake-player and checks what Jer shows:
  song info, the Next and Play/Pause buttons, synced lyrics from LRCLIB, and that skipping a song never
  leaves the previous song's lyrics behind. Runs Jer normally (not in demo mode), so this exercises
  the same code path as Spotify or a browser. Windows PowerShell 5.1 (UI Automation).
#>
param(
    [string]$Exe = "dist/Jer.exe",
    [string]$FakePlayer = "fake/FakePlayer.exe",
    [string]$OutDir = "screenshots"
)

$ErrorActionPreference = "Stop"
$Exe = (Resolve-Path $Exe).Path
$FakePlayer = (Resolve-Path $FakePlayer).Path
New-Item -ItemType Directory -Force -Path $OutDir | Out-Null
$OutDir = (Resolve-Path $OutDir).Path
$DataDir = Join-Path $env:APPDATA "Jer"
$JerLog = Join-Path $DataDir "jer.log"
$PlayerLog = Join-Path (Split-Path $FakePlayer) "fake-player.log"
$info = (Get-Item $Exe).VersionInfo
$Version = "$($info.FileMajorPart).$($info.FileMinorPart).$($info.FileBuildPart)"

Add-Type -AssemblyName System.Windows.Forms, System.Drawing, UIAutomationClient, UIAutomationTypes
Add-Type @"
using System;
using System.Runtime.InteropServices;
public static class MediaTestInput {
    [DllImport("user32.dll")] public static extern bool SetCursorPos(int x, int y);
    [DllImport("user32.dll")] public static extern void mouse_event(uint flags, int dx, int dy, int data, UIntPtr extra);
    [DllImport("user32.dll")] public static extern bool SetProcessDPIAware();
}
"@
[MediaTestInput]::SetProcessDPIAware() | Out-Null
$screen = [System.Windows.Forms.Screen]::PrimaryScreen.Bounds

$Yellow = @("Look at the stars", "Look how they shine for you", "And everything you do", "Yeah, they were all yellow",
            "I came along", "I wrote a song for you", "And all the things you do")
$Bohemian = @("Is this the real life? Is this just fantasy?", "Caught in a landslide, no escape from reality",
              "Open your eyes, look up to the skies and see", "I'm just a poor boy, I need no sympathy")

function Save-Shot([string]$name) {
    $bmp = New-Object System.Drawing.Bitmap $screen.Width, $screen.Height
    $g = [System.Drawing.Graphics]::FromImage($bmp)
    $g.CopyFromScreen($screen.Location, [System.Drawing.Point]::Empty, $screen.Size, [System.Drawing.CopyPixelOperation]::SourceCopy)
    $bmp.Save((Join-Path $OutDir "$name.png"), [System.Drawing.Imaging.ImageFormat]::Png)
    $g.Dispose(); $bmp.Dispose()
    Write-Host "Saved $name.png"
}

function Move-Mouse([int]$x, [int]$y) {
    [MediaTestInput]::SetCursorPos($x, $y) | Out-Null
    [MediaTestInput]::mouse_event(0x0001, 1, 0, 0, [UIntPtr]::Zero)
    Start-Sleep -Milliseconds 60
    [MediaTestInput]::mouse_event(0x0001, -1, 0, 0, [UIntPtr]::Zero)
}

function Get-JerWindow {
    $process = Get-Process Jer -ErrorAction SilentlyContinue | Select-Object -First 1
    if (-not $process) { throw "Jer is not running" }
    $condition = New-Object System.Windows.Automation.AndCondition(
        (New-Object System.Windows.Automation.PropertyCondition([System.Windows.Automation.AutomationElement]::ProcessIdProperty, $process.Id)),
        (New-Object System.Windows.Automation.PropertyCondition([System.Windows.Automation.AutomationElement]::NameProperty, "Jer")))
    return [System.Windows.Automation.AutomationElement]::RootElement.FindFirst([System.Windows.Automation.TreeScope]::Children, $condition)
}

# Every piece of text UI Automation can see in the notch.
function Get-JerTexts {
    $window = Get-JerWindow
    if (-not $window) { return @() }
    $names = @()
    foreach ($element in $window.FindAll([System.Windows.Automation.TreeScope]::Descendants, [System.Windows.Automation.Condition]::TrueCondition)) {
        $name = $element.Current.Name
        if ($name) { $names += $name }
    }
    return $names
}

function Wait-ForText([string[]]$any, [int]$seconds, [string]$what) {
    $deadline = (Get-Date).AddSeconds($seconds)
    while ((Get-Date) -lt $deadline) {
        $texts = Get-JerTexts
        foreach ($text in $any) {
            if ($texts -contains $text) {
                Write-Host "OK: $what ('$text')"
                return
            }
        }
        Start-Sleep -Milliseconds 500
    }
    Write-Host ("Text in the notch: " + ((Get-JerTexts) -join " | "))
    throw "Timed out after $seconds s waiting for: $what"
}

# Lyrics come from LRCLIB over the internet. If the service is down, say so instead of failing the build.
function Wait-ForLyrics([string[]]$lines, [string]$what) {
    try {
        Wait-ForText $lines 40 $what
    }
    catch {
        if ((Test-Path $JerLog) -and (Select-String -Path $JerLog -Pattern "Lyrics lookup failed" -SimpleMatch -Quiet)) {
            Write-Warning "LRCLIB could not be reached, so '$what' was not checked."
            return
        }
        throw
    }
}

function Assert-NoText([string[]]$none, [string]$what) {
    $texts = Get-JerTexts
    foreach ($text in $none) {
        if ($texts -contains $text) { throw "Unexpected '$text' in the notch: $what" }
    }
    Write-Host "OK: $what"
}

function Invoke-JerButton([string]$name) {
    $window = Get-JerWindow
    $condition = New-Object System.Windows.Automation.AndCondition(
        (New-Object System.Windows.Automation.PropertyCondition([System.Windows.Automation.AutomationElement]::ControlTypeProperty, [System.Windows.Automation.ControlType]::Button)),
        (New-Object System.Windows.Automation.PropertyCondition([System.Windows.Automation.AutomationElement]::NameProperty, $name)))
    $button = $window.FindFirst([System.Windows.Automation.TreeScope]::Descendants, $condition)
    if (-not $button) { throw "Button '$name' not found in the notch" }
    $button.GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern).Invoke()
    Write-Host "Pressed '$name'"
}

function Wait-ForPlayerLog([string]$pattern, [int]$seconds) {
    $deadline = (Get-Date).AddSeconds($seconds)
    while ((Get-Date) -lt $deadline) {
        if ((Test-Path $PlayerLog) -and (Select-String -Path $PlayerLog -Pattern $pattern -SimpleMatch -Quiet)) {
            Write-Host "OK: fake player logged '$pattern'"
            return
        }
        Start-Sleep -Milliseconds 300
    }
    throw "The fake player never logged '$pattern'"
}

$player = $null
try {
    Get-Process Jer, FakePlayer -ErrorAction SilentlyContinue | Stop-Process -Force
    Start-Sleep -Milliseconds 800

    # 1. A music app starts playing (Yellow by Coldplay, 0:30 into the song).
    $psi = New-Object System.Diagnostics.ProcessStartInfo $FakePlayer
    $psi.UseShellExecute = $false
    $psi.RedirectStandardInput = $true
    $psi.CreateNoWindow = $true
    $player = [System.Diagnostics.Process]::Start($psi)
    Wait-ForPlayerLog "Registered with the media controls" 15
    Wait-ForPlayerLog "Timeline:" 10

    # 2. Jer starts for real (not demo), with synced lyrics on.
    New-Item -ItemType Directory -Force -Path $DataDir | Out-Null
    @{ FirstRunDone = $true; StartWithWindows = $false; LastVersion = $Version; LyricsEnabled = $true; MascotEnabled = $false } |
        ConvertTo-Json | Set-Content -Path (Join-Path $DataDir "settings.json") -Encoding UTF8
    Move-Mouse ([int]($screen.Width / 2)) ([int]($screen.Height / 2))
    Start-Process -FilePath $Exe | Out-Null
    Wait-ForLyrics $Yellow "a line of Yellow in the closed notch"
    Save-Shot "50-media-closed-yellow"

    # 3. Hover: the open notch shows the song from the media controls.
    Move-Mouse ([int]($screen.Width / 2)) 8
    Wait-ForText @("Yellow") 10 "song title"
    Wait-ForText @("Coldplay") 5 "artist"
    Start-Sleep -Milliseconds 800
    Save-Shot "51-media-open-yellow"

    # 4. Next in Jer skips the song in the player; Jer follows with the new song's lyrics only.
    Invoke-JerButton "Next"
    Wait-ForPlayerLog "Button: Next" 10
    Wait-ForText @("Bohemian Rhapsody") 10 "the next song's title"
    Assert-NoText $Yellow "no Yellow lyrics while the next song loads"
    Wait-ForLyrics $Bohemian "Bohemian Rhapsody lyrics"
    Assert-NoText $Yellow "no Yellow lyrics after the switch"
    Start-Sleep -Milliseconds 800
    Save-Shot "52-media-open-bohemian"

    # 5. Play/Pause in Jer reaches the player.
    Invoke-JerButton "Play or pause"
    Wait-ForPlayerLog "Button: Pause" 10
    Invoke-JerButton "Play or pause"
    Wait-ForPlayerLog "Button: Play" 10

    # 6. Skips from the player itself (keyboard media keys, another app) are followed too.
    $player.StandardInput.WriteLine("next")
    Wait-ForText @("Separuh Aku") 10 "a song skipped in the player"
    Assert-NoText ($Yellow + $Bohemian) "no lyrics from earlier songs"

    # 7. Closed again, with the new song.
    Move-Mouse ([int]($screen.Width / 2)) ([int]($screen.Height / 2))
    Start-Sleep -Seconds 4
    Save-Shot "53-media-closed-separuh"

    # 8. The player quits: Jer shows that nothing is playing.
    $player.StandardInput.WriteLine("quit")
    $player.WaitForExit(10000) | Out-Null
    Move-Mouse ([int]($screen.Width / 2)) 8
    Wait-ForText @("Nothing playing") 15 "nothing playing after the player quit"
    Save-Shot "54-media-player-closed"
    Move-Mouse ([int]($screen.Width / 2)) ([int]($screen.Height / 2))

    if (Select-String -Path $JerLog -Pattern "\[ERROR\]|Media sessions are not available" -Quiet) {
        throw "Jer logged a media problem"
    }

    Write-Host "Media test passed."
}
catch {
    Write-Host "----- fake-player.log -----"
    if (Test-Path $PlayerLog) { Get-Content $PlayerLog | Write-Host }
    Write-Host "----- jer.log -----"
    if (Test-Path $JerLog) { Get-Content $JerLog | Write-Host }
    try { Save-Shot "59-media-failure" } catch { }
    throw
}
finally {
    if ($player -and -not $player.HasExited) { $player.Kill() }
    Get-Process Jer -ErrorAction SilentlyContinue | Stop-Process -Force
    if (Test-Path $PlayerLog) { Copy-Item $PlayerLog (Join-Path $OutDir "fake-player.log") -Force }
}
