$ErrorActionPreference = 'Stop'
$root = Split-Path (Split-Path $PSScriptRoot -Parent) -Parent
$vswhere = "${env:ProgramFiles(x86)}\Microsoft Visual Studio\Installer\vswhere.exe"
$msbuild = & $vswhere -latest -requires Microsoft.Component.MSBuild -find 'MSBuild\**\Bin\MSBuild.exe' | Select-Object -First 1
$csc = Join-Path (Split-Path $msbuild) 'Roslyn\csc.exe'
$testOutput = Join-Path $env:TEMP ('AerBIM-McpDiagnostics-' + [guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $testOutput | Out-Null
Copy-Item -Path "$root\bin\Release R2022\*.dll" -Destination $testOutput
Copy-Item -LiteralPath 'C:\Program Files\Autodesk\Revit 2022\RevitAPI.dll','C:\Program Files\Autodesk\Revit 2022\RevitAPIUI.dll' -Destination $testOutput
& $csc /nologo /target:exe /platform:x64 "/out:$testOutput\smoke.exe" "/r:$testOutput\EPPlus.dll" "/r:$testOutput\PluginForRevit2022.dll" /r:System.Web.Extensions.dll /r:System.ComponentModel.DataAnnotations.dll "$PSScriptRoot\ReflectionSmoke.cs"
if ($LASTEXITCODE -ne 0) { throw 'MCP diagnostics test compilation failed' }
& "$testOutput\smoke.exe" "$testOutput\output"
if ($LASTEXITCODE -ne 0) { throw 'MCP diagnostics tests failed' }
