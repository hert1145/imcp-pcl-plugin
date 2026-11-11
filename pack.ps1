param(
    [string]$Tag = "v1.0.0",
    [string]$Owner = "HertaTeam",
    [string]$Repo = "imcp-pcl-plugin",
    [string]$PclLiteDir = $env:PCL_LITE_DIR
)

$ErrorActionPreference = "Stop"
$root = Split-Path -Parent $MyInvocation.MyCommand.Path

if (-not $PclLiteDir) {
    $fallback = Join-Path (Split-Path -Parent $root) "pcl-lite-src"
    if (Test-Path (Join-Path $fallback "PCL.Plugin.SDK\PCL.Plugin.SDK.csproj")) {
        $PclLiteDir = $fallback
    } else {
        throw "pcl-lite source not found. Set PCL_LITE_DIR or pass -PclLiteDir."
    }
}

Write-Host "Building plugin..." -ForegroundColor Cyan
dotnet build (Join-Path $root "PCL.Plugin.ImcProxy.csproj") -c Release "-p:PclLiteDir=$PclLiteDir\" | Out-Host
if ($LASTEXITCODE -ne 0) { throw "build failed" }

$dist = Join-Path $root "dist\imcproxy.control"
Copy-Item (Join-Path $root "bin\Release\net10.0-windows\PCL.Plugin.ImcProxy.dll") $dist -Force
Copy-Item (Join-Path $root "plugin.json") $dist -Force

$assetName = "imcproxy.control-$Tag.pclplugin"
$package = Join-Path $root "dist\$assetName"
if (Test-Path $package) { Remove-Item $package -Force }

Write-Host "Packing $assetName ..." -ForegroundColor Cyan
Add-Type -AssemblyName System.IO.Compression.FileSystem
[System.IO.Compression.ZipFile]::CreateFromDirectory($dist, $package, [System.IO.Compression.CompressionLevel]::Optimal, $false)

$sha256 = (Get-FileHash $package -Algorithm SHA256).Hash.ToLowerInvariant()
$size = (Get-Item $package).Length

$manifest = Get-Content (Join-Path $root "plugin.json") -Raw -Encoding UTF8 | ConvertFrom-Json

$entry = [ordered]@{
    id          = $manifest.id
    name        = "IMCProxy"
    version     = $manifest.version
    author      = $manifest.author
    description = "PCL Lite control panel for IMCProxy-Free: download, run hidden, edit settings."
    packageUrl  = "https://github.com/$Owner/$Repo/releases/download/$Tag/$assetName"
    website     = "https://github.com/$Owner/$Repo"
    sha256      = $sha256
}

Write-Host ""
Write-Host "package: $package ($([math]::Round($size / 1KB, 1)) KB)" -ForegroundColor Green
Write-Host "sha256: $sha256" -ForegroundColor Green
Write-Host ""
Write-Host "Add this entry to the plugins array of plugins/index.json:" -ForegroundColor Yellow
$entry | ConvertTo-Json -Depth 5
