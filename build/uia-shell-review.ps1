param(
    [Parameter(Mandatory = $true)][int]$TargetProcessId,
    [Parameter(Mandatory = $true)][string]$OutputDirectory,
    [int[][]]$Sizes = @(@(900, 650), @(1200, 800), @(1360, 850), @(1600, 1000))
)

$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName UIAutomationClient
Add-Type -AssemblyName UIAutomationTypes
Add-Type -AssemblyName System.Drawing
Add-Type @'
using System;
using System.Runtime.InteropServices;
public static class FungibleShellUiaNative {
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

function Find-ById($Root, [string]$Id, [int]$TimeoutMs = 8000) {
    $condition = [System.Windows.Automation.PropertyCondition]::new(
        [System.Windows.Automation.AutomationElement]::AutomationIdProperty, $Id)
    $timer = [Diagnostics.Stopwatch]::StartNew()
    while ($timer.ElapsedMilliseconds -lt $TimeoutMs) {
        $element = $Root.FindFirst([System.Windows.Automation.TreeScope]::Descendants, $condition)
        if ($null -ne $element) { return $element }
        Start-Sleep -Milliseconds 100
    }
    throw "Timed out waiting for '$Id'."
}

function Select-Page($Window, [string]$Name) {
    $condition = [System.Windows.Automation.PropertyCondition]::new(
        [System.Windows.Automation.AutomationElement]::NameProperty, $Name)
    foreach ($element in $Window.FindAll([System.Windows.Automation.TreeScope]::Descendants, $condition)) {
        $pattern = $null
        if ($element.TryGetCurrentPattern([System.Windows.Automation.SelectionItemPattern]::Pattern, [ref]$pattern)) {
            ([System.Windows.Automation.SelectionItemPattern]$pattern).Select()
            Start-Sleep -Milliseconds 400
            return
        }
    }
    throw "Navigation item '$Name' was not found."
}

function Assert-HorizontallyContained($Element, $Window, [string]$Label) {
    $elementRect = $Element.Current.BoundingRectangle
    $windowRect = $Window.Current.BoundingRectangle
    if ($elementRect.Width -le 0) { throw "$Label has no width." }
    if ($elementRect.Left -lt ($windowRect.Left - 1) -or $elementRect.Right -gt ($windowRect.Right + 1)) {
        throw "$Label overflows horizontally: element=$elementRect window=$windowRect"
    }
}

function Bring-IntoView($Element) {
    $pattern = $null
    if ($Element.TryGetCurrentPattern([System.Windows.Automation.ScrollItemPattern]::Pattern, [ref]$pattern)) {
        ([System.Windows.Automation.ScrollItemPattern]$pattern).ScrollIntoView()
        Start-Sleep -Milliseconds 150
    }
}

function Scroll-ToTop($Window) {
    foreach ($element in $Window.FindAll([System.Windows.Automation.TreeScope]::Descendants,
        [System.Windows.Automation.Condition]::TrueCondition)) {
        $pattern = $null
        if ($element.TryGetCurrentPattern([System.Windows.Automation.ScrollPattern]::Pattern, [ref]$pattern)) {
            try {
                $scroll = [System.Windows.Automation.ScrollPattern]$pattern
                if ($scroll.Current.VerticallyScrollable) { $scroll.SetScrollPercent(-1, 0); Start-Sleep -Milliseconds 200; return }
            } catch { }
        }
    }
}

function Save-Shot([IntPtr]$Handle, [string]$Path) {
    $rect = [FungibleShellUiaNative+RECT]::new()
    if (-not [FungibleShellUiaNative]::GetWindowRect($Handle, [ref]$rect)) { throw 'GetWindowRect failed.' }
    $bitmap = [Drawing.Bitmap]::new($rect.Right - $rect.Left, $rect.Bottom - $rect.Top)
    $graphics = [Drawing.Graphics]::FromImage($bitmap)
    try {
        $dc = $graphics.GetHdc()
        try { $ok = [FungibleShellUiaNative]::PrintWindow($Handle, $dc, 2) }
        finally { $graphics.ReleaseHdc($dc) }
        if (-not $ok) { $graphics.CopyFromScreen($rect.Left, $rect.Top, 0, 0, $bitmap.Size) }
        $bitmap.Save($Path, [Drawing.Imaging.ImageFormat]::Png)
    }
    finally { $graphics.Dispose(); $bitmap.Dispose() }
}

$process = Get-Process -Id $TargetProcessId
$handle = $process.MainWindowHandle
if ($handle -eq [IntPtr]::Zero) { throw 'Target process has no visible main window.' }
$window = [System.Windows.Automation.AutomationElement]::FromHandle($handle)
$scale = [FungibleShellUiaNative]::GetDpiForWindow($handle) / 96.0
New-Item -ItemType Directory -Path $OutputDirectory -Force | Out-Null
$results = @()

foreach ($size in $Sizes) {
    $width, $height = $size
    if (-not [FungibleShellUiaNative]::SetWindowPos($handle, [IntPtr]::Zero, 24, 24,
        [int][Math]::Round($width * $scale), [int][Math]::Round($height * $scale), 0)) {
        throw "Unable to resize to ${width}x${height}."
    }
    [FungibleShellUiaNative]::ShowWindow($handle, 9) | Out-Null
    [FungibleShellUiaNative]::SetForegroundWindow($handle) | Out-Null
    Start-Sleep -Milliseconds 350

    Select-Page $window 'Home'
    Scroll-ToTop $window
    # WinUI intentionally omits layout-only Grid/Border elements from the
    # control UIA view. Assert the exposed content at both horizontal extremes.
    foreach ($id in @('NetWorthText', 'AssetsText', 'LiabilitiesText', 'AsOfText', 'Donut')) {
        $element = Find-ById $window $id
        Bring-IntoView $element
        Assert-HorizontallyContained $element $window "$id at ${width}x${height}"
    }
    Scroll-ToTop $window
    Save-Shot $handle (Join-Path $OutputDirectory "home-${width}x${height}.png")

    Select-Page $window 'Settings'
    Scroll-ToTop $window
    $startup = Find-ById $window 'RunAtLoginToggle'
    Assert-HorizontallyContained $startup $window "Startup toggle at ${width}x${height}"
    if ($startup.Current.IsOffscreen) { throw "Startup toggle is not on the Settings first screen at ${width}x${height}." }
    $toggle = $startup.GetCurrentPattern([System.Windows.Automation.TogglePattern]::Pattern)
    if ($toggle.Current.ToggleState -ne [System.Windows.Automation.ToggleState]::On) {
        throw 'Installed app startup toggle is not enabled.'
    }
    Save-Shot $handle (Join-Path $OutputDirectory "settings-${width}x${height}.png")
    $results += [pscustomobject]@{ Window="${width}x${height}"; HomeContained=$true; SettingsContained=$true; StartupEnabled=$true }
}

$results | Format-Table -AutoSize
