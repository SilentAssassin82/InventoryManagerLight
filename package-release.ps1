#Requires -Version 5.1
<#
.SYNOPSIS
    Builds InventoryManagerLight in Release, packages it, and creates a GitHub release.
    Requires the GitHub CLI (gh) to be installed and authenticated (gh auth login).

.PARAMETER Notes
    Optional release notes. If omitted, GitHub auto-generates notes from commits since the last tag.

.PARAMETER Draft
    Create the release as a draft instead of publishing it immediately.

.EXAMPLE
    .\package-release.cmd
    .\package-release.cmd -Notes "Fixed assembler queuing precision."
    .\package-release.cmd -Draft
#>
param(
    [string]$Notes = "",
    [switch]$Draft
)

$ErrorActionPreference = "Stop"
Set-StrictMode -Version Latest

$repoRoot    = $PSScriptRoot
$projDir     = Join-Path $repoRoot "InventoryManagerLight"
$projFile    = Join-Path $projDir  "InventoryManagerLight.csproj"
$manifestXml = Join-Path $projDir  "manifest.xml"
$dllPath     = Join-Path $projDir  "bin\Release\InventoryManagerLight.dll"

Write-Host ""
[xml]$xml = Get-Content $manifestXml -Raw
$version  = $xml.PluginManifest.Version.Trim()
$tag      = "v$version"
$zipName  = "InventoryManagerLight-$tag.zip"
$zipPath  = Join-Path $repoRoot $zipName

Write-Host "=== InventoryManagerLight $tag ===" -ForegroundColor Cyan

if (-not (Get-Command gh -ErrorAction SilentlyContinue)) {
    Write-Error "GitHub CLI (gh) not found. Install from https://cli.github.com/ then run: gh auth login"
}

$vswhere = Join-Path ${env:ProgramFiles(x86)} "Microsoft Visual Studio\Installer\vswhere.exe"
if (-not (Test-Path $vswhere)) { Write-Error "vswhere.exe not found. Is Visual Studio installed?" }
$msbuild = & $vswhere -latest -requires Microsoft.Component.MSBuild -find "MSBuild\**\Bin\MSBuild.exe" 2>$null | Select-Object -First 1
if (-not $msbuild -or -not (Test-Path $msbuild)) { Write-Error "MSBuild not found via vswhere." }

Write-Host "`nBuilding Release configuration..." -ForegroundColor Yellow
& $msbuild $projFile /p:Configuration=Release /nologo /verbosity:minimal
if ($LASTEXITCODE -ne 0) { Write-Error "Build failed - aborting." }
Write-Host "Build succeeded." -ForegroundColor Green

Write-Host "`nPackaging $zipName..." -ForegroundColor Yellow
if (Test-Path $zipPath) { Remove-Item $zipPath -Force }

$staging  = Join-Path $env:TEMP "IML-stage-$tag"
if (Test-Path $staging) { Remove-Item $staging -Recurse -Force }
$innerDir = Join-Path $staging "InventoryManagerLight"
New-Item -ItemType Directory $innerDir | Out-Null
Copy-Item $dllPath     $innerDir
Copy-Item $manifestXml $innerDir

Add-Type -AssemblyName System.IO.Compression.FileSystem
[System.IO.Compression.ZipFile]::CreateFromDirectory($staging, $zipPath)
Remove-Item $staging -Recurse -Force

$sizeKb = [math]::Round((Get-Item $zipPath).Length / 1KB, 1)
Write-Host "Created $zipName ($sizeKb KB)" -ForegroundColor Green

$existingTag = git -C $repoRoot tag -l $tag
if ($existingTag) {
    Write-Warning "Tag $tag already exists. To remove: git tag -d $tag && git push origin :refs/tags/$tag"
    exit 1
}

Write-Host "`nCreating GitHub release $tag..." -ForegroundColor Yellow
$ghArgs = @("release", "create", $tag, $zipPath, "--title", "InventoryManagerLight $tag")
if ($Draft)  { $ghArgs += "--draft" }
if ($Notes)  { $ghArgs += @("--notes", $Notes) }
else         { $ghArgs += "--generate-notes" }

& gh @ghArgs
if ($LASTEXITCODE -ne 0) { Write-Error "gh release create failed." }

$releaseWord = if ($Draft) { "Draft release" } else { "Release" }
Write-Host "`n$releaseWord $tag published successfully." -ForegroundColor Green
Write-Host "https://github.com/SilentAssassin82/InventoryManagerLight/releases/tag/$tag`n"
