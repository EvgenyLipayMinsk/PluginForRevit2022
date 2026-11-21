# Проверка экспорта Excel вне Revit. Visual Studio / MSBuild и собранный Release R2022 обязательны.
$ErrorActionPreference = 'Stop'
$root = Split-Path (Split-Path $PSScriptRoot -Parent) -Parent
$vswhere = "${env:ProgramFiles(x86)}\Microsoft Visual Studio\Installer\vswhere.exe"
$msbuild = & $vswhere -latest -requires Microsoft.Component.MSBuild -find 'MSBuild\**\Bin\MSBuild.exe' | Select-Object -First 1
$csc = Join-Path (Split-Path $msbuild) 'Roslyn\csc.exe'
$testOutput = Join-Path $env:TEMP ('AerBIM-JournalForms-' + [guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $testOutput | Out-Null
Copy-Item -Path "$root\bin\Release R2022\*.dll" -Destination $testOutput
& $csc /nologo /target:exe "/out:$testOutput\smoke.exe" "/r:$testOutput\EPPlus.dll" "/r:$testOutput\PluginForRevit2022.dll" /r:System.Drawing.dll /r:System.ComponentModel.DataAnnotations.dll "$PSScriptRoot\FormSmoke.cs"
if ($LASTEXITCODE -ne 0) { throw 'Smoke test compilation failed' }
& "$testOutput\smoke.exe" test "$testOutput\output" $root
if ($LASTEXITCODE -ne 0) { throw 'Smoke tests failed' }
# Изолируем производственный Graph от зависимостей Revit для проверки Дейкстры.
$source = Get-Content (Join-Path $root 'Core\CableJournal\Services\RouteLengthCalculator.cs') -Raw -Encoding UTF8
$graph = [regex]::Match($source, '(?s)        public class Graph\s*\{.*?\r?\n        \}')
if (-not $graph.Success) { throw 'Production Graph class not found' }
$unit = "using System;`nusing System.Collections.Generic;`nusing ARBM.CableJournalExporter.Models;`nnamespace ARBM.CableJournalExporter.Services { public class RouteLengthCalculator {`n" + $graph.Value + "`n} }"
$graphSource = Join-Path $testOutput 'ProductionGraph.cs'
Set-Content -LiteralPath $graphSource -Value $unit -Encoding UTF8
& $csc /nologo /target:exe "/out:$testOutput\graph-smoke.exe" $graphSource "$PSScriptRoot\GraphSmoke.cs" "$root\Core\CableJournal\Models\CableInstallation.cs" "$root\Core\CableJournal\Models\RouteTraceSegment.cs"
if ($LASTEXITCODE -ne 0) { throw 'Graph test compilation failed' }
& "$testOutput\graph-smoke.exe"
if ($LASTEXITCODE -ne 0) { throw 'Graph tests failed' }
Write-Host "Test output: $testOutput\output"
