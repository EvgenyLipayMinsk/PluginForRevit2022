# Собирает надстройку под все версии Revit и общий MSI: Installer\out\AerBIM.Manager-<версия>.msi
# Версия берётся из AssemblyFileVersion в Properties\AssemblyInfo.cs.
# На машине сборки нужны Revit 2021-2024 (библиотеки API), Visual Studio, .NET SDK и Node.js.
$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot -Parent

$info = Get-Content (Join-Path $root 'Properties\AssemblyInfo.cs') -Raw -Encoding UTF8
$match = [regex]::Match($info, '(?m)^\[assembly: AssemblyFileVersion\("([\d.]+)"\)\]')
if (-not $match.Success) { throw 'AssemblyFileVersion не найдена.' }
$version = $match.Groups[1].Value

$vswhere = "${env:ProgramFiles(x86)}\Microsoft Visual Studio\Installer\vswhere.exe"
$msbuild = & $vswhere -latest -requires Microsoft.Component.MSBuild -find 'MSBuild\**\Bin\MSBuild.exe' | Select-Object -First 1
if (-not $msbuild) { throw 'MSBuild не найден.' }

foreach ($revit in 2021, 2022, 2023, 2024) {
    Write-Host "== Сборка Release R$revit"
    & $msbuild (Join-Path $root 'PluginForRevit2022.csproj') "/p:Configuration=Release R$revit" /p:Platform=AnyCPU /t:Rebuild /v:m /nologo
    if ($LASTEXITCODE -ne 0) { throw "Ошибка сборки R$revit" }
}

Write-Host '== Зависимости MCP-сервера'
Push-Location (Join-Path $root 'McpServer')
try {
    npm ci --omit=dev
    if ($LASTEXITCODE -ne 0) { throw 'Ошибка npm ci' }
} finally { Pop-Location }

Write-Host "== MSI $version"
dotnet build (Join-Path $PSScriptRoot 'AerBIM.Manager.Installer.wixproj') -c Release "-p:ProductVersion=$version" --nologo -v m
if ($LASTEXITCODE -ne 0) { throw 'Ошибка сборки MSI' }

Get-ChildItem (Join-Path $PSScriptRoot 'out') -Recurse -Filter *.msi | Select-Object FullName, Length
