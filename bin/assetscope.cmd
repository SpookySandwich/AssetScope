@echo off
setlocal EnableExtensions
set "ROOT=%~dp0.."
pushd "%ROOT%" >nul
node --import tsx/esm "native-api\src\cli.ts" %*
set "CODE=%ERRORLEVEL%"
popd >nul
exit /b %CODE%
