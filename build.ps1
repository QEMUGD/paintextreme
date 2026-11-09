$ErrorActionPreference = "Stop"
$project = Join-Path $PSScriptRoot "PaintAutoDrawer256.csproj"
dotnet publish $project -c Release -r win-x64 --self-contained true `
  /p:PublishSingleFile=true /p:IncludeNativeLibrariesForSelfExtract=true
if ($LASTEXITCODE -ne 0) { throw "Build failed." }
Write-Host ""
Write-Host "Build complete:"
Write-Host (Join-Path $PSScriptRoot "bin\Release\net8.0-windows\win-x64\publish\PaintAutoDrawer256.exe")
