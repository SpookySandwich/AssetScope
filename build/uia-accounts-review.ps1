param(
    [Parameter(Mandatory = $true)][int]$TargetProcessId,
    [Parameter(Mandatory = $true)][string]$OutputDirectory,
    [int[][]]$Sizes = @(@(900, 650), @(1162, 780), @(1163, 780), @(1164, 780), @(1200, 800), @(1600, 1000))
)

$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName UIAutomationClient
Add-Type -AssemblyName UIAutomationTypes
Add-Type -AssemblyName System.Drawing

Add-Type @'
using System;
using System.Runtime.InteropServices;
public static class FungibleUiaNative {
    [StructLayout(LayoutKind.Sequential)]
    public struct RECT { public int Left, Top, Right, Bottom; }
    [DllImport("user32.dll", SetLastError=true)]
    public static extern bool GetWindowRect(IntPtr hwnd, out RECT rect);
    [DllImport("user32.dll", SetLastError=true)]
    public static extern bool SetWindowPos(IntPtr hwnd, IntPtr after, int x, int y, int cx, int cy, uint flags);
    [DllImport("user32.dll")]
    public static extern uint GetDpiForWindow(IntPtr hwnd);
}
'@

function Wait-ElementById {
    param(
        [System.Windows.Automation.AutomationElement]$Root,
        [string]$AutomationId,
        [int]$TimeoutMs = 6000
    )
    $stopwatch = [Diagnostics.Stopwatch]::StartNew()
    $condition = [System.Windows.Automation.PropertyCondition]::new(
        [System.Windows.Automation.AutomationElement]::AutomationIdProperty,
        $AutomationId)
    while ($stopwatch.ElapsedMilliseconds -lt $TimeoutMs) {
        $element = $Root.FindFirst([System.Windows.Automation.TreeScope]::Descendants, $condition)
        if ($null -ne $element) { return $element }
        Start-Sleep -Milliseconds 100
    }
    throw "Timed out waiting for AutomationId '$AutomationId'."
}

function Find-ElementByName {
    param(
        [System.Windows.Automation.AutomationElement]$Root,
        [string]$Name
    )
    $condition = [System.Windows.Automation.PropertyCondition]::new(
        [System.Windows.Automation.AutomationElement]::NameProperty,
        $Name)
    return $Root.FindFirst([System.Windows.Automation.TreeScope]::Descendants, $condition)
}

function Invoke-Element {
    param([System.Windows.Automation.AutomationElement]$Element)
    $pattern = $null
    if ($Element.TryGetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern, [ref]$pattern)) {
        ([System.Windows.Automation.InvokePattern]$pattern).Invoke()
        return
    }
    if ($Element.TryGetCurrentPattern([System.Windows.Automation.SelectionItemPattern]::Pattern, [ref]$pattern)) {
        ([System.Windows.Automation.SelectionItemPattern]$pattern).Select()
        return
    }
    throw "Element '$($Element.Current.Name)' has no supported invoke pattern."
}

function Assert-Reachable {
    param(
        [System.Windows.Automation.AutomationElement]$Element,
        [System.Windows.Automation.AutomationElement]$Window,
        [string]$Label
    )
    $elementRect = $Element.Current.BoundingRectangle
    $windowRect = $Window.Current.BoundingRectangle
    if ($Element.Current.IsOffscreen -or $elementRect.Width -le 0 -or $elementRect.Height -le 0) {
        throw "$Label is offscreen or has an empty bounding rectangle."
    }
    if ($elementRect.Left -lt $windowRect.Left -or $elementRect.Top -lt $windowRect.Top -or
        $elementRect.Right -gt $windowRect.Right -or $elementRect.Bottom -gt $windowRect.Bottom) {
        throw "$Label extends outside the window."
    }
}

function Save-WindowScreenshot {
    param([IntPtr]$Handle, [string]$Path)
    $rect = [FungibleUiaNative+RECT]::new()
    if (-not [FungibleUiaNative]::GetWindowRect($Handle, [ref]$rect)) {
        throw 'GetWindowRect failed.'
    }
    $width = $rect.Right - $rect.Left
    $height = $rect.Bottom - $rect.Top
    $bitmap = [Drawing.Bitmap]::new($width, $height)
    $graphics = [Drawing.Graphics]::FromImage($bitmap)
    try {
        $graphics.CopyFromScreen($rect.Left, $rect.Top, 0, 0, [Drawing.Size]::new($width, $height))
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
$accountsNav = Find-ElementByName -Root $window -Name 'Accounts'
if ($null -eq $accountsNav) { throw 'Accounts navigation item was not found.' }
Invoke-Element $accountsNav
$null = Wait-ElementById -Root $window -AutomationId 'AccountsList'

$dpi = [FungibleUiaNative]::GetDpiForWindow($handle)
$scale = $dpi / 96.0
$results = @()

foreach ($size in $Sizes) {
    $logicalWidth = $size[0]
    $logicalHeight = $size[1]
    $physicalWidth = [int][Math]::Round($logicalWidth * $scale)
    $physicalHeight = [int][Math]::Round($logicalHeight * $scale)
    if (-not [FungibleUiaNative]::SetWindowPos($handle, [IntPtr]::Zero, 40, 40, $physicalWidth, $physicalHeight, 0x0004)) {
        throw "SetWindowPos failed for ${logicalWidth}x${logicalHeight}."
    }
    Start-Sleep -Milliseconds 350

    $accountsPage = Wait-ElementById -Root $window -AutomationId 'AccountsList'
    foreach ($controlId in @('AccountsSearchBox', 'AccountsSortPicker', 'AddAccountButton', 'AccountsFilterScroller', 'AccountsList')) {
        $control = Wait-ElementById -Root $window -AutomationId $controlId
        Assert-Reachable -Element $control -Window $window -Label "$controlId at ${logicalWidth}x${logicalHeight}"
    }
    $pageLogicalWidth = [Math]::Round($accountsPage.Current.BoundingRectangle.Width / $scale, 1)
    Save-WindowScreenshot -Handle $handle -Path (Join-Path $OutputDirectory "accounts-${logicalWidth}x${logicalHeight}.png")

    $addButton = Wait-ElementById -Root $window -AutomationId 'AddAccountButton'
    Invoke-Element $addButton
    $dialog = Wait-ElementById -Root $window -AutomationId 'AddAccountChoiceDialog'
    foreach ($controlId in @('AlreadyConnectedInstitutionsList', 'ConnectAnotherInstitutionButton', 'AddManualBalanceButton', 'ManageConnectedInstitutionsButton')) {
        $control = Wait-ElementById -Root $dialog -AutomationId $controlId
        Assert-Reachable -Element $control -Window $window -Label "$controlId in dialog at ${logicalWidth}x${logicalHeight}"
    }
    Save-WindowScreenshot -Handle $handle -Path (Join-Path $OutputDirectory "add-account-${logicalWidth}x${logicalHeight}.png")

    $cancel = Find-ElementByName -Root $dialog -Name 'Cancel'
    if ($null -eq $cancel) { throw 'Dialog Cancel button was not found.' }
    Invoke-Element $cancel
    Start-Sleep -Milliseconds 150

    $results += [pscustomobject]@{
        Window = "${logicalWidth}x${logicalHeight}"
        AccountsPageLogicalWidth = $pageLogicalWidth
        DialogControlsReachable = $true
    }
}

# Functional routing checks: managing an existing Item must go to Settings;
# connecting another institution must reuse the same SetupView flow. The demo
# profile has no credentials, so this check never launches Plaid or a browser.
$addButton = Wait-ElementById -Root $window -AutomationId 'AddAccountButton'
Invoke-Element $addButton
$dialog = Wait-ElementById -Root $window -AutomationId 'AddAccountChoiceDialog'
Invoke-Element (Wait-ElementById -Root $dialog -AutomationId 'ManageConnectedInstitutionsButton')
$null = Wait-ElementById -Root $window -AutomationId 'AutomaticBankSyncExpander'

$accountsNav = Find-ElementByName -Root $window -Name 'Accounts'
Invoke-Element $accountsNav
$null = Wait-ElementById -Root $window -AutomationId 'AccountsList'
Invoke-Element (Wait-ElementById -Root $window -AutomationId 'AddAccountButton')
$dialog = Wait-ElementById -Root $window -AutomationId 'AddAccountChoiceDialog'
Invoke-Element (Wait-ElementById -Root $dialog -AutomationId 'ConnectAnotherInstitutionButton')
$null = Wait-ElementById -Root $window -AutomationId 'PlaidClientIdInput'
$setupCancel = Wait-ElementById -Root $window -AutomationId 'SetupCancelButton'
Assert-Reachable -Element $setupCancel -Window $window -Label 'Setup Cancel after Connect another institution'
Invoke-Element $setupCancel

$results | Format-Table -AutoSize
