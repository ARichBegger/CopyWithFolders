#requires -Version 5.1
<#
.SYNOPSIS
    一键构建 CopyWithFolders。

.DESCRIPTION
    依次执行：还原 -> 编译（要求零错误零警告）-> 运行核心逻辑测试 ->
    发布自包含单文件 exe。任一步失败立即中止。

.PARAMETER SkipTests
    跳过测试，仅发布（不建议用于正式发布）。

.PARAMETER OutputDir
    发布输出目录，默认 dist。

.EXAMPLE
    pwsh ./build.ps1

.EXAMPLE
    pwsh ./build.ps1 -SkipTests -OutputDir ./out
#>
[CmdletBinding()]
param(
    [switch]$SkipTests,
    [string]$OutputDir = "dist"
)

$ErrorActionPreference = "Stop"
$root = Split-Path -Parent $MyInvocation.MyCommand.Path
Set-Location $root

function Write-Step([string]$text) {
    Write-Host ""
    Write-Host "==> $text" -ForegroundColor Cyan
}

# dotnet 不一定在 PATH 上（例如解压式安装、只把 SDK 装到自定义目录），
# 因此依次尝试：PATH -> DOTNET_ROOT -> 常见安装位置。
function Resolve-DotNet {
    $cmd = Get-Command dotnet -ErrorAction SilentlyContinue
    if ($cmd) { return $cmd.Source }

    $candidates = @()
    if ($env:DOTNET_ROOT) { $candidates += (Join-Path $env:DOTNET_ROOT "dotnet.exe") }
    $candidates += @(
        "$env:ProgramFiles\dotnet\dotnet.exe",
        "${env:ProgramFiles(x86)}\dotnet\dotnet.exe",
        "$env:LOCALAPPDATA\Microsoft\dotnet\dotnet.exe"
    )
    foreach ($c in $candidates) {
        if ($c -and (Test-Path $c)) { return $c }
    }
    throw "未找到 dotnet。请安装 .NET 8 SDK：https://dotnet.microsoft.com/download/dotnet/8.0"
}

$script:DotNet = Resolve-DotNet

function Invoke-DotNet([string[]]$arguments) {
    & $script:DotNet @arguments
    if ($LASTEXITCODE -ne 0) {
        throw "dotnet $($arguments -join ' ') 执行失败，退出码 $LASTEXITCODE"
    }
}

$sdkVersion = (& $script:DotNet --version)
if ($LASTEXITCODE -ne 0) {
    throw "无法读取 .NET SDK 版本，请确认安装的是 SDK 而非仅运行时。"
}
Write-Host "使用 .NET SDK $sdkVersion"
Write-Host "dotnet     : $script:DotNet"

$appProject  = "src/CopyWithFolders/CopyWithFolders.csproj"
$testProject = "tests/CopyLogicTest/CopyLogicTest.csproj"

Write-Step "还原依赖"
Invoke-DotNet @("restore", $appProject)
Invoke-DotNet @("restore", $testProject)

Write-Step "编译（Release）"
Invoke-DotNet @("build", $appProject,  "-c", "Release", "--no-restore", "-v", "minimal")
Invoke-DotNet @("build", $testProject, "-c", "Release", "--no-restore", "-v", "minimal")

if (-not $SkipTests) {
    Write-Step "运行核心逻辑测试"
    Invoke-DotNet @("run", "--project", $testProject, "-c", "Release", "--no-build")
}
else {
    Write-Host "已按参数跳过测试" -ForegroundColor Yellow
}

Write-Step "发布单文件 exe"
if (Test-Path $OutputDir) { Remove-Item $OutputDir -Recurse -Force }
Invoke-DotNet @(
    "publish", $appProject,
    "-c", "Release",
    "-r", "win-x64",
    "--self-contained", "true",
    "-p:PublishSingleFile=true",
    "-p:EnableCompressionInSingleFile=true",
    "-p:IncludeNativeLibrariesForSelfExtract=true",
    "-p:PublishTrimmed=false",
    "-o", $OutputDir
)

$exe = Join-Path $OutputDir "CopyWithFolders.exe"
if (-not (Test-Path $exe)) { throw "未找到构建产物：$exe" }

$item   = Get-Item $exe
$hash   = (Get-FileHash $exe -Algorithm SHA256).Hash
$sizeMb = [math]::Round($item.Length / 1MB, 1)

Write-Host ""
Write-Host "构建完成" -ForegroundColor Green
Write-Host "  文件   : $($item.FullName)"
Write-Host "  大小   : $sizeMb MB"
Write-Host "  SHA256 : $hash"
Write-Host ""
Write-Host "提示：界面排版测试需要交互式桌面，请手动执行"
Write-Host "      dotnet run --project tests/UiLayoutTest -c Release"
