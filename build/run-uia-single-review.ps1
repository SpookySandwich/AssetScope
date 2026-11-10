param(
    [string]$OutputDirectory = (Join-Path $PSScriptRoot '..\artifacts\uia-real-zh-light-v1'),
    [string[]]$Pages = @('home', 'accounts', 'investments', 'trends', 'settings')
)

$ErrorActionPreference = 'Stop'
$appPath = Join-Path $env:LOCALAPPDATA 'Programs\AssetScope\assetscope.exe'
$expectedArtifactRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..\artifacts'))
$resolvedOutput = [IO.Path]::GetFullPath($OutputDirectory)
if (-not $resolvedOutput.StartsWith($expectedArtifactRoot + [IO.Path]::DirectorySeparatorChar,
        [StringComparison]::OrdinalIgnoreCase)) {
    throw "Output directory must be inside $expectedArtifactRoot."
}

if (Test-Path -LiteralPath $resolvedOutput) {
    Remove-Item -LiteralPath $resolvedOutput -Recurse -Force
}

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

    & (Join-Path $PSScriptRoot 'uia-visual-review.ps1') `
        -TargetProcessId $appProcess.Id `
        -OutputDirectory $resolvedOutput `
        -Sizes (,@(1600, 900)) `
        -Pages $Pages
}
finally {
    Get-Process -Name 'assetscope' -ErrorAction SilentlyContinue |
        Stop-Process -Force -ErrorAction SilentlyContinue
}
