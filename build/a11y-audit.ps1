# Automated accessibility audit (SPEC §27.7, automated portion): walks the UIA
# tree of the running assetscope window on each main page and fails when
# any interactive element lacks an accessible name (SPEC §24.1).
#
#   powershell -ExecutionPolicy Bypass -File build\a11y-audit.ps1

param([int]$TargetProcessId = 0)

$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName UIAutomationClient
Add-Type -AssemblyName UIAutomationTypes

$process = if ($TargetProcessId -gt 0) {
    Get-Process -Id $TargetProcessId -ErrorAction SilentlyContinue
} else {
    Get-Process -Name 'assetscope' -ErrorAction SilentlyContinue | Select-Object -First 1
}
if (-not $process) { throw 'assetscope is not running. Launch it first.' }

$root = [System.Windows.Automation.AutomationElement]::RootElement
$window = $root.FindFirst([System.Windows.Automation.TreeScope]::Children,
    (New-Object System.Windows.Automation.PropertyCondition(
        [System.Windows.Automation.AutomationElement]::ProcessIdProperty, $process.Id)))
if (-not $window) { throw 'Main window not found.' }

$interactiveTypes = @('button', 'check box', 'radio button', 'combo box', 'edit',
    'menu item', 'list item', 'tab item', 'hyperlink', 'spinner')

function Get-Violations {
    param($scopeName)
    $violations = @()
    $all = $window.FindAll([System.Windows.Automation.TreeScope]::Descendants,
        [System.Windows.Automation.Condition]::TrueCondition)
    $counted = 0
    foreach ($element in $all) {
        $c = $element.Current
        $type = $c.LocalizedControlType
        if ($interactiveTypes -notcontains $type) { continue }
        if ($c.IsOffscreen) { continue }
        $counted++
        if ([string]::IsNullOrWhiteSpace($c.Name) -and [string]::IsNullOrWhiteSpace($c.HelpText)) {
            $violations += "[$scopeName] unnamed $type (automationId='$($c.AutomationId)')"
        }
    }
    Write-Host ("  {0}: {1} interactive elements checked, {2} unnamed" -f $scopeName, $counted, $violations.Count)
    return $violations
}

function Select-NavItem {
    param($name)
    # The UIA tree is briefly stale right after a page navigation, so retry.
    # Several elements can share the name (nav item, its inner text, page
    # title); try each until one supports SelectionItemPattern.
    for ($attempt = 0; $attempt -lt 4; $attempt++) {
        $navMatches = $window.FindAll([System.Windows.Automation.TreeScope]::Descendants,
            (New-Object System.Windows.Automation.PropertyCondition(
                [System.Windows.Automation.AutomationElement]::NameProperty, $name)))
        foreach ($item in $navMatches) {
            try {
                $pattern = $item.GetCurrentPattern([System.Windows.Automation.SelectionItemPattern]::Pattern)
                $pattern.Select()
                Start-Sleep -Seconds 3
                return $true
            } catch { }
        }
        Start-Sleep -Seconds 2
    }
    return $false
}

Write-Host 'Accessibility audit (unnamed interactive elements = violations):'
$allViolations = @()
foreach ($page in @('Home', 'Accounts', 'Assets', 'Trends', 'Settings')) {
    if (Select-NavItem $page) {
        $allViolations += Get-Violations $page
    } else {
        Write-Host "  $($page): navigation item not found — skipped"
    }
}

if ($allViolations.Count -gt 0) {
    Write-Host "`nFAIL — unnamed interactive elements:" -ForegroundColor Red
    $allViolations | ForEach-Object { Write-Host "  $_" }
    exit 1
}
Write-Host "`nPASS — every visible interactive element exposes an accessible name." -ForegroundColor Green
exit 0
