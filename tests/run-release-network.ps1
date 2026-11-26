param(
    [string]$Executable = (Join-Path $PSScriptRoot '..\bin\Release\KimNotes.exe'),
    [switch]$Online
)
$ErrorActionPreference = 'Stop'
$candidate = (Resolve-Path -LiteralPath $Executable).Path
$runRoot = Join-Path $env:TEMP ('KimNotes-release-network-' + [guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $runRoot | Out-Null
$csc = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319\csc.exe'
$runner = Join-Path $runRoot 'ReleaseNetworkRegression.exe'
& $csc /nologo /target:exe "/out:$runner" /reference:System.Net.Http.dll /reference:System.Xml.Linq.dll (Join-Path $PSScriptRoot 'ReleaseNetworkRegression.cs')
if ($LASTEXITCODE -ne 0) { throw 'Release network test compilation failed.' }
$testArgs = @($candidate)
if ($Online) { $testArgs += '--online' }
& $runner @testArgs
if ($LASTEXITCODE -ne 0) { throw "Release network regression failed. Artifacts: $runRoot" }
Write-Output "Release network checks passed. Artifacts: $runRoot"
