param(
    [switch]$NoProto
)

$ErrorActionPreference = "Stop"
$RootDir = Resolve-Path (Join-Path $PSScriptRoot "..")

. (Join-Path $PSScriptRoot "build-all.ps1") -NoProto:$NoProto

Write-Host "`n==> " -ForegroundColor Green -NoNewline; Write-Host "Running Aspire Host"
aspire run --project "$RootDir\aspire-host\AspireHost.csproj"