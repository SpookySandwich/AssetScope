param(
    [Parameter(Mandatory = $true)][int]$TargetProcessId,
    [switch]$SelectSettings
)

$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName UIAutomationClient
Add-Type -AssemblyName UIAutomationTypes

$process = Get-Process -Id $TargetProcessId
$window = [System.Windows.Automation.AutomationElement]::FromHandle($process.MainWindowHandle)
if ($SelectSettings) {
    $condition = [System.Windows.Automation.PropertyCondition]::new(
        [System.Windows.Automation.AutomationElement]::AutomationIdProperty,
        'NavSettings')
    $settings = $window.FindFirst(
        [System.Windows.Automation.TreeScope]::Descendants,
        $condition)
    $selection = [System.Windows.Automation.SelectionItemPattern]$settings.GetCurrentPattern(
        [System.Windows.Automation.SelectionItemPattern]::Pattern)
    $selection.Select()
    Start-Sleep -Milliseconds 1000
}
$all = $window.FindAll(
    [System.Windows.Automation.TreeScope]::Descendants,
    [System.Windows.Automation.Condition]::TrueCondition)
$rows = @(
    foreach ($element in $all) {
        try {
            $current = $element.Current
            if ($current.AutomationId -notmatch 'NavPrivacy|NavSettings|Settings|TogglePane|Theme|Language' -and
                $current.Name -notmatch '金额|Privacy|设置|Settings|主题|Theme|语言|Language') { continue }
            $selected = $null
            $selectionPattern = $null
            if ($element.TryGetCurrentPattern(
                [System.Windows.Automation.SelectionItemPattern]::Pattern,
                [ref]$selectionPattern)) {
                $selected = ([System.Windows.Automation.SelectionItemPattern]$selectionPattern).Current.IsSelected
            }
            [pscustomobject]@{
                Name = $current.Name
                Id = $current.AutomationId
                Type = $current.ControlType.ProgrammaticName
                Class = $current.ClassName
                Offscreen = $current.IsOffscreen
                Enabled = $current.IsEnabled
                Patterns = ($element.GetSupportedPatterns().ProgrammaticName -join ', ')
                Selected = $selected
                Bounds = $current.BoundingRectangle.ToString()
            }
        }
        catch [System.Windows.Automation.ElementNotAvailableException] { }
    }
)
$rows | Format-List
