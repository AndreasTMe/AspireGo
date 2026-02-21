param(
    [switch]$NoProto
)

$ErrorActionPreference = "Stop"
$RootDir = Resolve-Path (Join-Path $PSScriptRoot "..")

if (-not $NoProto) {
  if (-not (Get-Command protoc -ErrorAction SilentlyContinue)) {
    throw "protoc not found on PATH"
  }

  Write-Host '==> ' -ForegroundColor Green -NoNewline; Write-Host "Generating Go code from proto files"
  
  Push-Location "$RootDir\.proto"
  try {
      protoc `
        --go_out=../workers --go_opt=module=main `
        --go-grpc_out=../workers --go-grpc_opt=module=main `
        *.proto
  }
  finally {
      Pop-Location
  }
  
  Write-Host "Generation completed"
} else {
  Write-Host '==> ' -ForegroundColor Green -NoNewline; Write-Host "Skipping proto generation"
}