#!/usr/bin/env pwsh
# Captures the real NovaTerminal window, including the OS drop shadow and rounded corners
# that a headless render cannot produce.
#
# This script does NOT drive the app. You arrange the window; it only captures. Foreground
# automation on Windows is unreliable enough that leaving arrangement to a human is the
# design, not a limitation. See docs/assets/shots/hero/README.md for the exact arrangement
# (profile, commands, theme, window size) to produce before running this for each hero shot.

param(
    [Parameter(Mandatory = $true)][string] $Name,
    [string] $OutputDirectory = "docs/assets/shots/hero"
)

$ErrorActionPreference = 'Stop'

Add-Type -AssemblyName System.Drawing
Add-Type @"
using System;
using System.Runtime.InteropServices;
public struct RECT { public int Left, Top, Right, Bottom; }
public static class Dwm {
    [DllImport("dwmapi.dll")]
    public static extern int DwmGetWindowAttribute(IntPtr hwnd, int attr, out RECT value, int size);
}
"@

$novaTerminalProcesses = @(Get-Process -Name 'NovaTerminal' -ErrorAction SilentlyContinue)
if ($novaTerminalProcesses.Count -eq 0) { throw 'NovaTerminal is not running. Start it, arrange the window, then re-run.' }
if ($novaTerminalProcesses.Count -gt 1) {
    Write-Warning "Multiple NovaTerminal processes are running; capturing PID $($novaTerminalProcesses[0].Id). Close the others first if that is not the one you arranged."
}
$process = $novaTerminalProcesses[0]

if ($process.MainWindowHandle -eq [IntPtr]::Zero) {
    throw 'NovaTerminal has no main window handle yet (minimized, still starting, or hidden). Restore the window and re-run.'
}

Write-Host 'Arrange the window now. Capturing in 5 seconds…'
Start-Sleep -Seconds 5

$rect = New-Object RECT
$DWMWA_EXTENDED_FRAME_BOUNDS = 9
$hr = [Dwm]::DwmGetWindowAttribute($process.MainWindowHandle, $DWMWA_EXTENDED_FRAME_BOUNDS, [ref] $rect, 16)
if ($hr -ne 0) { throw "DwmGetWindowAttribute failed (HRESULT 0x$($hr.ToString('X8'))). Is DWM composition running?" }

$pad = 40
$width = ($rect.Right - $rect.Left) + ($pad * 2)
$height = ($rect.Bottom - $rect.Top) + ($pad * 2)

$bitmap = New-Object System.Drawing.Bitmap $width, $height
$graphics = [System.Drawing.Graphics]::FromImage($bitmap)
$graphics.CopyFromScreen($rect.Left - $pad, $rect.Top - $pad, 0, 0, $bitmap.Size)

New-Item -ItemType Directory -Force -Path $OutputDirectory | Out-Null
$path = Join-Path $OutputDirectory "$Name.png"
$bitmap.Save($path, [System.Drawing.Imaging.ImageFormat]::Png)

$graphics.Dispose()
$bitmap.Dispose()

Write-Host "Saved $path ($width x $height)"
