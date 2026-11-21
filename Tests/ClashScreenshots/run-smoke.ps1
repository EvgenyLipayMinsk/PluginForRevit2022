$ErrorActionPreference = 'Stop'
$root = Split-Path (Split-Path $PSScriptRoot -Parent) -Parent
$vswhere = "${env:ProgramFiles(x86)}\Microsoft Visual Studio\Installer\vswhere.exe"
$msbuild = & $vswhere -latest -requires Microsoft.Component.MSBuild -find 'MSBuild\**\Bin\MSBuild.exe' | Select-Object -First 1
$csc = Join-Path (Split-Path $msbuild) 'Roslyn\csc.exe'
$testOutput = Join-Path $env:TEMP ('AerBIM-ClashScreenshots-' + [guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $testOutput | Out-Null
Copy-Item -Path "$root\bin\Release R2022\*.dll" -Destination $testOutput
$framework = Join-Path ${env:ProgramFiles(x86)} 'Reference Assemblies\Microsoft\Framework\.NETFramework\v4.8'
$sources = @('NavisReport.cs','ClashStatusStore.cs','ClashScreenshot.cs','ClashExcel.cs') | ForEach-Object { Join-Path $root "Core\Clashes\$_" }
& $csc /nologo /target:exe /platform:x64 "/out:$testOutput\smoke.exe" "/r:$testOutput\EPPlus.dll" /r:System.Drawing.dll /r:System.Xml.Linq.dll /r:System.Web.Extensions.dll /r:System.IO.Compression.dll /r:System.IO.Compression.FileSystem.dll /r:System.ComponentModel.DataAnnotations.dll "/r:$framework\PresentationCore.dll" "/r:$framework\WindowsBase.dll" "/r:$framework\PresentationFramework.dll" "$PSScriptRoot\Smoke.cs" $sources
if ($LASTEXITCODE -ne 0) { throw 'Screenshot smoke compilation failed' }
& "$testOutput\smoke.exe" "$testOutput\output"
if ($LASTEXITCODE -ne 0) { throw 'Screenshot smoke tests failed' }
