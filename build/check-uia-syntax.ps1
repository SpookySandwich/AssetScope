$ErrorActionPreference = 'Stop'
$tokens = $null
$parseErrors = $null
$target = Join-Path $PSScriptRoot 'uia-visual-review.ps1'
[void][System.Management.Automation.Language.Parser]::ParseFile(
    $target, [ref]$tokens, [ref]$parseErrors)
if ($parseErrors.Count -gt 0) {
    $parseErrors | Format-List
    exit 1
}
Write-Output "UIA syntax OK: $($PSVersionTable.PSVersion)"
