# PCL-Injector 一键发布脚本
# 产物: dist\PCL-Injector.exe (单文件自包含, 可直接分享)
$ErrorActionPreference = "Stop"
Set-Location (Split-Path -Parent $MyInvocation.MyCommand.Path)

Write-Host "== [1/3] Build Payload ==" -ForegroundColor Cyan
dotnet build "PCL.Injector.Payload\PCL.Injector.Payload.csproj" -c Release
if ($LASTEXITCODE -ne 0) { exit 1 }

Write-Host "== [2/3] Build hmcl-agent.jar ==" -ForegroundColor Cyan
& javac -d "hmcl-agent\build\classes" "hmcl-agent\Agent.java"
if ($LASTEXITCODE -ne 0) { Write-Host "javac failed" -ForegroundColor Red; exit 1 }
& powershell -ExecutionPolicy Bypass -File "hmcl-agent\package.ps1"
if ($LASTEXITCODE -ne 0) { exit 1 }

Write-Host "== [3/3] Publish single-file to dist\ ==" -ForegroundColor Cyan
dotnet publish "PCL.Injector.Cli\PCL.Injector.Cli.csproj" -c Release `
    -r win-x64 --self-contained true `
    /p:PublishSingleFile=true `
    /p:EnableCompressionInSingleFile=true `
    /p:DebugType=none `
    -o dist
if ($LASTEXITCODE -ne 0) { exit 1 }

Write-Host ""
Write-Host "== Done ==" -ForegroundColor Green
Get-ChildItem dist | ForEach-Object {
    Write-Host ("  {0}  ({1:N1} MB)" -f $_.FullName, ($_.Length / 1MB))
}
