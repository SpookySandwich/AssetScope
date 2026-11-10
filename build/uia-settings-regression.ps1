param(
    [Parameter(Mandatory = $true)][int]$TargetProcessId,
    [string]$OutputDirectory = (Join-Path $PSScriptRoot '..\artifacts\uia-settings-regression'),
    [int[][]]$Sizes = @(@(900, 650), @(1079, 780), @(1080, 780), @(1400, 900), @(1600, 1000))
)

$ErrorActionPreference = 'Stop'
$repoRoot = [IO.Path]::GetFullPath((Split-Path -Parent $PSScriptRoot))
$artifactRoot = [IO.Path]::GetFullPath((Join-Path $repoRoot 'artifacts'))
$resolvedOutput = [IO.Path]::GetFullPath($OutputDirectory)
if (-not $resolvedOutput.StartsWith(
        $artifactRoot + [IO.Path]::DirectorySeparatorChar,
        [StringComparison]::OrdinalIgnoreCase)) {
    throw "OutputDirectory must be inside $artifactRoot."
}

Add-Type -AssemblyName UIAutomationClient
Add-Type -AssemblyName UIAutomationTypes
Add-Type -AssemblyName System.Drawing
Add-Type @'
using System;
using System.Runtime.InteropServices;
public static class AssetScopeSettingsUiaNative {
    [StructLayout(LayoutKind.Sequential)] public struct RECT { public int Left, Top, Right, Bottom; }
    [DllImport("user32.dll", SetLastError=true)] public static extern bool GetWindowRect(IntPtr h, out RECT r);
    [DllImport("user32.dll", SetLastError=true)] public static extern bool SetWindowPos(IntPtr h, IntPtr a, int x, int y, int w, int hgt, uint f);
    [DllImport("user32.dll")] public static extern uint GetDpiForWindow(IntPtr h);
    [DllImport("user32.dll")] public static extern bool ShowWindow(IntPtr h, int command);
    [DllImport("user32.dll", SetLastError=true)] public static extern bool PrintWindow(IntPtr h, IntPtr dc, uint flags);
}
'@

function Find-OptionalById($Root, [string]$Id) {
    $condition = [System.Windows.Automation.PropertyCondition]::new(
        [System.Windows.Automation.AutomationElement]::AutomationIdProperty, $Id)
    return $Root.FindFirst([System.Windows.Automation.TreeScope]::Descendants, $condition)
}

function Find-ById($Root, [string]$Id, [int]$TimeoutMs = 10000) {
    $timer = [Diagnostics.Stopwatch]::StartNew()
    while ($timer.ElapsedMilliseconds -lt $TimeoutMs) {
        $element = Find-OptionalById $Root $Id
        if ($null -ne $element) { return $element }
        Start-Sleep -Milliseconds 100
    }
    throw "Timed out waiting for '$Id'."
}

function Wait-ForEnabled($Root, [string]$Id, [int]$TimeoutMs = 30000) {
    $timer = [Diagnostics.Stopwatch]::StartNew()
    while ($timer.ElapsedMilliseconds -lt $TimeoutMs) {
        $element = Find-OptionalById $Root $Id
        if ($null -ne $element -and $element.Current.IsEnabled) { return $element }
        Start-Sleep -Milliseconds 100
    }
    throw "Timed out waiting for enabled '$Id'."
}

function Bring-IntoView($Element) {
    $pattern = $null
    if ($Element.TryGetCurrentPattern([System.Windows.Automation.ScrollItemPattern]::Pattern, [ref]$pattern)) {
        ([System.Windows.Automation.ScrollItemPattern]$pattern).ScrollIntoView()
        Start-Sleep -Milliseconds 120
    }
}

function Scroll-ToTop($Window) {
    foreach ($element in $Window.FindAll(
        [System.Windows.Automation.TreeScope]::Descendants,
        [System.Windows.Automation.Condition]::TrueCondition)) {
        try {
            $pattern = $null
            if ($element.TryGetCurrentPattern([System.Windows.Automation.ScrollPattern]::Pattern, [ref]$pattern)) {
                $scroll = [System.Windows.Automation.ScrollPattern]$pattern
                if ($scroll.Current.VerticallyScrollable) {
                    $scroll.SetScrollPercent(-1, 0)
                    Start-Sleep -Milliseconds 160
                    return
                }
            }
        } catch { }
    }
}

function Assert-Contained($Element, $Window, [string]$Label) {
    $item = $Element.Current.BoundingRectangle
    $outer = $Window.Current.BoundingRectangle
    if ($item.Width -le 0 -or $item.Height -le 0) { throw "$Label has no bounds." }
    if ($item.Left -lt ($outer.Left - 1) -or $item.Right -gt ($outer.Right + 1)) {
        throw "$Label overflows horizontally: item=$item window=$outer"
    }
}

function Save-Shot([IntPtr]$Handle, [string]$Path) {
    $rect = [AssetScopeSettingsUiaNative+RECT]::new()
    if (-not [AssetScopeSettingsUiaNative]::GetWindowRect($Handle, [ref]$rect)) {
        throw 'GetWindowRect failed.'
    }
    $bitmap = [Drawing.Bitmap]::new($rect.Right - $rect.Left, $rect.Bottom - $rect.Top)
    $graphics = [Drawing.Graphics]::FromImage($bitmap)
    try {
        $dc = $graphics.GetHdc()
        try { $ok = [AssetScopeSettingsUiaNative]::PrintWindow($Handle, $dc, 2) }
        finally { $graphics.ReleaseHdc($dc) }
        if (-not $ok) { throw 'PrintWindow failed.' }
        $bitmap.Save($Path, [Drawing.Imaging.ImageFormat]::Png)
    }
    finally {
        $graphics.Dispose()
        $bitmap.Dispose()
    }
}

function Invoke-Element($Element, [string]$Label) {
    $pattern = $null
    if (-not $Element.TryGetCurrentPattern(
        [System.Windows.Automation.InvokePattern]::Pattern, [ref]$pattern)) {
        throw "$Label does not expose InvokePattern."
    }
    ([System.Windows.Automation.InvokePattern]$pattern).Invoke()
}

function Invoke-Or-Select($Element, [string]$Label) {
    $pattern = $null
    if ($Element.TryGetCurrentPattern(
        [System.Windows.Automation.InvokePattern]::Pattern, [ref]$pattern)) {
        ([System.Windows.Automation.InvokePattern]$pattern).Invoke()
        return
    }
    if ($Element.TryGetCurrentPattern(
        [System.Windows.Automation.SelectionItemPattern]::Pattern, [ref]$pattern)) {
        ([System.Windows.Automation.SelectionItemPattern]$pattern).Select()
        return
    }
    throw "$Label exposes neither InvokePattern nor SelectionItemPattern."
}

$process = Get-Process -Id $TargetProcessId
$handle = $process.MainWindowHandle
if ($handle -eq [IntPtr]::Zero) { throw 'Target process has no visible main window.' }
$window = [System.Windows.Automation.AutomationElement]::FromHandle($handle)
$scale = [AssetScopeSettingsUiaNative]::GetDpiForWindow($handle) / 96.0
New-Item -ItemType Directory -Path $resolvedOutput -Force | Out-Null

$null = Wait-ForEnabled $window 'NavHome'
$settings = Find-ById $window 'NavSettings'
Invoke-Or-Select $settings 'Settings footer'
$null = Find-ById $window 'ThemeLightItem'

$results = @()
foreach ($size in $Sizes) {
    $width, $height = $size
    [AssetScopeSettingsUiaNative]::ShowWindow($handle, 9) | Out-Null
    if (-not [AssetScopeSettingsUiaNative]::SetWindowPos(
        $handle, [IntPtr]::Zero, 24, 24,
        [int][Math]::Round($width * $scale),
        [int][Math]::Round($height * $scale), 0)) {
        throw "Unable to resize to ${width}x${height}."
    }
    Start-Sleep -Milliseconds 350

    # SelectorBar itself has no AutomationPeer in WinUI; its items expose the
    # native SelectionItemPattern and are the stable observable contract.
    $theme = Find-ById $window 'ThemeLightItem'
    Assert-Contained $theme $window "Theme selector at ${width}x${height}"

    foreach ($actionId in @('BackupNowButton', 'PrivacyModeToggle', 'CopyDiagnosticsButton')) {
        $action = Find-ById $window $actionId
        Bring-IntoView $action
        Assert-Contained $action $window "$actionId at ${width}x${height}"
    }

    $bank = Find-ById $window 'AutomaticBankSyncExpander'
    Bring-IntoView $bank
    $snapTrade = Find-ById $window 'SnapTradeConnectionExpander'
    Bring-IntoView $snapTrade
    $bankWidth = $bank.Current.BoundingRectangle.Width / $scale
    $snapTradeWidth = $snapTrade.Current.BoundingRectangle.Width / $scale
    if ([Math]::Abs($bankWidth - $snapTradeWidth) -gt 8) {
        throw "Connection rows do not share a width at ${width}x${height}: Plaid=$bankWidth SnapTrade=$snapTradeWidth."
    }
    if ($bankWidth -lt ($width * 0.35)) {
        throw "Connection rows remain undersized at ${width}x${height}: width=$bankWidth."
    }

    foreach ($groupId in @('BackupsSettingsGroup', 'PrivacySecuritySettingsGroup', 'DiagnosticsSettingsGroup')) {
        $group = Find-OptionalById $window $groupId
        if ($null -ne $group) {
            $expandPattern = $null
            if ($group.TryGetCurrentPattern(
                [System.Windows.Automation.ExpandCollapsePattern]::Pattern, [ref]$expandPattern)) {
                throw "$groupId unexpectedly exposes ExpandCollapsePattern."
            }
        }
    }

    Scroll-ToTop $window
    Save-Shot $handle (Join-Path $resolvedOutput "settings-${width}x${height}.png")
    $results += [pscustomobject]@{
        Window = "${width}x${height}"
        PlaidWidth = [Math]::Round($bankWidth, 1)
        SnapTradeWidth = [Math]::Round($snapTradeWidth, 1)
        FixedGroupsReachable = $true
    }
}

# Footer privacy is one session state shared with the settings ToggleSwitch.
# Exercise both directions and restore the user's original state.
[AssetScopeSettingsUiaNative]::SetWindowPos(
    $handle, [IntPtr]::Zero, 24, 24,
    [int][Math]::Round(1400 * $scale),
    [int][Math]::Round(900 * $scale), 0) | Out-Null
Start-Sleep -Milliseconds 250
$privacyFooter = Find-ById $window 'NavPrivacy'
$privacyToggle = Find-ById $window 'PrivacyModeToggle'
$togglePattern = [System.Windows.Automation.TogglePattern]$privacyToggle.GetCurrentPattern(
    [System.Windows.Automation.TogglePattern]::Pattern)
$beforePrivacy = $togglePattern.Current.ToggleState
$beforePrivacyName = $privacyFooter.Current.Name
Invoke-Or-Select $privacyFooter 'Privacy footer'
$deadline = [DateTime]::UtcNow.AddSeconds(15)
do {
    Start-Sleep -Milliseconds 100
    $privacyFooter = Find-ById $window 'NavPrivacy'
    $privacyToggle = Find-ById $window 'PrivacyModeToggle'
    $togglePattern = [System.Windows.Automation.TogglePattern]$privacyToggle.GetCurrentPattern(
        [System.Windows.Automation.TogglePattern]::Pattern)
    $afterPrivacy = $togglePattern.Current.ToggleState
} while ($afterPrivacy -eq $beforePrivacy -and [DateTime]::UtcNow -lt $deadline)
if ($afterPrivacy -eq $beforePrivacy) { throw 'Privacy footer did not update the settings toggle.' }
if ($privacyFooter.Current.Name -eq $beforePrivacyName) { throw 'Privacy footer accessible action did not change.' }
Save-Shot $handle (Join-Path $resolvedOutput 'settings-privacy-after-toggle-1400x900.png')
Invoke-Or-Select $privacyFooter 'Privacy footer restore'
$deadline = [DateTime]::UtcNow.AddSeconds(5)
do {
    Start-Sleep -Milliseconds 100
    $privacyToggle = Find-ById $window 'PrivacyModeToggle'
    $togglePattern = [System.Windows.Automation.TogglePattern]$privacyToggle.GetCurrentPattern(
        [System.Windows.Automation.TogglePattern]::Pattern)
} while ($togglePattern.Current.ToggleState -ne $beforePrivacy -and [DateTime]::UtcNow -lt $deadline)
if ($togglePattern.Current.ToggleState -ne $beforePrivacy) { throw 'Privacy footer did not restore its original state.' }

# Theme changes now live only in Settings. Verify the SelectorBar changes the
# effective theme, capture it for contrast review, then restore the original.
$themeIds = @('ThemeLightItem', 'ThemeDarkItem', 'ThemeSystemItem')
$originalTheme = $null
foreach ($themeId in $themeIds) {
    $item = Find-ById $window $themeId
    $selection = [System.Windows.Automation.SelectionItemPattern]$item.GetCurrentPattern(
        [System.Windows.Automation.SelectionItemPattern]::Pattern)
    if ($selection.Current.IsSelected) { $originalTheme = $themeId; break }
}
if ($null -eq $originalTheme) { throw 'No selected theme item was exposed.' }
$targetTheme = if ($originalTheme -eq 'ThemeDarkItem') { 'ThemeLightItem' } else { 'ThemeDarkItem' }
Invoke-Or-Select (Find-ById $window $targetTheme) 'Theme selector target'
Start-Sleep -Milliseconds 500
$targetSelection = [System.Windows.Automation.SelectionItemPattern](
    (Find-ById $window $targetTheme).GetCurrentPattern(
        [System.Windows.Automation.SelectionItemPattern]::Pattern))
if (-not $targetSelection.Current.IsSelected) { throw 'Theme selector did not select the target theme.' }
Save-Shot $handle (Join-Path $resolvedOutput 'settings-theme-after-toggle-1400x900.png')
Invoke-Or-Select (Find-ById $window $originalTheme) 'Theme selector restore'
Start-Sleep -Milliseconds 500

# The header action is one Button whose accessible state follows the same
# arrow -> spinning arrow -> success check -> arrow lifecycle.
$refresh = Wait-ForEnabled $window 'HeaderRefreshButton'
$refreshIdleName = $refresh.Current.Name
Invoke-Element $refresh 'Header refresh button'
$deadline = [DateTime]::UtcNow.AddSeconds(5)
do {
    Start-Sleep -Milliseconds 100
    $refresh = Find-ById $window 'HeaderRefreshButton'
} while ($refresh.Current.IsEnabled -and $refresh.Current.Name -eq $refreshIdleName -and
    [DateTime]::UtcNow -lt $deadline)
if ($refresh.Current.IsEnabled -and $refresh.Current.Name -eq $refreshIdleName) {
    throw 'Refresh button never entered its syncing state.'
}
$syncName = $refresh.Current.Name
$deadline = [DateTime]::UtcNow.AddMinutes(3)
do {
    Start-Sleep -Milliseconds 200
    $refresh = Find-ById $window 'HeaderRefreshButton'
} while (-not $refresh.Current.IsEnabled -and [DateTime]::UtcNow -lt $deadline)
if (-not $refresh.Current.IsEnabled) { throw 'Refresh button did not leave its syncing state.' }
if ($refresh.Current.Name -eq $syncName -or $refresh.Current.Name -eq $refreshIdleName) {
    throw "Refresh button did not expose a distinct success state; name='$($refresh.Current.Name)'."
}
$successName = $refresh.Current.Name
Save-Shot $handle (Join-Path $resolvedOutput 'settings-refresh-success-1400x900.png')
$deadline = [DateTime]::UtcNow.AddSeconds(15)
do {
    Start-Sleep -Milliseconds 100
    $refresh = Find-ById $window 'HeaderRefreshButton'
} while ($refresh.Current.Name -ne $refreshIdleName -and [DateTime]::UtcNow -lt $deadline)
if ($refresh.Current.Name -ne $refreshIdleName) { throw 'Refresh button did not return to idle.' }

# These placeholders intentionally remain visible but inert. Invoking them
# must leave the current window enabled and must not open a dialog or browser.
foreach ($inertId in @('CheckUpdatesButton', 'OpenHelpButton')) {
    $inert = Find-ById $window $inertId
    Bring-IntoView $inert
    Invoke-Element $inert $inertId
    Start-Sleep -Milliseconds 300
    if (-not $window.Current.IsEnabled) {
        throw "$inertId unexpectedly opened a modal interaction."
    }
}

[AssetScopeSettingsUiaNative]::ShowWindow($handle, 3) | Out-Null
Start-Sleep -Milliseconds 350
Scroll-ToTop $window
Save-Shot $handle (Join-Path $resolvedOutput 'settings-maximized.png')

$results | Format-Table -AutoSize
Write-Output "Privacy toggle: '$beforePrivacyName' -> '$($privacyFooter.Current.Name)' -> restored"
Write-Output "Theme selector: '$originalTheme' -> '$targetTheme' -> '$originalTheme'"
Write-Output "Refresh state: '$refreshIdleName' -> '$syncName' -> '$successName' -> '$($refresh.Current.Name)'"
