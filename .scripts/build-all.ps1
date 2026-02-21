param(
    [switch]$NoProto
)

$ErrorActionPreference = "Stop"
$RootDir = Resolve-Path (Join-Path $PSScriptRoot "..")

. (Join-Path $PSScriptRoot "build-proto.ps1") -NoProto:$NoProto

Write-Host "`n==> " -ForegroundColor Green -NoNewline; Write-Host "Building Orchestrator"
dotnet build "$RootDir\orchestrator\Orchestrator.csproj"

Write-Host "`n==> " -ForegroundColor Green -NoNewline; Write-Host "Building Aspire Host"
dotnet build "$RootDir\aspire-host\AspireHost.csproj"
