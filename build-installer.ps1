$ErrorActionPreference = "Stop"

$root = Split-Path -Parent $MyInvocation.MyCommand.Path
Set-Location $root

Write-Host "1/2 Publicando aplicativo principal (single-file)..." -ForegroundColor Cyan
Write-Host "1/3 Publicando aplicativo principal (single-file)..." -ForegroundColor Cyan
dotnet publish .\KSnakeBatteryTray.csproj `
    -c Release `
    -r win-x64 `
    --self-contained false `
    -p:PublishSingleFile=true `
    -p:IncludeNativeLibrariesForSelfExtract=true `
    -p:DebugType=None `
    -p:DebugSymbols=false `
    -o .\Installer\Payload `
    --nologo
if ($LASTEXITCODE -ne 0) { throw "Falha ao publicar KSnakeBatteryTray." }

Write-Host "2/2 Compilando instalador WirelessBatterySetup.exe..." -ForegroundColor Cyan
Write-Host "2/3 Comprimindo payload com Brotli..." -ForegroundColor Cyan
$srcExe = Join-Path $root "Installer\Payload\KSnakeBatteryTray.exe"
$brFile = Join-Path $root "Installer\Payload\KSnakeBatteryTray.exe.br"
$tmpDir = Join-Path $env:TEMP "br_compress_tool"
New-Item -ItemType Directory -Force -Path $tmpDir | Out-Null
@'
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <OutputType>Exe</OutputType>
    <TargetFramework>net10.0</TargetFramework>
    <ImplicitUsings>enable</ImplicitUsings>
  </PropertyGroup>
</Project>
'@ | Set-Content (Join-Path $tmpDir "BrTool.csproj")
@'
using System.IO.Compression;
var src = args[0];
var dst = args[1];
using (var input = File.OpenRead(src))
using (var output = File.Create(dst))
using (var br = new BrotliStream(output, CompressionLevel.SmallestSize))
    input.CopyTo(br);
Console.WriteLine($"Brotli: {new FileInfo(src).Length:N0} -> {new FileInfo(dst).Length:N0} bytes");
'@ | Set-Content (Join-Path $tmpDir "Program.cs")

dotnet run --project (Join-Path $tmpDir "BrTool.csproj") -c Release -- $srcExe $brFile
$brExit = $LASTEXITCODE
Remove-Item $tmpDir -Recurse -Force -ErrorAction SilentlyContinue
if ($brExit -ne 0 -or -not (Test-Path $brFile)) { throw "Falha ao comprimir payload com Brotli." }

Write-Host "3/3 Compilando instalador WirelessBatterySetup.exe..." -ForegroundColor Cyan
dotnet publish .\Installer\WirelessBatterySetup.csproj `
    -c Release `
    -r win-x64 `
    --self-contained false `
    -p:PublishSingleFile=true `
    -p:IncludeNativeLibrariesForSelfExtract=true `
    -p:DebugType=None `
    -p:DebugSymbols=false `
    -o .\release-installer `
    --nologo
if ($LASTEXITCODE -ne 0) { throw "Falha ao publicar WirelessBatterySetup." }

Copy-Item .\release-installer\WirelessBatterySetup.exe .\WirelessBatterySetup.exe -Force
$sizeMb = [Math]::Round((Get-Item .\WirelessBatterySetup.exe).Length / 1MB, 2)
Write-Host ""
Write-Host "Pronto! Instalador gerado em: $root\WirelessBatterySetup.exe" -ForegroundColor Green
Write-Host "Pronto! Instalador gerado em: $root\WirelessBatterySetup.exe ($sizeMb MB)" -ForegroundColor Green
