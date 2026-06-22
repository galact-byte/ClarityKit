<#
.SYNOPSIS
  Create a clean ClarityKit portable zip package.
.PARAMETER Version
  Package version suffix. Defaults to git tag/short SHA when available.
.PARAMETER OutputDir
  Directory where the zip will be written.
#>
param(
    [string]$Version = "",
    [string]$OutputDir = "dist"
)

$ErrorActionPreference = "Stop"

$repoRoot = Resolve-Path (Join-Path $PSScriptRoot "..")
$repoRootPath = $repoRoot.Path

function Assert-InRepoPath([string]$Path) {
    $fullPath = [System.IO.Path]::GetFullPath($Path)
    $rootWithSeparator = $repoRootPath.TrimEnd('\') + '\'
    if ($fullPath -ne $repoRootPath -and -not $fullPath.StartsWith($rootWithSeparator, [System.StringComparison]::OrdinalIgnoreCase)) {
        throw "Refusing to operate outside repository: $fullPath"
    }
    return $fullPath
}

function Remove-InRepoTreeIfPresent {
    param(
        [string]$Path,
        [switch]$WarnOnly
    )

    $fullPath = Assert-InRepoPath $Path
    if (-not (Test-Path -LiteralPath $fullPath)) {
        return
    }

    for ($attempt = 1; $attempt -le 5; $attempt++) {
        try {
            Remove-Item -LiteralPath $fullPath -Recurse -Force
            return
        } catch {
            if ($attempt -eq 5) {
                if ($WarnOnly) {
                    Write-Warning "Could not remove temporary directory: $fullPath"
                    Write-Warning $_.Exception.Message
                    return
                }
                throw
            }
            Start-Sleep -Milliseconds (200 * $attempt)
        }
    }
}

if ([string]::IsNullOrWhiteSpace($Version)) {
    $tag = git -C $repoRootPath describe --tags --exact-match 2>$null
    if ($LASTEXITCODE -eq 0 -and -not [string]::IsNullOrWhiteSpace($tag)) {
        $Version = $tag.Trim()
    } else {
        $sha = git -C $repoRootPath rev-parse --short HEAD 2>$null
        if ($LASTEXITCODE -eq 0 -and -not [string]::IsNullOrWhiteSpace($sha)) {
            $Version = $sha.Trim()
        } else {
            $Version = (Get-Date -Format "yyyyMMdd-HHmmss")
        }
    }
}

$safeVersion = $Version -replace '[^A-Za-z0-9._-]', '-'
$packageName = "ClarityKit-$safeVersion"

$outputPath = Assert-InRepoPath ([System.IO.Path]::GetFullPath((Join-Path $repoRootPath $OutputDir)))
$stageRoot = Assert-InRepoPath (Join-Path $outputPath "_stage\package-$([System.Guid]::NewGuid().ToString('N'))")
$stageDir = Assert-InRepoPath (Join-Path $stageRoot $packageName)
$zipPath = Assert-InRepoPath (Join-Path $outputPath "$packageName.zip")

New-Item -ItemType Directory -Force -Path $outputPath | Out-Null

if (Test-Path -LiteralPath $zipPath) {
    $baseZipPath = $zipPath
    $zipPath = $null
    for ($i = 1; $i -le 999; $i++) {
        $candidate = Assert-InRepoPath (Join-Path $outputPath "$packageName-$i.zip")
        if (-not (Test-Path -LiteralPath $candidate)) {
            $zipPath = $candidate
            Write-Warning "Package already exists: $baseZipPath"
            Write-Warning "Writing package to: $zipPath"
            break
        }
    }
    if (-not $zipPath) {
        throw "Could not find an available package filename for $packageName"
    }
}

New-Item -ItemType Directory -Force -Path $stageDir | Out-Null

$rootFiles = @("README.md", "LICENSE", "start.bat")
foreach ($file in $rootFiles) {
    $source = Join-Path $repoRootPath $file
    if (-not (Test-Path -LiteralPath $source)) {
        throw "Required file is missing: $file"
    }
    Copy-Item -LiteralPath $source -Destination $stageDir -Force
}

$toolSource = Join-Path $repoRootPath "tool"
$toolDest = Join-Path $stageDir "tool"
if (-not (Test-Path -LiteralPath $toolSource)) {
    throw "Required directory is missing: tool"
}

$excludedDirs = @("__pycache__", ".pytest_cache")
Get-ChildItem -LiteralPath $toolSource -Recurse -Force | ForEach-Object {
    $relative = [System.IO.Path]::GetRelativePath($toolSource, $_.FullName)
    $parts = $relative -split '[\\/]'
    if ($parts | Where-Object { $excludedDirs -contains $_ }) {
        return
    }

    $target = Join-Path $toolDest $relative
    if ($_.PSIsContainer) {
        New-Item -ItemType Directory -Force -Path $target | Out-Null
    } else {
        New-Item -ItemType Directory -Force -Path (Split-Path -Parent $target) | Out-Null
        Copy-Item -LiteralPath $_.FullName -Destination $target -Force
    }
}

$requiredAssets = @(
    "tool\assets\ClarityKit.Mono.dll",
    "tool\assets\il2cpp_src\ClarityKit.IL2CPP.csproj",
    "tool\assets\il2cpp_src\Plugin.cs",
    "tool\assets\il2cpp_src\Keywords.cs"
)

foreach ($asset in $requiredAssets) {
    $assetPath = Join-Path $stageDir $asset
    if (-not (Test-Path -LiteralPath $assetPath)) {
        throw "Package is missing required asset: $asset"
    }
}

Compress-Archive -LiteralPath $stageDir -DestinationPath $zipPath -CompressionLevel Optimal
Remove-InRepoTreeIfPresent -Path $stageRoot -WarnOnly

Write-Host "Created package: $zipPath"
