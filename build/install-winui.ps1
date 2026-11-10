# assetscope - per-user install (no admin required).
# Publishes a self-contained Release build + bundled sidecar with a private
# Node runtime into %LOCALAPPDATA%\Programs, creates a Start-menu shortcut,
# and registers an uninstall entry so Windows lists it under Installed apps.
#
#   powershell -ExecutionPolicy Bypass -File build\install-winui.ps1
#   powershell -ExecutionPolicy Bypass -File build\install-winui.ps1 -Uninstall

param([switch]$Uninstall)

$ErrorActionPreference = 'Stop'
$RepoRoot = Split-Path -Parent $PSScriptRoot
$InstallDir = Join-Path $env:LOCALAPPDATA 'Programs\AssetScope'
$ShortcutPath = Join-Path $env:APPDATA 'Microsoft\Windows\Start Menu\Programs\AssetScope.lnk'
$UninstallKey = 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Uninstall\assetscope'
$RunKey = 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Run'
$RunValueName = 'assetscope'
$Version = '1.0.0'
$LegacyInstallDir = Join-Path $env:LOCALAPPDATA 'Programs\fungible-winui'
$LegacyShortcutPath = Join-Path $env:APPDATA 'Microsoft\Windows\Start Menu\Programs\fungible-winui.lnk'
$LegacyUninstallKey = 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Uninstall\fungible-winui'
$LegacyRunValueName = 'fungible-winui'

function Remove-DailyTask {
    Start-Process -FilePath 'schtasks.exe' `
        -ArgumentList '/Delete', '/F', '/TN', 'assetscope-daily-sync' `
        -WindowStyle Hidden -Wait | Out-Null
}

function Remove-LegacyRegistration {
    Get-Process -Name 'fungible-winui' -ErrorAction SilentlyContinue | Stop-Process -Force -ErrorAction SilentlyContinue
    Start-Process -FilePath 'schtasks.exe' `
        -ArgumentList '/Delete', '/F', '/TN', 'fungible-winui-daily-sync' `
        -WindowStyle Hidden -Wait | Out-Null
    if (Test-Path -LiteralPath $LegacyInstallDir) { Remove-Item -LiteralPath $LegacyInstallDir -Recurse -Force }
    if (Test-Path -LiteralPath $LegacyShortcutPath) { Remove-Item -LiteralPath $LegacyShortcutPath -Force }
    if (Test-Path -LiteralPath $LegacyUninstallKey) { Remove-Item -LiteralPath $LegacyUninstallKey -Recurse -Force }
    Remove-ItemProperty -Path $RunKey -Name $LegacyRunValueName -ErrorAction SilentlyContinue
}

function Remove-Install {
    Get-Process -Name 'assetscope' -ErrorAction SilentlyContinue | Stop-Process -Force -ErrorAction SilentlyContinue
    Remove-DailyTask
    Start-Sleep -Seconds 1
    if (Test-Path $InstallDir) { Remove-Item -Recurse -Force $InstallDir }
    if (Test-Path $ShortcutPath) { Remove-Item -Force $ShortcutPath }
    if (Test-Path $UninstallKey) { Remove-Item -Recurse -Force $UninstallKey }
    Remove-ItemProperty -Path $RunKey -Name $RunValueName -ErrorAction SilentlyContinue
    Write-Host 'AssetScope removed. Your financial data (%USERPROFILE%\.assetscope) was kept.'
}

# The assistant skill documents this build's CLI, so it is refreshed with every
# install. A copy left behind keeps describing the commands of an older build.
function Install-Skill {
    $source = Join-Path $RepoRoot '.claude\skills\assetscope'
    $claudeHome = Join-Path $env:USERPROFILE '.claude'
    if (-not (Test-Path -LiteralPath $source) -or -not (Test-Path -LiteralPath $claudeHome)) { return $null }
    $target = Join-Path $claudeHome 'skills\assetscope'
    New-Item -ItemType Directory -Force (Join-Path $target 'scripts') | Out-Null
    Copy-Item -Force (Join-Path $source 'SKILL.md') $target
    Copy-Item -Force (Join-Path $source 'scripts\*') (Join-Path $target 'scripts')
    # The launcher falls back to this checkout when the installed CLI is missing.
    [IO.File]::WriteAllText((Join-Path $target 'config.json'), (@{ repo = $RepoRoot } | ConvertTo-Json) + "`n")
    return $target
}

if ($Uninstall) { Remove-Install; exit 0 }

# 1. Bundle the sidecar (single ESM file; libsql family stays external for its native addon)
Write-Host '[1/5] Bundling sidecar...'
$SidecarOut = Join-Path $RepoRoot 'dist\sidecar'
if (Test-Path $SidecarOut) { Remove-Item -Recurse -Force $SidecarOut }
New-Item -ItemType Directory -Force $SidecarOut | Out-Null
Push-Location $RepoRoot
try {
    $esbuild = Join-Path $RepoRoot 'node_modules\.bin\esbuild.cmd'
    & $esbuild native-api/src/main.ts --bundle --platform=node --target=node22 --format=esm `
        --outfile=dist/sidecar/sidecar.mjs `
        --external:@libsql/client --external:libsql `
        "--banner:js=import { createRequire } from 'node:module'; const require = createRequire(import.meta.url);"
    if ($LASTEXITCODE -ne 0) { throw 'esbuild failed' }

    & $esbuild native-api/src/cli.ts --bundle --platform=node --target=node22 --format=esm `
        --outfile=dist/sidecar/assetscope.mjs `
        --external:@libsql/client --external:libsql `
        "--banner:js=import { createRequire } from 'node:module'; const require = createRequire(import.meta.url);"
    if ($LASTEXITCODE -ne 0) { throw 'esbuild failed (cli)' }
}
finally { Pop-Location }

# Runtime node_modules for the external libsql family (JS + the win32-x64 native addon).
$RuntimeDeps = @('@libsql', 'libsql', 'js-base64', 'promise-limit', '@neon-rs', 'detect-libc', 'ws')
$SidecarModules = Join-Path $SidecarOut 'node_modules'
New-Item -ItemType Directory -Force $SidecarModules | Out-Null
foreach ($dep in $RuntimeDeps) {
    $src = Join-Path $RepoRoot "node_modules\$dep"
    if (Test-Path $src) { Copy-Item -Recurse -Force $src (Join-Path $SidecarModules $dep) }
}

# Private Node runtime: the installed app must not depend on PATH. Ask node
# for its own path, since `node` on PATH can be a version-manager shim.
$NodeSource = & node -p 'process.execPath'
if ($LASTEXITCODE -ne 0 -or -not (Test-Path -LiteralPath $NodeSource)) { throw 'Could not locate the Node.js runtime.' }
Copy-Item -Force $NodeSource (Join-Path $SidecarOut 'node.exe')

# CLI shim: resolves node.exe and assetscope.mjs by its own location so it
# never depends on PATH, regardless of where the installer places this folder.
@'
@echo off
"%~dp0node.exe" "%~dp0assetscope.mjs" %*
'@ | Set-Content (Join-Path $SidecarOut 'assetscope.cmd') -Encoding ASCII

# 2. Smoke-test the bundle before shipping it
Write-Host '[2/5] Smoke-testing bundle...'
$TestDataDir = Join-Path $env:TEMP ("assetscope-bundle-test-" + [guid]::NewGuid().ToString('N').Substring(0, 8))
$TestOut = Join-Path $env:TEMP 'assetscope-bundle-test-out.txt'
$TestErr = Join-Path $env:TEMP 'assetscope-bundle-test-err.txt'
Remove-Item -LiteralPath $TestOut,$TestErr -Force -ErrorAction SilentlyContinue
$proc = Start-Process -FilePath (Join-Path $SidecarOut 'node.exe') `
    -ArgumentList 'sidecar.mjs', '--port', '0', '--mode', 'demo', '--data-dir', $TestDataDir `
    -WorkingDirectory $SidecarOut -PassThru -NoNewWindow `
    -RedirectStandardOutput $TestOut `
    -RedirectStandardError $TestErr
$deadline = [DateTime]::UtcNow.AddSeconds(20)
$handshake = $null
do {
    Start-Sleep -Milliseconds 250
    $handshake = Get-Content $TestOut -ErrorAction SilentlyContinue | Select-Object -First 1
} while (-not ($handshake -match '"event":"ready"') -and -not $proc.HasExited -and [DateTime]::UtcNow -lt $deadline)
if (-not $proc.HasExited) { Stop-Process -Id $proc.Id -Force -ErrorAction SilentlyContinue }
# A killed sidecar still holds its files until it has fully exited; deleting
# sooner silently left an empty test folder behind on every install.
$proc.WaitForExit(10000) | Out-Null
Remove-Item -Recurse -Force $TestDataDir -ErrorAction SilentlyContinue
$smokeErr = Get-Content $TestErr -ErrorAction SilentlyContinue | Select-Object -First 10
Remove-Item -LiteralPath $TestOut,$TestErr -Force -ErrorAction SilentlyContinue
if (-not ($handshake -match '"event":"ready"')) {
    $smokeErr
    throw "Sidecar bundle smoke test failed: $handshake"
}
Write-Host '      bundle handshake OK'

$CliTestDataDir = Join-Path $env:TEMP ("assetscope-cli-test-" + [guid]::NewGuid().ToString('N').Substring(0, 8))
$cliOut = & (Join-Path $SidecarOut 'assetscope.cmd') doctor --demo --data-dir $CliTestDataDir 2>$null
Remove-Item -Recurse -Force $CliTestDataDir -ErrorAction SilentlyContinue
if ($LASTEXITCODE -ne 0 -or -not ($cliOut -match '"nativeSchemaVersion"')) {
    throw "CLI bundle smoke test failed: $cliOut"
}
Write-Host '      cli bundle OK'

# 3. Publish the app (self-contained Release)
Write-Host '[3/5] Publishing app...'
$PublishDir = Join-Path $RepoRoot 'dist\app'
if (Test-Path $PublishDir) { Remove-Item -Recurse -Force $PublishDir }
dotnet publish (Join-Path $RepoRoot 'winui\AssetScope.App\AssetScope.App.csproj') `
    -c Release -r win-x64 -p:Platform=x64 --self-contained -o $PublishDir --nologo -v q
if ($LASTEXITCODE -ne 0) { throw 'dotnet publish failed' }

# 4. Install
Write-Host '[4/5] Installing...'
$wasRunning = [bool](Get-Process -Name 'assetscope' -ErrorAction SilentlyContinue)
Get-Process -Name 'assetscope' -ErrorAction SilentlyContinue | Stop-Process -Force -ErrorAction SilentlyContinue
Remove-DailyTask
Remove-LegacyRegistration
Start-Sleep -Seconds 1
if (Test-Path $InstallDir) { Remove-Item -Recurse -Force $InstallDir }
New-Item -ItemType Directory -Force $InstallDir | Out-Null
Copy-Item -Recurse -Force "$PublishDir\*" $InstallDir
Copy-Item -Recurse -Force $SidecarOut (Join-Path $InstallDir 'sidecar')
Copy-Item -Force (Join-Path $RepoRoot 'THIRD_PARTY_NOTICES.txt') $InstallDir
Copy-Item -Force (Join-Path $RepoRoot 'LICENSE') $InstallDir

# Uninstaller inside the install dir (referenced by the registry entry).
@"
`$dir = '$InstallDir'
Get-Process -Name 'assetscope' -ErrorAction SilentlyContinue | Stop-Process -Force -ErrorAction SilentlyContinue
Start-Process -FilePath 'schtasks.exe' -ArgumentList '/Delete', '/F', '/TN', 'assetscope-daily-sync' -WindowStyle Hidden -Wait | Out-Null
Start-Sleep -Seconds 1
Remove-Item -Force '$ShortcutPath' -ErrorAction SilentlyContinue
Remove-Item -Recurse -Force '$UninstallKey' -ErrorAction SilentlyContinue
Remove-ItemProperty -Path '$RunKey' -Name '$RunValueName' -ErrorAction SilentlyContinue
Write-Host 'AssetScope removed. Your financial data (%USERPROFILE%\.assetscope) was kept.'
Start-Process powershell -ArgumentList '-NoProfile', '-Command', "Start-Sleep 2; Remove-Item -Recurse -Force '`$dir'" -WindowStyle Hidden
"@ | Set-Content (Join-Path $InstallDir 'uninstall.ps1') -Encoding UTF8

# 5. Start-menu shortcut + Installed-apps entry
Write-Host '[5/5] Registering...'
$shell = New-Object -ComObject WScript.Shell
$shortcut = $shell.CreateShortcut($ShortcutPath)
$shortcut.TargetPath = Join-Path $InstallDir 'assetscope.exe'
$shortcut.WorkingDirectory = $InstallDir
$shortcut.Description = 'AssetScope — local-first net worth overview'
$shortcut.Save()

$sizeKb = [int]((Get-ChildItem -Recurse $InstallDir | Measure-Object Length -Sum).Sum / 1KB)
New-Item -Path $UninstallKey -Force | Out-Null
Set-ItemProperty $UninstallKey 'DisplayName' 'AssetScope'
Set-ItemProperty $UninstallKey 'DisplayVersion' $Version
Set-ItemProperty $UninstallKey 'Publisher' 'AssetScope'
Set-ItemProperty $UninstallKey 'InstallLocation' $InstallDir
Set-ItemProperty $UninstallKey 'DisplayIcon' (Join-Path $InstallDir 'assetscope.exe')
Set-ItemProperty $UninstallKey 'UninstallString' "powershell.exe -ExecutionPolicy Bypass -File `"$InstallDir\uninstall.ps1`""
Set-ItemProperty $UninstallKey 'NoModify' 1 -Type DWord
Set-ItemProperty $UninstallKey 'NoRepair' 1 -Type DWord
Set-ItemProperty $UninstallKey 'EstimatedSize' $sizeKb -Type DWord

# The unpackaged shell starts hidden after sign-in and owns the tray icon.
New-Item -Path $RunKey -Force | Out-Null
$installedExe = Join-Path $InstallDir 'assetscope.exe'
Set-ItemProperty -Path $RunKey -Name $RunValueName -Value "`"$installedExe`" --background"

$skillDir = Install-Skill

# The install stopped the tray app and removed its daily sync task. Start it
# again the way sign-in does, so both are back without waiting for a sign-in.
if ($wasRunning) { Start-Process -FilePath $installedExe -ArgumentList '--background' }

Write-Host ''
Write-Host "Installed to $InstallDir"
if ($skillDir) { Write-Host "Assistant skill updated in $skillDir" }
Write-Host 'Launch from the Start menu: "AssetScope". Uninstall via Settings > Apps.'
