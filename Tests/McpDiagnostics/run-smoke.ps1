param(
    [string]$PluginDirectory,
    [string]$RevitDirectory = (Join-Path $env:ProgramFiles 'Autodesk\Revit 2022')
)

$ErrorActionPreference = 'Stop'
$root = Split-Path (Split-Path $PSScriptRoot -Parent) -Parent
$vswhere = "${env:ProgramFiles(x86)}\Microsoft Visual Studio\Installer\vswhere.exe"
$msbuild = & $vswhere -latest -requires Microsoft.Component.MSBuild -find 'MSBuild\**\Bin\MSBuild.exe' | Select-Object -First 1
$csc = Join-Path (Split-Path $msbuild) 'Roslyn\csc.exe'
$testOutput = Join-Path $env:TEMP ('AerBIM-McpDiagnostics-' + [guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $testOutput | Out-Null
if (!$PluginDirectory) { $PluginDirectory = Join-Path $root "bin\Release R2022" }
Copy-Item -Path (Join-Path $PluginDirectory "*.dll") -Destination $testOutput
Copy-Item -LiteralPath (Join-Path $RevitDirectory "RevitAPI.dll"), (Join-Path $RevitDirectory "RevitAPIUI.dll") -Destination $testOutput
& $csc /nologo /target:exe /platform:x64 "/out:$testOutput\smoke.exe" "/r:$testOutput\EPPlus.dll" "/r:$testOutput\PluginForRevit2022.dll" /r:System.Web.Extensions.dll /r:System.ComponentModel.DataAnnotations.dll "$PSScriptRoot\ReflectionSmoke.cs"
if ($LASTEXITCODE -ne 0) { throw 'MCP diagnostics test compilation failed' }
# Load native and managed Revit dependencies for this test process only.
Set-Content -LiteralPath (Join-Path $testOutput 'smoke.exe.config') -Encoding UTF8 -Value '<configuration><runtime><developmentMode developerInstallation="true" /></runtime></configuration>'
$processInfo = [Diagnostics.ProcessStartInfo]::new()
$processInfo.FileName = Join-Path $testOutput 'smoke.exe'
$processInfo.Arguments = '"' + (Join-Path $testOutput 'output') + '"'
$processInfo.UseShellExecute = $false
$processInfo.EnvironmentVariables['PATH'] = $RevitDirectory + [IO.Path]::PathSeparator + $env:PATH
$processInfo.EnvironmentVariables['DEVPATH'] = $RevitDirectory
$process = [Diagnostics.Process]::Start($processInfo)
try {
    $process.WaitForExit()
    if ($process.ExitCode -ne 0) { throw 'MCP diagnostics tests failed' }
}
finally { $process.Dispose() }
