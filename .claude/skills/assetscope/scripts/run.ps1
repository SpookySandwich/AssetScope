param(
  [Parameter(ValueFromRemainingArguments = $true)]
  [string[]]$CliArgs
)

$ErrorActionPreference = 'Stop'
$ScriptDir = Split-Path -Parent $MyInvocation.MyCommand.Path
$SkillDir = Split-Path -Parent $ScriptDir

function Write-CliMissing {
  $message = 'AssetScope CLI not found. Install the app, set ASSETSCOPE_CLI to assetscope.cmd, or set config.json repo / ASSETSCOPE_REPO to the checkout.'
  [Console]::Error.WriteLine((ConvertTo-Json -Compress -InputObject @{ error = @{ code = 'NOT_CONFIGURED'; message = $message } }))
  exit 3
}

function Invoke-CliExecutable([string]$Executable) {
  if ($null -eq $CliArgs) { & $Executable } else { & $Executable @CliArgs }
  exit $LASTEXITCODE
}

function Invoke-Repo([string]$Repo) {
  if (-not $Repo) { return }
  $bin = Join-Path $Repo 'bin\assetscope.cmd'
  if (Test-Path -LiteralPath $bin) { Invoke-CliExecutable $bin }
  $cliTs = Join-Path $Repo 'native-api\src\cli.ts'
  if (Test-Path -LiteralPath $cliTs) {
    Push-Location $Repo
    try {
      if ($null -eq $CliArgs) {
        & node --import tsx/esm 'native-api/src/cli.ts'
      } else {
        & node --import tsx/esm 'native-api/src/cli.ts' @CliArgs
      }
      exit $LASTEXITCODE
    } finally {
      Pop-Location
    }
  }
}

if ($env:ASSETSCOPE_CLI -and (Test-Path -LiteralPath $env:ASSETSCOPE_CLI)) {
  Invoke-CliExecutable $env:ASSETSCOPE_CLI
}

$installed = Join-Path $env:LOCALAPPDATA 'Programs\AssetScope\sidecar\assetscope.cmd'
if ($env:LOCALAPPDATA -and (Test-Path -LiteralPath $installed)) {
  Invoke-CliExecutable $installed
}

if ($env:ASSETSCOPE_REPO) { Invoke-Repo $env:ASSETSCOPE_REPO }

$configPath = Join-Path $SkillDir 'config.json'
if (Test-Path -LiteralPath $configPath) {
  $cfg = Get-Content -LiteralPath $configPath -Raw | ConvertFrom-Json
  if ($cfg.repo) { Invoke-Repo "$($cfg.repo)" }
}

$dir = $SkillDir
while ($dir) {
  if (Test-Path -LiteralPath (Join-Path $dir 'native-api\src\cli.ts')) {
    Invoke-Repo $dir
  }
  $parent = Split-Path -Parent $dir
  if (-not $parent -or $parent -eq $dir) { break }
  $dir = $parent
}

Write-CliMissing
