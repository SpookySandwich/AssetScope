param(
    [Parameter(Mandatory = $true)][int]$TargetProcessId,
    [Parameter(Mandatory = $true)][string]$OutputDirectory,
    [int[][]]$Sizes = @(@(900, 650), @(1162, 780), @(1163, 780), @(1164, 780), @(1200, 800), @(1600, 1000))
)

$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName UIAutomationClient
Add-Type -AssemblyName UIAutomationTypes
Add-Type -AssemblyName System.Drawing
Add-Type -AssemblyName System.Windows.Forms

Add-Type @'
using System;
using System.Runtime.InteropServices;
public static class FungibleTrendUiaNative {
    [StructLayout(LayoutKind.Sequential)]
    public struct RECT { public int Left, Top, Right, Bottom; }
    [DllImport("user32.dll", SetLastError=true)]
    public static extern bool GetWindowRect(IntPtr hwnd, out RECT rect);
    [DllImport("user32.dll", SetLastError=true)]
    public static extern bool SetWindowPos(IntPtr hwnd, IntPtr after, int x, int y, int cx, int cy, uint flags);
    [DllImport("user32.dll")]
    public static extern uint GetDpiForWindow(IntPtr hwnd);
    [DllImport("user32.dll")]
    public static extern bool ShowWindow(IntPtr hwnd, int command);
    [DllImport("user32.dll")]
    public static extern bool SetForegroundWindow(IntPtr hwnd);
    [DllImport("user32.dll", SetLastError=true)]
    public static extern bool PrintWindow(IntPtr hwnd, IntPtr hdc, uint flags);
}
'@

function Wait-ElementById {
    param($Root, [string]$AutomationId, [int]$TimeoutMs = 8000)
    $condition = [System.Windows.Automation.PropertyCondition]::new(
        [System.Windows.Automation.AutomationElement]::AutomationIdProperty, $AutomationId)
    $timer = [Diagnostics.Stopwatch]::StartNew()
    while ($timer.ElapsedMilliseconds -lt $TimeoutMs) {
        $element = $Root.FindFirst([System.Windows.Automation.TreeScope]::Descendants, $condition)
        if ($null -ne $element) { return $element }
        Start-Sleep -Milliseconds 100
    }
    throw "Timed out waiting for visible AutomationId '$AutomationId'."
}

function Assert-Reachable {
    param($Element, $Window, [string]$Label)
    $elementRect = $Element.Current.BoundingRectangle
    $windowRect = $Window.Current.BoundingRectangle
    if ($Element.Current.IsOffscreen -or $elementRect.Width -le 0 -or $elementRect.Height -le 0) {
        throw "$Label is offscreen or empty."
    }
    if ($elementRect.Left -lt $windowRect.Left -or $elementRect.Top -lt $windowRect.Top -or
        $elementRect.Right -gt $windowRect.Right -or $elementRect.Bottom -gt $windowRect.Bottom) {
        throw "$Label extends outside the window."
    }
}

function Bring-IntoView {
    param($Element)
    $pattern = $null
    if ($Element.TryGetCurrentPattern([System.Windows.Automation.ScrollItemPattern]::Pattern, [ref]$pattern)) {
        ([System.Windows.Automation.ScrollItemPattern]$pattern).ScrollIntoView()
        Start-Sleep -Milliseconds 250
    }
}

function Save-WindowScreenshot {
    param([IntPtr]$Handle, [string]$Path)
    $rect = [FungibleTrendUiaNative+RECT]::new()
    if (-not [FungibleTrendUiaNative]::GetWindowRect($Handle, [ref]$rect)) { throw 'GetWindowRect failed.' }
    $bitmap = [Drawing.Bitmap]::new($rect.Right - $rect.Left, $rect.Bottom - $rect.Top)
    $graphics = [Drawing.Graphics]::FromImage($bitmap)
    try {
        $hdc = $graphics.GetHdc()
        try { $captured = [FungibleTrendUiaNative]::PrintWindow($Handle, $hdc, 2) }
        finally { $graphics.ReleaseHdc($hdc) }
        if (-not $captured) {
            $graphics.CopyFromScreen($rect.Left, $rect.Top, 0, 0, $bitmap.Size)
        }
        $bitmap.Save($Path, [Drawing.Imaging.ImageFormat]::Png)
    }
    finally {
        $graphics.Dispose()
        $bitmap.Dispose()
    }
}

$process = Get-Process -Id $TargetProcessId
$handle = $process.MainWindowHandle
if ($handle -eq [IntPtr]::Zero) { throw 'Target process has no main window.' }
New-Item -ItemType Directory -Path $OutputDirectory -Force | Out-Null
$window = [System.Windows.Automation.AutomationElement]::FromHandle($handle)
$dpi = [FungibleTrendUiaNative]::GetDpiForWindow($handle)
$scale = $dpi / 96.0
$results = @()

foreach ($size in $Sizes) {
    $logicalWidth, $logicalHeight = $size
    $physicalWidth = [int][Math]::Round($logicalWidth * $scale)
    $physicalHeight = [int][Math]::Round($logicalHeight * $scale)
    # Restore first. Calling SW_RESTORE after SetWindowPos reapplies the saved
    # restore rectangle and silently turns a requested 1600x900 review into a
    # narrow responsive-state screenshot with a misleading filename.
    $null = [FungibleTrendUiaNative]::ShowWindow($handle, 9)
    Start-Sleep -Milliseconds 150
    if (-not [FungibleTrendUiaNative]::SetWindowPos(
        $handle, [IntPtr]::Zero, 30, 30, $physicalWidth, $physicalHeight, 0)) {
        throw "SetWindowPos failed for ${logicalWidth}x${logicalHeight}."
    }
    $null = [FungibleTrendUiaNative]::SetForegroundWindow($handle)
    Start-Sleep -Milliseconds 250

    foreach ($page in @(
        @{ Name = 'Trends'; Shortcut = '^4'; ReadyId = 'TrendsContentReady'; Notice = 'TrendsEstimateNotice' }
    )) {
        # Ctrl+4 is the app's public, localized navigation contract. It is more
        # robust than relying on NavigationViewItem exposure, which varies
        # between WinUI/UIA control-view implementations.
        $null = [FungibleTrendUiaNative]::SetForegroundWindow($handle)
        [System.Windows.Forms.SendKeys]::SendWait($page.Shortcut)
        # Page itself is a layout container and may be absent from the UIA
        # control view. Each page publishes a content-ready marker on its main
        # amount text after real data has rendered.
        $null = Wait-ElementById -Root $window -AutomationId $page.ReadyId
        $notice = Wait-ElementById -Root $window -AutomationId $page.Notice
        Bring-IntoView -Element $notice
        Assert-Reachable -Element $notice -Window $window -Label "$($page.Name) estimate notice at ${logicalWidth}x${logicalHeight}"
        if ($notice.Current.Name -notmatch 'transaction|交易|estimate|估算' -or
            $notice.Current.Name -match 'ChartEstimateBar') {
            throw "$($page.Name) estimate notice has no explanatory accessible name: '$($notice.Current.Name)'."
        }
        Save-WindowScreenshot -Handle $handle -Path (
            Join-Path $OutputDirectory "$($page.Name.ToLowerInvariant())-${logicalWidth}x${logicalHeight}.png")
        $results += [pscustomobject]@{
            Page = $page.Name
            Window = "${logicalWidth}x${logicalHeight}"
            NoticeName = $notice.Current.Name
            Reachable = $true
        }
    }
}

$results | Format-Table -AutoSize
