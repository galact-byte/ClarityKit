<#
.SYNOPSIS
  编译 ClarityKit.IL2CPP（BepInEx 6 / IL2CPP 通用去码插件）并安装到目标游戏。
  IL2CPP 的 interop 程序集与游戏绑定，必须先运行游戏一次生成 interop 再编译。
.PARAMETER GameDir
  目标 IL2CPP 游戏根目录（含 GameAssembly.dll、BepInEx\core、BepInEx\interop）。
.EXAMPLE
  pwsh build/build-il2cpp.ps1 -GameDir "D:\path\to\Il2CppGameRoot"
#>
param(
    [Parameter(Mandatory = $true)]
    [string]$GameDir
)

$ErrorActionPreference = "Stop"

if (-not (Test-Path $GameDir)) { Write-Error "游戏目录不存在: $GameDir"; exit 1 }

$interop = Join-Path $GameDir "BepInEx\interop"
$core = Join-Path $GameDir "BepInEx\core"
$plugins = Join-Path $GameDir "BepInEx\plugins"

if (-not (Test-Path (Join-Path $GameDir "GameAssembly.dll"))) {
    Write-Warning "未找到 GameAssembly.dll，请确认这是 IL2CPP 游戏。"
}
if (-not (Test-Path $core)) { Write-Error "未找到 BepInEx\core，请先安装 BepInEx 6 (IL2CPP): $core"; exit 1 }
if (-not (Test-Path $interop)) {
    Write-Error "未找到 $interop`n请先启动游戏一次，让 BepInEx 生成 interop 程序集后再编译。"
    exit 1
}
if (-not (Test-Path $plugins)) { New-Item -ItemType Directory -Path $plugins | Out-Null }

$proj = Join-Path $PSScriptRoot "..\src\ClarityKit.IL2CPP\ClarityKit.IL2CPP.csproj"

Write-Host "[ClarityKit] 编译 IL2CPP 版（引用目标游戏 interop）..." -ForegroundColor Cyan
dotnet build $proj -c Release -p:GameDir="$GameDir"
if ($LASTEXITCODE -ne 0) { Write-Error "编译失败"; exit 1 }

$dll = Join-Path $PSScriptRoot "..\src\ClarityKit.IL2CPP\bin\Release\ClarityKit.IL2CPP.dll"
Copy-Item $dll (Join-Path $plugins "ClarityKit.IL2CPP.dll") -Force
Write-Host "[ClarityKit] 已安装 ClarityKit.IL2CPP.dll -> $plugins" -ForegroundColor Green
