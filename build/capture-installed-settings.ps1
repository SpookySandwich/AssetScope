param(
    [string]$OutputPath = (Join-Path $PSScriptRoot '..\artifacts\uia-real-zh-light-settings-final\settings-1600x900.png')
)

$ErrorActionPreference = 'Stop'
$appPath = Join-Path $env:LOCALAPPDATA 'Programs\AssetScope\assetscope.exe'
$expectedArtifactRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..\artifacts'))
$resolvedOutput = [IO.Path]::GetFullPath($OutputPath)
if (-not $resolvedOutput.StartsWith($expectedArtifactRoot + [IO.Path]::DirectorySeparatorChar,
        [StringComparison]::OrdinalIgnoreCase)) {
    throw "Output path must be inside $expectedArtifactRoot."
}

Add-Type -AssemblyName System.Drawing
Add-Type -AssemblyName UIAutomationClient
Add-Type -AssemblyName UIAutomationTypes
Add-Type @'
using System;
using System.Runtime.InteropServices;
public static class FungibleSettingsCaptureNative {
    [StructLayout(LayoutKind.Sequential)] public struct RECT { public int Left, Top, Right, Bottom; }
    [DllImport("user32.dll", SetLastError=true)] public static extern bool GetWindowRect(IntPtr h, out RECT r);
    [DllImport("user32.dll", SetLastError=true)] public static extern bool SetWindowPos(IntPtr h, IntPtr a, int x, int y, int w, int hgt, uint f);
    [DllImport("user32.dll")] public static extern uint GetDpiForWindow(IntPtr h);
    [DllImport("user32.dll")] public static extern bool ShowWindow(IntPtr h, int c);
    [DllImport("user32.dll", SetLastError=true)] public static extern bool PrintWindow(IntPtr h, IntPtr dc, uint f);
}
'@

$appProcess = Start-Process -FilePath $appPath `
    -WorkingDirectory (Split-Path -Parent $appPath) -PassThru
try {
    $deadline = [DateTime]::UtcNow.AddSeconds(30)
    do {
        Start-Sleep -Milliseconds 250
        $appProcess.Refresh()
    } while ($appProcess.MainWindowHandle -eq 0 -and
        -not $appProcess.HasExited -and [DateTime]::UtcNow -lt $deadline)
    if ($appProcess.HasExited -or $appProcess.MainWindowHandle -eq 0) {
        throw 'Installed app did not open a visible main window.'
    }

    $handle = $appProcess.MainWindowHandle
    $scale = [FungibleSettingsCaptureNative]::GetDpiForWindow($handle) / 96.0
    [FungibleSettingsCaptureNative]::ShowWindow($handle, 9) | Out-Null
    [FungibleSettingsCaptureNative]::SetWindowPos(
        $handle, [IntPtr]::Zero, 24, 24,
        [int][Math]::Round(1600 * $scale),
        [int][Math]::Round(900 * $scale), 0) | Out-Null

    # Select the one requested destination by its stable navigation peer. Do
    # not enumerate or interact with Settings content.
    $window = [System.Windows.Automation.AutomationElement]::FromHandle($handle)
    $homeCondition = [System.Windows.Automation.PropertyCondition]::new(
        [System.Windows.Automation.AutomationElement]::AutomationIdProperty, 'NavHome')
    $homeNavItem = $null
    $expandedDeadline = [DateTime]::UtcNow.AddSeconds(10)
    do {
        Start-Sleep -Milliseconds 100
        $homeNavItem = $window.FindFirst(
            [System.Windows.Automation.TreeScope]::Descendants, $homeCondition)
        $homeWidth = if ($null -eq $homeNavItem) { 0 } else {
            $homeNavItem.Current.BoundingRectangle.Width / $scale
        }
        if ($homeWidth -gt 0 -and $homeWidth -lt 150) {
            $toggleCondition = [System.Windows.Automation.PropertyCondition]::new(
                [System.Windows.Automation.AutomationElement]::AutomationIdProperty, 'TogglePaneButton')
            $toggle = $window.FindFirst(
                [System.Windows.Automation.TreeScope]::Descendants, $toggleCondition)
            if ($null -ne $toggle) {
                $invoke = $toggle.GetCurrentPattern(
                    [System.Windows.Automation.InvokePattern]::Pattern)
                $invoke.Invoke()
            }
        }
    } while ($homeWidth -lt 150 -and [DateTime]::UtcNow -lt $expandedDeadline)
    if ($homeWidth -lt 150) {
        throw "Navigation did not expand before capture (item width $homeWidth)."
    }

    $navCondition = [System.Windows.Automation.PropertyCondition]::new(
        [System.Windows.Automation.AutomationElement]::AutomationIdProperty, 'NavSettings')
    $navSettings = $null
    $navDeadline = [DateTime]::UtcNow.AddSeconds(20)
    do {
        Start-Sleep -Milliseconds 100
        $navSettings = $window.FindFirst(
            [System.Windows.Automation.TreeScope]::Descendants, $navCondition)
    } while (($null -eq $navSettings -or -not $navSettings.Current.IsEnabled) -and
        [DateTime]::UtcNow -lt $navDeadline)
    if ($null -eq $navSettings -or -not $navSettings.Current.IsEnabled) {
        throw 'Settings navigation item was not ready.'
    }
    $selection = $navSettings.GetCurrentPattern(
        [System.Windows.Automation.SelectionItemPattern]::Pattern)
    $selection.Select()

    # Allow the sidecar and both first-viewport provider status calls to settle
    # without walking the large Settings automation tree.
    $readyDeadline = [DateTime]::UtcNow.AddSeconds(25)
    do {
        Start-Sleep -Milliseconds 500
        $logPath = Join-Path $env:LOCALAPPDATA 'assetscope\app.log'
        $log = Get-Content -Raw $logPath -ErrorAction SilentlyContinue
    } while ($log -notmatch 'Settings page navigated to' -and [DateTime]::UtcNow -lt $readyDeadline)
    if ($log -notmatch 'Settings page navigated to') {
        throw 'The installed app did not navigate to Settings.'
    }
    Start-Sleep -Seconds 3

    $rect = [FungibleSettingsCaptureNative+RECT]::new()
    if (-not [FungibleSettingsCaptureNative]::GetWindowRect($handle, [ref]$rect)) {
        throw 'GetWindowRect failed.'
    }
    $bitmap = [Drawing.Bitmap]::new($rect.Right - $rect.Left, $rect.Bottom - $rect.Top)
    $graphics = [Drawing.Graphics]::FromImage($bitmap)
    try {
        $dc = $graphics.GetHdc()
        try { $ok = [FungibleSettingsCaptureNative]::PrintWindow($handle, $dc, 2) }
        finally { $graphics.ReleaseHdc($dc) }
        if (-not $ok) { throw 'PrintWindow failed.' }
        New-Item -ItemType Directory -Force (Split-Path $resolvedOutput) | Out-Null
        $bitmap.Save($resolvedOutput, [Drawing.Imaging.ImageFormat]::Png)
        Write-Output $resolvedOutput
    }
    finally {
        $graphics.Dispose()
        $bitmap.Dispose()
    }
}
finally {
    Get-Process -Name 'assetscope' -ErrorAction SilentlyContinue |
        Stop-Process -Force -ErrorAction SilentlyContinue
}
