<#
.SYNOPSIS
  编译 ClarityKit.Mono（BepInEx 5 / Mono 通用去码插件）并安装到目标游戏。
.PARAMETER GameDir
  目标 Mono 游戏根目录（含 *_Data\Managed 与 BepInEx\core）。
.EXAMPLE
  pwsh build/build-mono.ps1 -GameDir "D:\path\to\GameRoot"
#>
param(
    [Parameter(Mandatory = $true)]
    [string]$GameDir
)

$ErrorActionPreference = "Stop"

if (-not (Test-Path $GameDir)) { Write-Error "游戏目录不存在: $GameDir"; exit 1 }

# 定位 *_Data\Managed
$dataDir = Get-ChildItem -Path $GameDir -Directory -Filter "*_Data" | Select-Object -First 1
if (-not $dataDir) { Write-Error "未找到 *_Data 目录，可能不是 Unity Mono 游戏: $GameDir"; exit 1 }
$managed = Join-Path $dataDir.FullName "Managed"
$core = Join-Path $GameDir "BepInEx\core"
$plugins = Join-Path $GameDir "BepInEx\plugins"

if (-not (Test-Path (Join-Path $managed "Assembly-CSharp.dll"))) {
    Write-Warning "未找到 Assembly-CSharp.dll，请确认这是 Mono（非 IL2CPP）游戏。"
}
if (-not (Test-Path $core)) { Write-Error "未找到 BepInEx\core，请先安装 BepInEx 5: $core"; exit 1 }
if (-not (Test-Path $plugins)) { New-Item -ItemType Directory -Path $plugins | Out-Null }

$proj = Join-Path $PSScriptRoot "..\src\ClarityKit.Mono\ClarityKit.Mono.csproj"

Write-Host "[ClarityKit] 编译 Mono 版..." -ForegroundColor Cyan
dotnet build $proj -c Release -p:ManagedDir="$managed" -p:BepInExCore="$core"
if ($LASTEXITCODE -ne 0) { Write-Error "编译失败"; exit 1 }

$dll = Join-Path $PSScriptRoot "..\src\ClarityKit.Mono\bin\Release\ClarityKit.Mono.dll"
Copy-Item $dll (Join-Path $plugins "ClarityKit.Mono.dll") -Force
Write-Host "[ClarityKit] 已安装 ClarityKit.Mono.dll -> $plugins" -ForegroundColor Green
